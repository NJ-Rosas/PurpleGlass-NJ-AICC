using PurpleGlass.Eventing;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Contracts;
using PurpleGlass.Modules.CallManagement.Domain;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Contracts;
using ConversationAggregate = PurpleGlass.Modules.Conversation.Domain.Conversation;

namespace PurpleGlass.UnitTests;

public sealed class ApplicationServiceTests
{
    [Fact]
    public async Task InboundHandlerReturnsPriorResultForDuplicateProviderNotification()
    {
        var store = new MemoryCallStore();
        var service = new CallManagementService(store, TimeProvider.System);
        var command = new RegisterInboundCall(Guid.NewGuid(), Guid.NewGuid(), "provider-1", "+15550000002", "+15550000001", Guid.NewGuid());

        CallSummary first = await service.RegisterInboundAsync(command, default);
        CallSummary replay = await service.RegisterInboundAsync(command, default);

        Assert.Equal(first.CallId, replay.CallId);
        Assert.Single(store.Calls);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task ConversationHandlerRejectsIneligibleCall()
    {
        Guid tenant = Guid.NewGuid();
        Guid call = Guid.NewGuid();
        var eligibility = new FixedEligibility(new CallEligibility(call, tenant, Guid.NewGuid(), "Received", false));
        var service = new ConversationService(new MemoryConversationStore(), eligibility, TimeProvider.System);

        ConversationApplicationException error = await Assert.ThrowsAsync<ConversationApplicationException>(() =>
            service.CreateAsync(new CreateConversation(tenant, eligibility.Value.LocationId, call, Guid.NewGuid(), "v1", "en-US"), default));

        Assert.Equal("call_not_eligible", error.Code);
    }

    private sealed class FixedEligibility(CallEligibility value) : ICallEligibilityQuery
    {
        public CallEligibility Value { get; } = value;
        public Task<CallEligibility?> GetEligibilityAsync(Guid tenantId, Guid callId, CancellationToken cancellationToken) => Task.FromResult<CallEligibility?>(Value);
    }

    private sealed class MemoryCallStore : ICallStore
    {
        public List<CallSession> Calls { get; } = [];
        public List<OutboxMessage> Outbox { get; } = [];
        public Task<CallSession?> GetAsync(Guid tenantId, Guid callId, bool tracking, CancellationToken cancellationToken) => Task.FromResult(Calls.SingleOrDefault(c => c.TenantId.Value == tenantId && c.Id.Value == callId));
        public Task<CallSession?> GetByProviderCallIdAsync(Guid tenantId, string providerCallId, bool tracking, CancellationToken cancellationToken) => Task.FromResult(Calls.SingleOrDefault(c => c.TenantId.Value == tenantId && c.ProviderCallId == providerCallId));
        public Task<CallSession?> GetByOutboundKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken) => Task.FromResult<CallSession?>(null);
        public Task<IReadOnlyList<CallSession>> GetRecentAsync(Guid tenantId, Guid? locationId, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CallSession>>(Calls);
        public void Add(CallSession callSession) => Calls.Add(callSession);
        public void AddOutboundRequest(Guid tenantId, string idempotencyKey, Guid callId, DateTimeOffset createdAtUtc) { }
        public void AddOutbox(OutboxMessage message) => Outbox.Add(message);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MemoryConversationStore : IConversationStore
    {
        private readonly List<ConversationAggregate> conversations = [];
        public Task<ConversationAggregate?> GetAsync(Guid tenantId, Guid conversationId, bool tracking, CancellationToken cancellationToken) => Task.FromResult(conversations.SingleOrDefault(c => c.TenantId.Value == tenantId && c.Id.Value == conversationId));
        public Task<ConversationAggregate?> GetForCallAsync(Guid tenantId, Guid callId, bool tracking, CancellationToken cancellationToken) => Task.FromResult(conversations.SingleOrDefault(c => c.TenantId.Value == tenantId && c.CallSession.Value == callId));
        public void Add(ConversationAggregate conversation) => conversations.Add(conversation);
        public void AddOutbox(OutboxMessage message) { }
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
