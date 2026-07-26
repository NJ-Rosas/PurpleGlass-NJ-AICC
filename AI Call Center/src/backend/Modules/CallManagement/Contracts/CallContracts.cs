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

public sealed record OutboundCallRequested(
    Guid CallId,
    DateTimeOffset RequestedAtUtc);

public sealed record CallHangupRequested(Guid CallId, DateTimeOffset RequestedAtUtc);

public sealed record CallProviderIdentityAssigned(Guid CallId, string Provider, DateTimeOffset AssignedAtUtc);

public sealed record CallEligibility(
    Guid CallId,
    Guid TenantId,
    Guid LocationId,
    string State,
    bool ConversationAllowed);

public interface ICallEligibilityQuery
{
    Task<CallEligibility?> GetEligibilityAsync(
        Guid tenantId,
        Guid callId,
        CancellationToken cancellationToken);
}

public sealed record StartOutboundCallRequest(string DestinationNumber);

public sealed record TelephonyNumberRequest(
    Guid? LocationId,
    string Provider,
    string Number,
    string? ProviderNumberId,
    bool InboundEnabled,
    bool OutboundEnabled,
    bool Active);

public sealed record TelephonyNumberSummary(
    Guid Id,
    Guid TenantId,
    Guid? LocationId,
    string Provider,
    string Number,
    bool InboundEnabled,
    bool OutboundEnabled,
    bool Active,
    long Version);

public sealed record CallSummary(
    Guid CallId,
    string Direction,
    string State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Outcome,
    string? Summary,
    string? RecordingReference,
    long Version,
    string Provider = "Synthetic",
    string FromNumber = "",
    string ToNumber = "");
