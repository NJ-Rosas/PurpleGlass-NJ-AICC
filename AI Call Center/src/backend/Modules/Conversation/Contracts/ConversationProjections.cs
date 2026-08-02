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
    long Version,
    string StartingLanguage = "en-US",
    string LanguageReason = "fallback",
    DateTimeOffset? LanguageChangedAtUtc = null,
    long LanguageChangeSequence = 0);

public sealed record ConversationLanguageChangeProjection(
    Guid ChangeId,
    long Sequence,
    string PreviousLanguageCode,
    string LanguageCode,
    string Reason,
    decimal? DetectionConfidence,
    DateTimeOffset ChangedAtUtc);

public sealed record CompletedConversationSummary(
    Guid ConversationId,
    Guid CallId,
    string Summary,
    string? CallerIntent,
    string Outcome,
    bool FollowUpRequired,
    bool Escalated,
    DateTimeOffset GeneratedAtUtc);

public sealed record ConversationDetails(
    Guid ConversationId,
    Guid CallId,
    string State,
    string Language,
    bool Escalated,
    string? EscalationReason,
    IReadOnlyList<LiveTranscriptTurn> Transcript,
    CompletedConversationSummary? Summary,
    IReadOnlyList<ConversationLanguageChangeProjection>? LanguageChanges = null);
