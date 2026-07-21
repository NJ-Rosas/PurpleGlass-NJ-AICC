namespace PurpleGlass.Modules.Conversation.Contracts;

public sealed record LiveTranscriptTurn(
    Guid ConversationId,
    Guid CallId,
    Guid TurnId,
    string Speaker,
    int SequenceNumber,
    string Text,
    DateTimeOffset CreatedAtUtc,
    bool SafetyFlagged,
    bool EscalationFlagged);

public sealed record ConversationStatusProjection(
    Guid ConversationId,
    Guid CallId,
    string State,
    string Language,
    bool Escalated,
    long Version);

public sealed record CompletedConversationSummary(
    Guid ConversationId,
    Guid CallId,
    string Summary,
    string? CallerIntent,
    string Outcome,
    bool FollowUpRequired,
    bool Escalated,
    DateTimeOffset GeneratedAtUtc);
