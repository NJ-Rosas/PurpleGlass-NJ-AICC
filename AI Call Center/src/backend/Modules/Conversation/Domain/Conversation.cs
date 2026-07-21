namespace PurpleGlass.Modules.Conversation.Domain;

public sealed class Conversation
{
    private readonly List<ConversationTurn> turns = [];

    private Conversation()
    {
    }

    public Conversation(
        ConversationId id,
        CallSessionReference callSession,
        TenantId tenantId,
        LocationId locationId,
        CorrelationId correlationId,
        string configurationVersion,
        string language,
        DateTimeOffset createdAtUtc)
    {
        ValidateIdentifier(id.Value, nameof(id));
        ValidateIdentifier(callSession.Value, nameof(callSession));
        ValidateIdentifier(tenantId.Value, nameof(tenantId));
        ValidateIdentifier(locationId.Value, nameof(locationId));
        ValidateIdentifier(correlationId.Value, nameof(correlationId));

        Id = id;
        CallSession = callSession;
        TenantId = tenantId;
        LocationId = locationId;
        CorrelationId = correlationId;
        ConfigurationVersion = RequireValue(configurationVersion, nameof(configurationVersion), 100);
        Language = RequireValue(language, nameof(language), 35);
        CreatedAtUtc = createdAtUtc;
        State = ConversationState.Created;
        Version = 1;
    }

    public ConversationId Id { get; private set; }

    public CallSessionReference CallSession { get; private set; }

    public TenantId TenantId { get; private set; }

    public LocationId LocationId { get; private set; }

    public CorrelationId CorrelationId { get; private set; }

    public string ConfigurationVersion { get; private set; } = string.Empty;

    public string Language { get; private set; } = string.Empty;

    public ConversationState State { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public ConversationSummary? Summary { get; private set; }

    public bool Escalated { get; private set; }

    public string? EscalationReason { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<ConversationTurn> Turns => turns;

    public void Activate(DateTimeOffset startedAtUtc)
    {
        EnsureState(ConversationState.Created);
        StartedAtUtc = startedAtUtc;
        TransitionTo(ConversationState.Active);
    }

    public ConversationTurn AddTurn(
        ConversationTurnId turnId,
        SpeakerRole speaker,
        string text,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc = null,
        DateTimeOffset? endedAtUtc = null,
        decimal? recognitionConfidence = null,
        bool safetyFlagged = false,
        bool escalationFlagged = false)
    {
        EnsureState(ConversationState.Active);

        if (turns.Any(turn => turn.Id == turnId))
        {
            throw new InvalidOperationException("A conversation turn with this identifier already exists.");
        }

        var turn = new ConversationTurn(
            turnId,
            Id,
            speaker,
            turns.Count + 1,
            text,
            startedAtUtc,
            endedAtUtc,
            recognitionConfidence,
            createdAtUtc,
            safetyFlagged,
            escalationFlagged);

        turns.Add(turn);
        Version++;
        return turn;
    }

    public void RecordEscalation(string reason)
    {
        EnsureState(ConversationState.Active);
        EscalationReason = RequireValue(reason, nameof(reason), 500);
        Escalated = true;
        Version++;
    }

    public void Complete(ConversationSummary summary, DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(summary);
        EnsureState(ConversationState.Active);

        if (!string.Equals(summary.ConfigurationVersion, ConfigurationVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Summary configuration version must match the conversation configuration version.");
        }

        if (summary.Escalated != Escalated)
        {
            throw new InvalidOperationException("Summary escalation status must match the conversation escalation state.");
        }

        Summary = summary;
        CompletedAtUtc = completedAtUtc;
        TransitionTo(ConversationState.Completed);
    }

    public void Fail(DateTimeOffset failedAtUtc)
    {
        if (State is ConversationState.Completed or ConversationState.Failed)
        {
            throw InvalidTransition(ConversationState.Failed);
        }

        CompletedAtUtc = failedAtUtc;
        TransitionTo(ConversationState.Failed);
    }

    private void TransitionTo(ConversationState target)
    {
        State = target;
        Version++;
    }

    private void EnsureState(ConversationState expected)
    {
        if (State != expected)
        {
            throw InvalidTransition(expected);
        }
    }

    private InvalidOperationException InvalidTransition(ConversationState target) =>
        new($"Conversation cannot transition from {State} to {target}.");

    private static void ValidateIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier is required.", parameterName);
        }
    }

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
