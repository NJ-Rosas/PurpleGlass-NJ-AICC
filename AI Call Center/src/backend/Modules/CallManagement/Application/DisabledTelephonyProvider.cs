namespace PurpleGlass.Modules.CallManagement.Application;

public sealed class DisabledTelephonyProvider : ITelephonyProvider, ITelephonyWebhookVerifier
{
    public string Name => "None";
    public string Provider => Name;
    public TelephonyProviderStatus Status => new(false, false, "disabled");
    public Task<OutboundCallResult> StartOutboundCallAsync(OutboundCallTransport request, CancellationToken cancellationToken) =>
        Task.FromResult(OutboundCallResult.Failure("provider_disabled"));
    public Task<TelephonyProviderResult> HangupCallAsync(string providerCallId, CancellationToken cancellationToken) =>
        Task.FromResult(TelephonyProviderResult.Failure("provider_disabled"));
    public bool Verify(string requestUrl, IReadOnlyDictionary<string, string> parameters, string signature) => false;
}
