using System.Text.Json;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.CallManagement.Contracts;
using PurpleGlass.Modules.CallManagement.Domain;

namespace PurpleGlass.Modules.CallManagement.Application;

public sealed class CallManagementService(ICallStore store, TimeProvider timeProvider) : ICallEligibilityQuery
{
    public async Task<CallSummary> RegisterInboundAsync(RegisterInboundCall command, CancellationToken cancellationToken)
    {
        CallSession? existing = await store.GetByProviderCallIdAsync(command.TenantId, command.ProviderCallId, false, cancellationToken);
        if (existing is not null)
        {
            return Map(existing);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        CallSession call = CallSession.ReceiveInbound(
            CallSessionId.New(), new TenantId(command.TenantId), new LocationId(command.LocationId),
            command.ProviderCallId, command.FromNumber, command.ToNumber, command.CorrelationId, now);
        store.Add(call);
        AddEvent(call, new CallReceived(call.Id.Value, "Inbound", now), nameof(CallReceived), now);
        await SaveAsync(cancellationToken);
        return Map(call);
    }

    public async Task<CallSummary> RequestOutboundAsync(RequestOutboundCall command, CancellationToken cancellationToken)
    {
        CallSession? existing = await store.GetByOutboundKeyAsync(command.TenantId, command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.ToNumber != command.ToNumber || existing.FromNumber != command.FromNumber)
            {
                throw CallApplicationException.IdempotencyConflict();
            }

            return Map(existing);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        CallSession call = CallSession.RequestOutbound(
            CallSessionId.New(), new TenantId(command.TenantId), new LocationId(command.LocationId),
            null, command.FromNumber, command.ToNumber, command.CorrelationId, now);
        store.Add(call);
        store.AddOutboundRequest(command.TenantId, RequireKey(command.IdempotencyKey), call.Id.Value, now);
        AddEvent(call, new OutboundCallRequested(call.Id.Value, now), nameof(OutboundCallRequested), now);
        await SaveAsync(cancellationToken);
        return Map(call);
    }

    public Task<CallSummary> MarkRingingAsync(ChangeCallState command, CancellationToken cancellationToken) =>
        TransitionAsync(command, CallState.Ringing, call => call.MarkRinging(), cancellationToken);

    public Task<CallSummary> MarkAnsweredAsync(ChangeCallState command, CancellationToken cancellationToken) =>
        TransitionAsync(command, CallState.Answered, call => call.Answer(timeProvider.GetUtcNow()), cancellationToken);

    public Task<CallSummary> MarkInConversationAsync(ChangeCallState command, CancellationToken cancellationToken) =>
        TransitionAsync(command, CallState.InConversation, call => call.StartConversation(), cancellationToken);

    public async Task<CallSummary> CompleteAsync(CompleteCall command, CancellationToken cancellationToken)
    {
        CallSession call = await Load(command.TenantId, command.CallId, cancellationToken);
        if (call.State == CallState.Completed && call.Outcome == command.Outcome.Trim()) return Map(call);
        EnsureVersion(call, command.ExpectedVersion);
        CallState previous = call.State;
        DateTimeOffset now = timeProvider.GetUtcNow();
        Apply(() => call.Complete(command.Outcome, now));
        AddEvent(call, new CallStateChanged(call.Id.Value, previous.ToString(), call.State.ToString(), call.Version), nameof(CallStateChanged), now);
        AddEvent(call, new CallCompleted(call.Id.Value, call.Outcome!, now, call.RecordingReference), nameof(CallCompleted), now);
        await SaveAsync(cancellationToken);
        return Map(call);
    }

    public async Task<CallSummary> FailAsync(FailCall command, CancellationToken cancellationToken)
    {
        CallSession call = await Load(command.TenantId, command.CallId, cancellationToken);
        if (call.State == CallState.Failed && call.Outcome == command.Reason.Trim()) return Map(call);
        EnsureVersion(call, command.ExpectedVersion);
        CallState previous = call.State;
        DateTimeOffset now = timeProvider.GetUtcNow();
        Apply(() => call.Fail(command.Reason, now));
        AddEvent(call, new CallStateChanged(call.Id.Value, previous.ToString(), call.State.ToString(), call.Version), nameof(CallStateChanged), now);
        AddEvent(call, new CallFailed(call.Id.Value, call.Outcome!, now), nameof(CallFailed), now);
        await SaveAsync(cancellationToken);
        return Map(call);
    }

    public async Task<CallSummary> AttachRecordingAsync(AttachCallRecording command, CancellationToken cancellationToken)
    {
        CallSession call = await Load(command.TenantId, command.CallId, cancellationToken);
        if (call.RecordingReference == command.Recording.ObjectReference) return Map(call);
        EnsureVersion(call, command.ExpectedVersion);
        Apply(() => call.AttachRecording(command.Recording));
        await SaveAsync(cancellationToken);
        return Map(call);
    }

    public async Task<CallSummary> GetAsync(Guid tenantId, Guid callId, CancellationToken cancellationToken) =>
        Map(await store.GetAsync(tenantId, callId, false, cancellationToken) ?? throw CallApplicationException.NotFound());

    public async Task<CallSummary> GetByProviderCallIdAsync(Guid tenantId, string providerCallId, CancellationToken cancellationToken) =>
        Map(await store.GetByProviderCallIdAsync(tenantId, providerCallId, false, cancellationToken) ?? throw CallApplicationException.NotFound());

    public async Task<IReadOnlyList<CallSummary>> GetRecentAsync(Guid tenantId, Guid? locationId, int limit, CancellationToken cancellationToken) =>
        (await store.GetRecentAsync(tenantId, locationId, Math.Clamp(limit, 1, 100), cancellationToken)).Select(Map).ToArray();

    public async Task<CallEligibility?> GetEligibilityAsync(Guid tenantId, Guid callId, CancellationToken cancellationToken)
    {
        CallSession? call = await store.GetAsync(tenantId, callId, false, cancellationToken);
        return call is null ? null : new CallEligibility(call.Id.Value, call.TenantId.Value, call.LocationId.Value,
            call.State.ToString(), call.State is CallState.Answered or CallState.InConversation);
    }

    private async Task<CallSummary> TransitionAsync(ChangeCallState command, CallState target, Action<CallSession> transition, CancellationToken cancellationToken)
    {
        CallSession call = await Load(command.TenantId, command.CallId, cancellationToken);
        if (call.State == target) return Map(call);
        EnsureVersion(call, command.ExpectedVersion);
        CallState previous = call.State;
        DateTimeOffset now = timeProvider.GetUtcNow();
        Apply(() => transition(call));
        if (target == CallState.Answered)
        {
            AddEvent(call, new CallAnswered(call.Id.Value, call.AnsweredAtUtc!.Value), nameof(CallAnswered), now);
        }
        AddEvent(call, new CallStateChanged(call.Id.Value, previous.ToString(), call.State.ToString(), call.Version), nameof(CallStateChanged), now);
        await SaveAsync(cancellationToken);
        return Map(call);
    }

    private async Task<CallSession> Load(Guid tenantId, Guid callId, CancellationToken cancellationToken) =>
        await store.GetAsync(tenantId, callId, true, cancellationToken) ?? throw CallApplicationException.NotFound();

    private static void EnsureVersion(CallSession call, long expectedVersion)
    {
        if (call.Version != expectedVersion) throw CallApplicationException.Concurrency();
    }

    private void AddEvent<T>(CallSession call, T payload, string type, DateTimeOffset now) => store.AddOutbox(OutboxMessage.Create(
        call.TenantId.Value, call.LocationId.Value,
        $"pg/local/v1/tenants/{call.TenantId.Value:D}/calls/{call.Id.Value:D}/events/{ToTopic(type)}",
        type, JsonSerializer.Serialize(payload), call.CorrelationId, now, producer: "call-management"));

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await store.SaveChangesAsync(cancellationToken); }
        catch (CallApplicationException) { throw; }
        catch (CallPersistenceConcurrencyException exception) { throw CallApplicationException.Concurrency(exception); }
    }

    private static void Apply(Action action)
    {
        try { action(); }
        catch (InvalidOperationException exception) { throw CallApplicationException.InvalidState(exception); }
    }

    private static string RequireKey(string value) => string.IsNullOrWhiteSpace(value) || value.Length > 200
        ? throw new CallApplicationException("invalid_idempotency_key", "A bounded idempotency key is required.") : value.Trim();
    private static string ToTopic(string value) => string.Concat(value.Select((c, i) => char.IsUpper(c) && i > 0 ? $"-{char.ToLowerInvariant(c)}" : char.ToLowerInvariant(c).ToString()));
    private static CallSummary Map(CallSession call) => new(call.Id.Value, call.Direction.ToString(), call.State.ToString(), call.CreatedAtUtc, call.CompletedAtUtc, call.Outcome, null, call.RecordingReference, call.Version);
}
