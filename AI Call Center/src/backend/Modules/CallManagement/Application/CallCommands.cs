using PurpleGlass.Modules.CallManagement.Domain;

namespace PurpleGlass.Modules.CallManagement.Application;

public sealed record RegisterInboundCall(
    Guid TenantId,
    Guid LocationId,
    string ProviderCallId,
    string FromNumber,
    string ToNumber,
    Guid CorrelationId);

public sealed record RequestOutboundCall(
    Guid TenantId,
    Guid LocationId,
    string IdempotencyKey,
    string FromNumber,
    string ToNumber,
    Guid CorrelationId);

public sealed record ChangeCallState(Guid TenantId, Guid CallId, long ExpectedVersion);

public sealed record CompleteCall(
    Guid TenantId,
    Guid CallId,
    long ExpectedVersion,
    string Outcome);

public sealed record FailCall(
    Guid TenantId,
    Guid CallId,
    long ExpectedVersion,
    string Reason);

public sealed record AttachCallRecording(
    Guid TenantId,
    Guid CallId,
    long ExpectedVersion,
    RecordingMetadata Recording);
