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
    string? TraceId = null,
    string Provider = "Synthetic");

public sealed record RequestOutboundCall(
    Guid TenantId,
    Guid LocationId,
    string IdempotencyKey,
    string FromNumber,
    string ToNumber,
    Guid CorrelationId,
    Guid? CausationId = null,
    string? TraceId = null,
    string Provider = "Synthetic");

public sealed record RequestTransportOutboundCall(
    Guid TenantId,
    Guid LocationId,
    string IdempotencyKey,
    string DestinationNumber,
    Guid CorrelationId,
    Guid? CausationId = null,
    string? TraceId = null);

public sealed record AssignProviderCallIdentity(
    Guid TenantId,
    Guid CallId,
    string ProviderCallId,
    string? ProviderParentCallId = null);

public sealed record ApplyProviderCallStatus(
    string Provider,
    string ProviderCallId,
    string ProviderStatus,
    string EventId,
    Guid? CorrelationId = null);

public sealed record RequestCallHangup(Guid TenantId, Guid LocationId, Guid CallId);

public sealed record ConfigureTelephonyNumber(
    Guid TenantId,
    Guid? LocationId,
    string Provider,
    string Number,
    string? ProviderNumberId,
    bool InboundEnabled,
    bool OutboundEnabled,
    bool Active);

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
