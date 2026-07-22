using PurpleGlass.Modules.CallManagement.Domain;

namespace PurpleGlass.Modules.CallManagement.Application;

public sealed record RegisterInboundCall(
    Guid TenantId,
    Guid LocationId,
    string ProviderCallId,
    string FromNumber,
    string ToNumber,
    Guid CorrelationId,
    Guid? CausationId = null,
    string? TraceId = null);

public sealed record RequestOutboundCall(
    Guid TenantId,
    Guid LocationId,
    string IdempotencyKey,
    string FromNumber,
    string ToNumber,
    Guid CorrelationId,
    Guid? CausationId = null,
    string? TraceId = null);

public sealed record ChangeCallState(
    Guid TenantId,
    Guid CallId,
    long ExpectedVersion,
    Guid? CausationId = null,
    string? TraceId = null);

public sealed record CompleteCall(
    Guid TenantId,
    Guid CallId,
    long ExpectedVersion,
    string Outcome,
    Guid? CausationId = null,
    string? TraceId = null);

public sealed record FailCall(
    Guid TenantId,
    Guid CallId,
    long ExpectedVersion,
    string Reason,
    Guid? CausationId = null,
    string? TraceId = null);

public sealed record AttachCallRecording(
    Guid TenantId,
    Guid CallId,
    long ExpectedVersion,
    RecordingMetadata Recording,
    Guid? CausationId = null,
    string? TraceId = null);
