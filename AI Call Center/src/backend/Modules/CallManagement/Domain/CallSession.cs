namespace PurpleGlass.Modules.CallManagement.Domain;

public sealed class CallSession
{
    private CallSession()
    {
    }

    private CallSession(
        CallSessionId id,
        TenantId tenantId,
        LocationId locationId,
        CallDirection direction,
        string providerCallId,
        string fromNumber,
        string toNumber,
        Guid correlationId,
        DateTimeOffset createdAtUtc)
    {
        if (tenantId.Value == Guid.Empty)
        {
            throw new ArgumentException("Tenant identifier is required.", nameof(tenantId));
        }

        if (locationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Location identifier is required.", nameof(locationId));
        }

        Id = id;
        TenantId = tenantId;
        LocationId = locationId;
        Direction = direction;
        ProviderCallId = RequireValue(providerCallId, nameof(providerCallId), 200);
        FromNumber = RequireValue(fromNumber, nameof(fromNumber), 32);
        ToNumber = RequireValue(toNumber, nameof(toNumber), 32);
        CorrelationId = correlationId == Guid.Empty ? Guid.NewGuid() : correlationId;
        CreatedAtUtc = createdAtUtc;
        State = direction == CallDirection.Inbound ? CallState.Received : CallState.Requested;
        Version = 1;
    }

    public CallSessionId Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public LocationId LocationId { get; private set; }

    public CallDirection Direction { get; private set; }

    public CallState State { get; private set; }

    public string ProviderCallId { get; private set; } = string.Empty;

    public string FromNumber { get; private set; } = string.Empty;

    public string ToNumber { get; private set; } = string.Empty;

    public Guid CorrelationId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? AnsweredAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? Outcome { get; private set; }

    public string? RecordingReference { get; private set; }

    public DateTimeOffset? RecordingRetentionEligibleAtUtc { get; private set; }

    public long Version { get; private set; }

    public static CallSession ReceiveInbound(
        CallSessionId id,
        TenantId tenantId,
        LocationId locationId,
        string providerCallId,
        string fromNumber,
        string toNumber,
        Guid correlationId,
        DateTimeOffset receivedAtUtc) =>
        new(id, tenantId, locationId, CallDirection.Inbound, providerCallId, fromNumber, toNumber, correlationId, receivedAtUtc);

    public static CallSession RequestOutbound(
        CallSessionId id,
        TenantId tenantId,
        LocationId locationId,
        string providerCallId,
        string fromNumber,
        string toNumber,
        Guid correlationId,
        DateTimeOffset requestedAtUtc) =>
        new(id, tenantId, locationId, CallDirection.Outbound, providerCallId, fromNumber, toNumber, correlationId, requestedAtUtc);

    public void MarkRinging()
    {
        EnsureState(CallState.Requested);
        TransitionTo(CallState.Ringing);
    }

    public void Answer(DateTimeOffset answeredAtUtc)
    {
        if (State is not (CallState.Received or CallState.Ringing))
        {
            throw InvalidTransition(CallState.Answered);
        }

        AnsweredAtUtc = answeredAtUtc;
        TransitionTo(CallState.Answered);
    }

    public void StartConversation()
    {
        EnsureState(CallState.Answered);
        TransitionTo(CallState.InConversation);
    }

    public void Complete(string outcome, DateTimeOffset completedAtUtc)
    {
        if (State is not (CallState.Answered or CallState.InConversation))
        {
            throw InvalidTransition(CallState.Completed);
        }

        Outcome = RequireValue(outcome, nameof(outcome), 100);
        CompletedAtUtc = completedAtUtc;
        TransitionTo(CallState.Completed);
    }

    public void Fail(string outcome, DateTimeOffset failedAtUtc)
    {
        if (State is CallState.Completed or CallState.Failed)
        {
            throw InvalidTransition(CallState.Failed);
        }

        Outcome = RequireValue(outcome, nameof(outcome), 100);
        CompletedAtUtc = failedAtUtc;
        TransitionTo(CallState.Failed);
    }

    public void AttachRecording(string recordingReference, DateTimeOffset retentionEligibleAtUtc)
    {
        if (State is not (CallState.Completed or CallState.Failed))
        {
            throw new InvalidOperationException("A recording can be attached only after the call ends.");
        }

        RecordingReference = RequireValue(recordingReference, nameof(recordingReference), 500);
        RecordingRetentionEligibleAtUtc = retentionEligibleAtUtc;
        Version++;
    }

    private void TransitionTo(CallState target)
    {
        State = target;
        Version++;
    }

    private void EnsureState(CallState expected)
    {
        if (State != expected)
        {
            throw InvalidTransition(expected);
        }
    }

    private InvalidOperationException InvalidTransition(CallState target) =>
        new($"Call cannot transition from {State} to {target}.");

    private static string RequireValue(string value, string parameterName, int maximumLength)
    {
        string normalized = value.Trim();
        if (normalized.Length is 0 || normalized.Length > maximumLength)
        {
            throw new ArgumentException($"Value must contain between 1 and {maximumLength} characters.", parameterName);
        }

        return normalized;
    }
}
