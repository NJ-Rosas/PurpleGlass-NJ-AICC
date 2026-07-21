namespace PurpleGlass.Modules.Conversation.Contracts;

public sealed record ConversationStarted(
    Guid ConversationId,
    Guid CallId,
    string Language,
    string ConfigurationVersion,
    DateTimeOffset StartedAtUtc);

public sealed record UserSpeechRecognized(
    Guid ConversationId,
    Guid TurnId,
    int SequenceNumber,
    string Text,
    decimal? RecognitionConfidence,
    DateTimeOffset RecognizedAtUtc);

public sealed record AIResponseGenerated(
    Guid ConversationId,
    Guid TurnId,
    int SequenceNumber,
    string Text,
    string ConfigurationVersion,
    DateTimeOffset GeneratedAtUtc);

public sealed record EscalationTriggered(
    Guid ConversationId,
    string Reason,
    DateTimeOffset TriggeredAtUtc);

public sealed record SummaryGenerated(
    Guid ConversationId,
    string Summary,
    string? CallerIntent,
    string Outcome,
    bool FollowUpRequired,
    bool Escalated,
    string ConfigurationVersion,
    DateTimeOffset GeneratedAtUtc);

public sealed record ConversationCompleted(Guid ConversationId, DateTimeOffset CompletedAtUtc);

public sealed record ConversationFailed(Guid ConversationId, DateTimeOffset FailedAtUtc);
