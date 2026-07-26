using System.Collections.Concurrent;
using PurpleGlass.Modules.CallManagement.Application;

namespace PurpleGlass.Adapters.Telephony.Fake;

public sealed class FakeTelephonyProvider : ITelephonyProvider, ITelephonyWebhookVerifier
{
    private readonly ConcurrentDictionary<Guid, string> calls = new();
    public string Name => "Fake";
    public string Provider => Name;
    public TelephonyProviderStatus Status => new(true, true, "configured");
    public string? NextFailureCode { get; set; }
    public IReadOnlyDictionary<Guid, string> Calls => calls;

    public Task<OutboundCallResult> StartOutboundCallAsync(OutboundCallTransport request, CancellationToken cancellationToken)
    {
        if (NextFailureCode is { } failure)
        {
            NextFailureCode = null;
            return Task.FromResult(OutboundCallResult.Failure(failure));
        }
        string providerCallId = calls.GetOrAdd(request.OperationId, id => $"FA{id:N}");
        return Task.FromResult(OutboundCallResult.Success(providerCallId));
    }

    public Task<TelephonyProviderResult> HangupCallAsync(string providerCallId, CancellationToken cancellationToken) =>
        Task.FromResult(calls.Values.Contains(providerCallId, StringComparer.Ordinal)
            ? TelephonyProviderResult.Success()
            : TelephonyProviderResult.Failure("provider_call_not_found"));

    public bool Verify(string requestUrl, IReadOnlyDictionary<string, string> parameters, string signature) =>
        string.Equals(signature, "fake-valid-signature", StringComparison.Ordinal);
}
