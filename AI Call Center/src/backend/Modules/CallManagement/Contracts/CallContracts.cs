namespace PurpleGlass.Modules.CallManagement.Contracts;

public sealed record CallReceived(
    Guid CallId,
    string Direction,
    DateTimeOffset ReceivedAtUtc);

public sealed record CallAnswered(Guid CallId, DateTimeOffset AnsweredAtUtc);

public sealed record CallStateChanged(
    Guid CallId,
    string PreviousState,
    string CurrentState,
    long Version);

public sealed record CallCompleted(
    Guid CallId,
    string Outcome,
    DateTimeOffset CompletedAtUtc,
    string? RecordingReference);

public sealed record CallFailed(
    Guid CallId,
    string Reason,
    DateTimeOffset FailedAtUtc);

public sealed record StartOutboundCallRequest(string DestinationNumber);

public sealed record CallSummary(
    Guid CallId,
    string Direction,
    string State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Outcome,
    string? Summary,
    string? RecordingReference,
    long Version);
