using System.Text.Json;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.CallManagement.Contracts;
using PurpleGlass.Modules.CallManagement.Domain;
using PurpleGlass.SharedKernel;

namespace PurpleGlass.Modules.CallManagement.Application;

public sealed class CallManagementService(
    ICallStore store,
    TimeProvider timeProvider,
    ITelephonyStore? telephonyStore = null,
    ILocationCallLanguageResolver? locationLanguageResolver = null) : ICallEligibilityQuery
{
    private ITelephonyStore Telephony => telephonyStore ?? store as ITelephonyStore
        ?? throw new CallApplicationException("telephony_store_unavailable", "Telephony persistence is not available.");
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
            command.ProviderCallId, PhoneNumber.Normalize(command.FromNumber),
            PhoneNumber.Normalize(command.ToNumber), command.CorrelationId, now, command.Provider,
            command.StartingLanguageCode, command.StartingLanguageReason);
        store.Add(call);
        AddEvent(call, new CallReceived(call.Id.Value, "Inbound", now), nameof(CallReceived), now, command.CausationId, command.TraceId);
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
            null, PhoneNumber.Normalize(command.FromNumber),
            PhoneNumber.Normalize(command.ToNumber), command.CorrelationId, now, command.Provider,
            command.StartingLanguageCode, command.StartingLanguageReason);
        store.Add(call);
        store.AddOutboundRequest(command.TenantId, RequireKey(command.IdempotencyKey), call.Id.Value, now);
        AddEvent(call, new OutboundCallRequested(call.Id.Value, now), nameof(OutboundCallRequested), now, command.CausationId, command.TraceId);
        await SaveAsync(cancellationToken);
        return Map(call);
    }

    public async Task<CallSummary> RequestTransportOutboundAsync(RequestTransportOutboundCall command, CancellationToken cancellationToken)
    {
        string destination = PhoneNumber.Normalize(command.DestinationNumber);
        (string startingLanguage, string languageReason) = await ResolveStartingLanguageAsync(
            command.TenantId, command.LocationId, command.LanguageCode, cancellationToken);
        CallSession? existing = await store.GetByOutboundKeyAsync(command.TenantId, command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.ToNumber != destination || existing.StartingLanguageCode != startingLanguage)
                throw CallApplicationException.IdempotencyConflict();
            return Map(existing);
        }

        TelephonyNumber source = await Telephony.ResolveOutboundNumberAsync(command.TenantId, command.LocationId, cancellationToken)
            ?? throw new CallApplicationException("telephony_number_unavailable", "No active outbound telephone number is configured for this location.");
        DateTimeOffset now = timeProvider.GetUtcNow();
        CallSession call = CallSession.RequestOutbound(
            CallSessionId.New(), new TenantId(command.TenantId), new LocationId(command.LocationId),
            null, source.NormalizedNumber, destination, command.CorrelationId, now, source.Provider,
            startingLanguage, languageReason);
        store.Add(call);
        store.AddOutboundRequest(command.TenantId, RequireKey(command.IdempotencyKey), call.Id.Value, now);
        Telephony.Add(new TelephonyOperation(Guid.NewGuid(), call.TenantId, call.LocationId, call.Id,
            TelephonyOperationType.StartOutbound, now));
        AddEvent(call, new OutboundCallRequested(call.Id.Value, now), nameof(OutboundCallRequested), now, command.CausationId, command.TraceId);
        await SaveAsync(cancellationToken);
        return Map(call);
    }

    public async Task<CallSummary> RegisterInboundTransportAsync(
        string provider, string providerCallId, string? providerParentCallId, string fromNumber, string toNumber,
        Guid correlationId, CancellationToken cancellationToken)
    {
        CallSession? existing = await Telephony.GetByProviderIdentityAsync(provider, providerCallId, false, cancellationToken);
        if (existing is not null) return Map(existing);
        string normalizedTo = PhoneNumber.Normalize(toNumber);
        TelephonyNumber route = await Telephony.ResolveInboundNumberAsync(provider, normalizedTo, cancellationToken)
            ?? throw new CallApplicationException("telephony_route_not_found", "The destination telephone number is not configured for inbound calls.");
        if (route.LocationId is null)
            throw new CallApplicationException("telephony_location_required", "The destination telephone number is not assigned to a location.");

        (string startingLanguage, string languageReason) = await ResolveStartingLanguageAsync(
            route.TenantId.Value, route.LocationId.Value.Value, null, cancellationToken);
        CallSummary call = await RegisterInboundAsync(new RegisterInboundCall(
            route.TenantId.Value, route.LocationId.Value.Value, providerCallId,
            PhoneNumber.Normalize(fromNumber), normalizedTo, correlationId, Provider: provider,
            StartingLanguageCode: startingLanguage, StartingLanguageReason: languageReason), cancellationToken);
        if (!string.IsNullOrWhiteSpace(providerParentCallId))
        {
            call = await AssignProviderIdentityAsync(new AssignProviderCallIdentity(
                route.TenantId.Value, call.CallId, providerCallId, providerParentCallId), cancellationToken);
        }
        return call;
    }

    public async Task<CallSummary> AssignProviderIdentityAsync(AssignProviderCallIdentity command, CancellationToken cancellationToken)
    {
        CallSession call = await Load(command.TenantId, command.CallId, cancellationToken);
        long previousVersion = call.Version;
        Apply(() => call.AssignProviderIdentity(command.ProviderCallId, command.ProviderParentCallId));
        if (call.Version != previousVersion)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            AddEvent(call, new CallProviderIdentityAssigned(call.Id.Value, call.Provider, now),
                nameof(CallProviderIdentityAssigned), now, null, null);
            await SaveAsync(cancellationToken);
        }
        return Map(call);
    }

    public async Task<CallSummary> ApplyProviderStatusAsync(ApplyProviderCallStatus command, CancellationToken cancellationToken)
    {
        string eventId = RequireKey(command.EventId);
        if (await Telephony.HasWebhookReceiptAsync(command.Provider, eventId, cancellationToken))
        {
            CallSession duplicate = await Telephony.GetByProviderIdentityAsync(command.Provider, command.ProviderCallId, false, cancellationToken)
                ?? throw CallApplicationException.NotFound();
            return Map(duplicate);
        }

        CallSession call = await Telephony.GetByProviderIdentityAsync(command.Provider, command.ProviderCallId, true, cancellationToken)
            ?? throw CallApplicationException.NotFound();
        DateTimeOffset now = timeProvider.GetUtcNow();
        string status = command.ProviderStatus.Trim().ToLowerInvariant();
        if (call.State is not (CallState.Completed or CallState.Failed))
        {
            if (status == "ringing" && call.State == CallState.Requested) Transition(call, call.MarkRinging, now);
            else if (status == "in-progress") EnsureAnswered(call, now);
            else if (status == "completed")
            {
                EnsureAnswered(call, now);
                if (call.State is CallState.Answered or CallState.InConversation)
                    Transition(call, () => call.Complete("provider_completed", now), now);
            }
            else if (status is "busy" or "failed" or "no-answer" or "canceled")
                Transition(call, () => call.Fail($"provider_{status.Replace('-', '_')}", now), now);
        }
        Telephony.AddWebhookReceipt(command.Provider, eventId, call.Id.Value, now);
        await SaveAsync(cancellationToken);
        return Map(call);
    }

    public async Task<VoiceCallContext> ResolveVoiceCallAsync(
        string provider,
        string providerCallId,
        CancellationToken cancellationToken)
    {
        CallSession call = await Telephony.GetByProviderIdentityAsync(provider, providerCallId, false, cancellationToken)
            ?? throw CallApplicationException.NotFound();
        return MapVoiceContext(call);
    }

    public async Task<VoiceCallContext> ConnectVoiceMediaAsync(
        string provider,
        string providerCallId,
        CancellationToken cancellationToken)
    {
        CallSession call = await Telephony.GetByProviderIdentityAsync(provider, providerCallId, true, cancellationToken)
            ?? throw CallApplicationException.NotFound();
        if (!string.Equals(call.Provider, provider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(call.ProviderCallId, providerCallId, StringComparison.Ordinal))
            throw CallApplicationException.NotFound();
        if (call.State is CallState.Completed or CallState.Failed)
            throw new CallApplicationException("call_not_eligible", "The call is no longer eligible for a voice session.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        EnsureAnswered(call, now);
        if (call.State == CallState.Answered)
            Transition(call, call.StartConversation, now);
        await SaveAsync(cancellationToken);
        return MapVoiceContext(call);
    }

    public async Task<CallSummary> RequestHangupAsync(RequestCallHangup command, CancellationToken cancellationToken)
    {
        CallSession call = await Load(command.TenantId, command.CallId, cancellationToken);
        if (call.LocationId.Value != command.LocationId) throw CallApplicationException.NotFound();
        if (call.State is CallState.Completed or CallState.Failed) return Map(call);
        if (call.ProviderCallId is null)
            throw new CallApplicationException("provider_identity_unavailable", "The call has not been accepted by the telephony provider.");
        TelephonyOperation? existing = await Telephony.GetTelephonyOperationForCallAsync(
            command.TenantId, command.CallId, TelephonyOperationType.Hangup, cancellationToken);
        if (existing is null)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            Telephony.Add(new TelephonyOperation(Guid.NewGuid(), call.TenantId, call.LocationId, call.Id,
                TelephonyOperationType.Hangup, now));
            AddEvent(call, new CallHangupRequested(call.Id.Value, now), nameof(CallHangupRequested), now, null, null);
            await SaveAsync(cancellationToken);
        }
        return Map(call);
    }

    public async Task<TelephonyNumberSummary> ConfigureTelephonyNumberAsync(ConfigureTelephonyNumber command, CancellationToken cancellationToken)
    {
        string normalized = PhoneNumber.Normalize(command.Number);
        TelephonyNumber? number = await Telephony.GetTelephonyNumberAsync(command.TenantId, command.Provider, normalized, true, cancellationToken);
        LocationId? locationId = command.LocationId.HasValue ? new LocationId(command.LocationId.Value) : null;
        if (number is null)
        {
            number = new TelephonyNumber(Guid.NewGuid(), new TenantId(command.TenantId), locationId,
                command.Provider, normalized, command.ProviderNumberId, command.InboundEnabled, command.OutboundEnabled);
            if (!command.Active) number.Update(locationId, command.ProviderNumberId, command.InboundEnabled, command.OutboundEnabled, false);
            Telephony.Add(number);
        }
        else
        {
            number.Update(locationId, command.ProviderNumberId, command.InboundEnabled, command.OutboundEnabled, command.Active);
        }
        await SaveAsync(cancellationToken);
        return Map(number);
    }

    public async Task<IReadOnlyList<TelephonyNumberSummary>> GetTelephonyNumbersAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await Telephony.GetTelephonyNumbersAsync(tenantId, cancellationToken)).Select(Map).ToArray();

    public async Task<TelephonyDispatch?> BeginNextTelephonyDispatchAsync(CancellationToken cancellationToken)
    {
        TelephonyOperation? operation = await Telephony.GetPendingTelephonyOperationAsync(cancellationToken);
        if (operation is null) return null;
        operation.BeginDispatch(timeProvider.GetUtcNow());
        CallSession call = await Load(operation.TenantId.Value, operation.CallId.Value, cancellationToken);
        await SaveAsync(cancellationToken);
        return new TelephonyDispatch(operation.Id, operation.Type.ToString(), operation.TenantId.Value,
            operation.LocationId.Value, operation.CallId.Value, call.Provider, call.ProviderCallId,
            call.FromNumber, call.ToNumber, operation.CreatedAtUtc);
    }

    public async Task CompleteTelephonyDispatchAsync(Guid operationId, string? providerCallId, string? safeErrorCode, CancellationToken cancellationToken)
    {
        TelephonyOperation operation = await Telephony.GetTelephonyOperationAsync(operationId, true, cancellationToken)
            ?? throw new CallApplicationException("telephony_operation_not_found", "Telephony operation was not found.");
        if (operation.State is TelephonyOperationState.Completed or TelephonyOperationState.Failed) return;
        if (operation.State != TelephonyOperationState.Dispatching)
            throw new CallApplicationException("telephony_operation_invalid_state", "Telephony operation is not dispatching.");
        CallSession call = await Load(operation.TenantId.Value, operation.CallId.Value, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (safeErrorCode is not null)
        {
            operation.Fail(safeErrorCode, now);
            if (call.State is not (CallState.Completed or CallState.Failed))
                Transition(call, () => call.Fail(safeErrorCode, now), now);
        }
        else
        {
            if (operation.Type == TelephonyOperationType.StartOutbound)
            {
                if (string.IsNullOrWhiteSpace(providerCallId))
                    throw new CallApplicationException("provider_invalid_response", "Provider response did not contain a call identifier.");
                call.AssignProviderIdentity(providerCallId);
                AddEvent(call, new CallProviderIdentityAssigned(call.Id.Value, call.Provider, now),
                    nameof(CallProviderIdentityAssigned), now, null, null);
            }
            operation.Complete(now);
        }
        await SaveAsync(cancellationToken);
    }

    public async Task ReconcileProviderIdentityAsync(Guid operationId, string provider, string providerCallId, CancellationToken cancellationToken)
    {
        TelephonyOperation operation = await Telephony.GetTelephonyOperationAsync(operationId, true, cancellationToken)
            ?? throw new CallApplicationException("telephony_operation_not_found", "Telephony operation was not found.");
        if (operation.Type != TelephonyOperationType.StartOutbound)
            throw new CallApplicationException("telephony_operation_invalid_type", "Telephony operation cannot assign a provider identity.");
        CallSession call = await Load(operation.TenantId.Value, operation.CallId.Value, cancellationToken);
        if (!string.Equals(call.Provider, provider, StringComparison.OrdinalIgnoreCase))
            throw new CallApplicationException("provider_mismatch", "Provider callback did not match the durable call provider.");
        long version = call.Version;
        call.AssignProviderIdentity(providerCallId);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (call.Version != version)
            AddEvent(call, new CallProviderIdentityAssigned(call.Id.Value, call.Provider, now),
                nameof(CallProviderIdentityAssigned), now, null, null);
        if (operation.State == TelephonyOperationState.Dispatching) operation.Complete(now);
        await SaveAsync(cancellationToken);
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
        AddEvent(call, new CallStateChanged(call.Id.Value, previous.ToString(), call.State.ToString(), call.Version), nameof(CallStateChanged), now, command.CausationId, command.TraceId);
        AddEvent(call, new CallCompleted(call.Id.Value, call.Outcome!, now, call.RecordingReference), nameof(CallCompleted), now, command.CausationId, command.TraceId);
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
        AddEvent(call, new CallStateChanged(call.Id.Value, previous.ToString(), call.State.ToString(), call.Version), nameof(CallStateChanged), now, command.CausationId, command.TraceId);
        AddEvent(call, new CallFailed(call.Id.Value, call.Outcome!, now), nameof(CallFailed), now, command.CausationId, command.TraceId);
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

    public async Task<CallSummary> GetForLocationAsync(Guid tenantId, Guid locationId, Guid callId, CancellationToken cancellationToken)
    {
        CallSession call = await store.GetAsync(tenantId, callId, false, cancellationToken)
            ?? throw CallApplicationException.NotFound();
        return call.LocationId.Value == locationId ? Map(call) : throw CallApplicationException.NotFound();
    }

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
            AddEvent(call, new CallAnswered(call.Id.Value, call.AnsweredAtUtc!.Value), nameof(CallAnswered), now, command.CausationId, command.TraceId);
        }
        AddEvent(call, new CallStateChanged(call.Id.Value, previous.ToString(), call.State.ToString(), call.Version), nameof(CallStateChanged), now, command.CausationId, command.TraceId);
        await SaveAsync(cancellationToken);
        return Map(call);
    }

    private async Task<CallSession> Load(Guid tenantId, Guid callId, CancellationToken cancellationToken) =>
        await store.GetAsync(tenantId, callId, true, cancellationToken) ?? throw CallApplicationException.NotFound();

    private static void EnsureVersion(CallSession call, long expectedVersion)
    {
        if (call.Version != expectedVersion) throw CallApplicationException.Concurrency();
    }

    private void AddEvent<T>(CallSession call, T payload, string type, DateTimeOffset now, Guid? causationId, string? traceId) => store.AddOutbox(OutboxMessage.Create(
        call.TenantId.Value, call.LocationId.Value,
        $"pg/local/v1/tenants/{call.TenantId.Value:D}/calls/{call.Id.Value:D}/events/{ToTopic(type)}",
        type, JsonSerializer.Serialize(payload), call.CorrelationId, now,
        causationId: causationId, traceId: traceId, producer: "call-management"));

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

    private void EnsureAnswered(CallSession call, DateTimeOffset now)
    {
        if (call.State == CallState.Requested) Transition(call, call.MarkRinging, now);
        if (call.State is CallState.Ringing or CallState.Received) Transition(call, () => call.Answer(now), now);
    }

    private void Transition(CallSession call, Action action, DateTimeOffset now)
    {
        CallState previous = call.State;
        Apply(action);
        AddEvent(call, new CallStateChanged(call.Id.Value, previous.ToString(), call.State.ToString(), call.Version),
            nameof(CallStateChanged), now, null, null);
        if (call.State == CallState.Answered)
            AddEvent(call, new CallAnswered(call.Id.Value, now), nameof(CallAnswered), now, null, null);
        else if (call.State == CallState.Completed)
            AddEvent(call, new CallCompleted(call.Id.Value, call.Outcome!, now, call.RecordingReference), nameof(CallCompleted), now, null, null);
        else if (call.State == CallState.Failed)
            AddEvent(call, new CallFailed(call.Id.Value, call.Outcome!, now), nameof(CallFailed), now, null, null);
    }

    private static string RequireKey(string value) => string.IsNullOrWhiteSpace(value) || value.Length > 200
        ? throw new CallApplicationException("invalid_idempotency_key", "A bounded idempotency key is required.") : value.Trim();
    private static string ToTopic(string value) => string.Concat(value.Select((c, i) => char.IsUpper(c) && i > 0 ? $"-{char.ToLowerInvariant(c)}" : char.ToLowerInvariant(c).ToString()));
    private async Task<(string Code, string Reason)> ResolveStartingLanguageAsync(
        Guid tenantId, Guid locationId, string? overrideCode, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(overrideCode))
        {
            if (!SupportedCallLanguages.TryNormalize(overrideCode, out SupportedCallLanguage language))
                throw new CallApplicationException("unsupported_call_language", "The requested call language is not supported.");
            return (language.Code, "call_override");
        }
        string? configured = locationLanguageResolver is null
            ? null
            : await locationLanguageResolver.ResolveDefaultLanguageAsync(tenantId, locationId, cancellationToken);
        if (SupportedCallLanguages.TryNormalize(configured, out SupportedCallLanguage locationLanguage))
            return (locationLanguage.Code, "location_default");
        return (SupportedCallLanguages.SystemFallbackCode, "fallback");
    }

    private static CallSummary Map(CallSession call) => new(call.Id.Value, call.Direction.ToString(), call.State.ToString(), call.CreatedAtUtc, call.CompletedAtUtc, call.Outcome, null, call.RecordingReference, call.Version, call.Provider, call.FromNumber, call.ToNumber, call.StartingLanguageCode, call.StartingLanguageReason);
    private static VoiceCallContext MapVoiceContext(CallSession call) => new(
        call.Id.Value, call.TenantId.Value, call.LocationId.Value, call.CorrelationId,
        call.Direction.ToString(), call.State.ToString(), call.Provider,
        call.ProviderCallId ?? throw new CallApplicationException("provider_identity_unavailable", "The provider call identity is unavailable."),
        call.Version, call.StartingLanguageCode, call.StartingLanguageReason);
    private static TelephonyNumberSummary Map(TelephonyNumber number) => new(number.Id, number.TenantId.Value,
        number.LocationId?.Value, number.Provider, number.NormalizedNumber, number.InboundEnabled,
        number.OutboundEnabled, number.IsActive, number.Version);
}
