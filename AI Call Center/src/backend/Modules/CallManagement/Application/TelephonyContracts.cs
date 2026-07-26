namespace PurpleGlass.Modules.CallManagement.Application;

public interface ITelephonyProvider
{
    string Name { get; }
    TelephonyProviderStatus Status { get; }
    Task<OutboundCallResult> StartOutboundCallAsync(OutboundCallTransport request, CancellationToken cancellationToken);
    Task<TelephonyProviderResult> HangupCallAsync(string providerCallId, CancellationToken cancellationToken);
}

public interface ITelephonyWebhookVerifier
{
    string Provider { get; }
    bool Verify(string requestUrl, IReadOnlyDictionary<string, string> parameters, string signature);
}

public sealed record TelephonyProviderStatus(bool Enabled, bool Configured, string State);

public sealed record OutboundCallTransport(
    Guid OperationId,
    Guid CallId,
    string FromNumber,
    string DestinationNumber,
    Uri AnswerUrl,
    Uri StatusCallbackUrl);

public sealed record OutboundCallResult(bool Succeeded, string? ProviderCallId, string? SafeErrorCode)
{
    public static OutboundCallResult Success(string providerCallId) => new(true, providerCallId, null);
    public static OutboundCallResult Failure(string code) => new(false, null, code);
}

public sealed record TelephonyProviderResult(bool Succeeded, string? SafeErrorCode)
{
    public static TelephonyProviderResult Success() => new(true, null);
    public static TelephonyProviderResult Failure(string code) => new(false, code);
}

public sealed record TelephonyConfigurationStatus(string Provider, bool Enabled, bool Configured, string State);

public sealed record TelephonyDispatch(
    Guid OperationId,
    string OperationType,
    Guid TenantId,
    Guid LocationId,
    Guid CallId,
    string Provider,
    string? ProviderCallId,
    string FromNumber,
    string ToNumber);
