using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PurpleGlass.Adapters.Telephony.Fake;
using PurpleGlass.Eventing;
using PurpleGlass.Integrations.Worker;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Contracts;
using PurpleGlass.Modules.CallManagement.Domain;
using PurpleGlass.Modules.CallManagement.Infrastructure;

namespace PurpleGlass.IntegrationTests;

[Collection(DurablePathGroup.Name)]
public sealed class WorkerResilienceTests(DurablePathFixture fixture)
{
    [Fact]
    public async Task CompletionConflictRetriesWithoutRepeatingProviderAction()
    {
        TestHarness harness = await CreateHarnessAsync();
        harness.Faults.FailSaves.Add(2);

        Assert.True(await harness.Processor.ProcessNextAsync(default));

        Assert.Single(harness.Provider.Calls);
        await using CallManagementDbContext verify = fixture.CreateCalls();
        Assert.Equal(TelephonyOperationState.Completed,
            (await verify.TelephonyOperations.SingleAsync(operation => operation.Id == harness.OperationId)).State);
    }

    [Fact]
    public async Task ConcurrentlyCompletedOperationConvergesWithoutRepeatingProviderAction()
    {
        TestHarness harness = await CreateHarnessAsync();
        harness.Faults.FailSaves.Add(2);
        harness.Faults.BeforeFailure = async cancellationToken =>
        {
            await using CallManagementDbContext concurrent = fixture.CreateCalls();
            TelephonyOperation operation = await concurrent.TelephonyOperations.SingleAsync(
                item => item.Id == harness.OperationId, cancellationToken);
            operation.Complete(TimeProvider.System.GetUtcNow());
            await concurrent.SaveChangesAsync(cancellationToken);
        };

        Assert.True(await harness.Processor.ProcessNextAsync(default));
        Assert.Single(harness.Provider.Calls);
    }

    [Fact]
    public async Task ConcurrentTerminalCallStateIsNotOverwrittenByCompletionRecovery()
    {
        TestHarness harness = await CreateHarnessAsync();
        harness.Faults.FailSaves.Add(2);
        harness.Faults.BeforeFailure = async cancellationToken =>
        {
            await using CallManagementDbContext concurrent = fixture.CreateCalls();
            CallSession call = await concurrent.Calls.SingleAsync(
                item => item.Id == new CallSessionId(harness.CallId), cancellationToken);
            call.Fail("superseded", TimeProvider.System.GetUtcNow());
            await concurrent.SaveChangesAsync(cancellationToken);
        };

        Assert.True(await harness.Processor.ProcessNextAsync(default));

        await using CallManagementDbContext verify = fixture.CreateCalls();
        CallSession call = await verify.Calls.SingleAsync(item => item.Id == new CallSessionId(harness.CallId));
        Assert.Equal(CallState.Failed, call.State);
        Assert.Single(harness.Provider.Calls);
    }

    [Fact]
    public async Task RepeatedConflictExhaustsBudgetAndWorkerCanContinueWithoutDuplicateProviderAction()
    {
        TestHarness harness = await CreateHarnessAsync();
        harness.Faults.FailSaves.UnionWith([2, 3, 4]);

        Assert.True(await harness.Processor.ProcessNextAsync(default));
        Assert.Single(harness.Provider.Calls);
        Assert.Equal(4, harness.Faults.SaveCount);

        Assert.False(await harness.Processor.ProcessNextAsync(default));
        Assert.Single(harness.Provider.Calls);
    }

    [Fact]
    public async Task UnexpectedCompletionFailureRemainsFatalAndObservable()
    {
        TestHarness harness = await CreateHarnessAsync();
        harness.Faults.UnexpectedSave = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Processor.ProcessNextAsync(default));
        Assert.Single(harness.Provider.Calls);
    }

    private async Task<TestHarness> CreateHarnessAsync()
    {
        Guid tenant = Guid.NewGuid();
        Guid location = Guid.NewGuid();
        Guid callId;
        Guid operationId;
        await using (CallManagementDbContext seed = fixture.CreateCalls())
        {
            var calls = new CallManagementService(seed, TimeProvider.System);
            _ = await calls.ConfigureTelephonyNumberAsync(new ConfigureTelephonyNumber(
                tenant, location, "Fake", $"+1787{Random.Shared.Next(1000000, 9999999)}", null,
                true, true, true), default);
            CallSummary call = await calls.RequestTransportOutboundAsync(new RequestTransportOutboundCall(
                tenant, location, Guid.NewGuid().ToString("N"), "+17875550123", Guid.NewGuid()), default);
            callId = call.CallId;
            operationId = (await seed.TelephonyOperations.SingleAsync(item => item.CallId == new CallSessionId(callId))).Id;
        }

        var faults = new SaveFaultPlan();
        var provider = new FakeTelephonyProvider();
        var services = new ServiceCollection();
        services.AddDbContext<CallManagementDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddSingleton(faults);
        services.AddScoped<FaultingCallStore>();
        services.AddScoped<ICallStore>(sp => sp.GetRequiredService<FaultingCallStore>());
        services.AddScoped<ITelephonyStore>(sp => sp.GetRequiredService<FaultingCallStore>());
        services.AddScoped<CallManagementService>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        ServiceProvider serviceProvider = services.BuildServiceProvider();
        var processor = new TelephonyDispatchProcessor(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(), provider,
            Options.Create(new TelephonyRuntimeOptions { PublicBaseUrl = "https://example.test" }),
            TimeProvider.System, NullLogger<TelephonyDispatchProcessor>.Instance);
        return new TestHarness(serviceProvider, processor, provider, faults, operationId, callId);
    }

    private sealed record TestHarness(
        ServiceProvider Services,
        TelephonyDispatchProcessor Processor,
        FakeTelephonyProvider Provider,
        SaveFaultPlan Faults,
        Guid OperationId,
        Guid CallId);

    private sealed class SaveFaultPlan
    {
        private int saveCount;
        public int SaveCount => Volatile.Read(ref saveCount);
        public HashSet<int> FailSaves { get; } = [];
        public int? UnexpectedSave { get; set; }
        public Func<CancellationToken, Task>? BeforeFailure { get; set; }
        public int NextSave() => Interlocked.Increment(ref saveCount);
    }

    private sealed class FaultingCallStore(
        CallManagementDbContext inner,
        SaveFaultPlan faults) : ICallStore, ITelephonyStore
    {
        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            int save = faults.NextSave();
            if (faults.UnexpectedSave == save) throw new InvalidOperationException("synthetic unexpected failure");
            if (faults.FailSaves.Contains(save))
            {
                if (faults.BeforeFailure is not null) await faults.BeforeFailure(cancellationToken);
                throw new CallPersistenceConcurrencyException(new DbUpdateConcurrencyException("synthetic concurrency conflict"));
            }
            await ((ICallStore)inner).SaveChangesAsync(cancellationToken);
        }

        public Task<CallSession?> GetAsync(Guid tenantId, Guid callId, bool tracking, CancellationToken token) => inner.GetAsync(tenantId, callId, tracking, token);
        public Task<CallSession?> GetByProviderCallIdAsync(Guid tenantId, string id, bool tracking, CancellationToken token) => inner.GetByProviderCallIdAsync(tenantId, id, tracking, token);
        public Task<CallSession?> GetByOutboundKeyAsync(Guid tenantId, string key, CancellationToken token) => inner.GetByOutboundKeyAsync(tenantId, key, token);
        public Task<IReadOnlyList<CallSession>> GetRecentAsync(Guid tenantId, Guid? locationId, int limit, CancellationToken token) => inner.GetRecentAsync(tenantId, locationId, limit, token);
        public void Add(CallSession call) => inner.Add(call);
        public void AddOutboundRequest(Guid tenantId, string key, Guid callId, DateTimeOffset created) => inner.AddOutboundRequest(tenantId, key, callId, created);
        public void AddOutbox(OutboxMessage message) => inner.AddOutbox(message);
        public Task<CallSession?> GetByProviderIdentityAsync(string provider, string id, bool tracking, CancellationToken token) => inner.GetByProviderIdentityAsync(provider, id, tracking, token);
        public Task<TelephonyNumber?> ResolveInboundNumberAsync(string provider, string number, CancellationToken token) => inner.ResolveInboundNumberAsync(provider, number, token);
        public Task<TelephonyNumber?> ResolveOutboundNumberAsync(Guid tenantId, Guid locationId, CancellationToken token) => inner.ResolveOutboundNumberAsync(tenantId, locationId, token);
        public Task<IReadOnlyList<TelephonyNumber>> GetTelephonyNumbersAsync(Guid tenantId, CancellationToken token) => inner.GetTelephonyNumbersAsync(tenantId, token);
        public Task<TelephonyNumber?> GetTelephonyNumberAsync(Guid tenantId, string provider, string number, bool tracking, CancellationToken token) => inner.GetTelephonyNumberAsync(tenantId, provider, number, tracking, token);
        public Task<bool> HasWebhookReceiptAsync(string provider, string eventId, CancellationToken token) => inner.HasWebhookReceiptAsync(provider, eventId, token);
        public Task<TelephonyOperation?> GetPendingTelephonyOperationAsync(CancellationToken token) => inner.GetPendingTelephonyOperationAsync(token);
        public Task<TelephonyOperation?> GetTelephonyOperationAsync(Guid id, bool tracking, CancellationToken token) => inner.GetTelephonyOperationAsync(id, tracking, token);
        public Task<TelephonyOperation?> GetTelephonyOperationForCallAsync(Guid tenantId, Guid callId, TelephonyOperationType type, CancellationToken token) => inner.GetTelephonyOperationForCallAsync(tenantId, callId, type, token);
        public void Add(TelephonyNumber number) => inner.Add(number);
        public void Add(TelephonyOperation operation) => inner.Add(operation);
        public void AddWebhookReceipt(string provider, string eventId, Guid callId, DateTimeOffset received) => inner.AddWebhookReceipt(provider, eventId, callId, received);
    }
}
