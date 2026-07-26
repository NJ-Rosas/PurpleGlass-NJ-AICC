using PurpleGlass.Modules.CallManagement.Application;
using Twilio.Clients;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace PurpleGlass.Adapters.Telephony.Twilio;

public sealed class TwilioTelephonyProvider(TwilioTelephonyOptions options) : ITelephonyProvider
{
    public string Name => "Twilio";
    public TelephonyProviderStatus Status => new(true, options.IsConfigured,
        options.IsConfigured ? "configured" : "misconfigured");

    public async Task<OutboundCallResult> StartOutboundCallAsync(OutboundCallTransport request, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured) return OutboundCallResult.Failure("provider_misconfigured");
        try
        {
            ITwilioRestClient client = new TwilioRestClient(options.AccountSid, options.AuthToken);
            CallResource call = await CallResource.CreateAsync(
                to: new PhoneNumber(request.DestinationNumber),
                from: new PhoneNumber(request.FromNumber),
                url: request.AnswerUrl,
                statusCallback: request.StatusCallbackUrl,
                statusCallbackMethod: global::Twilio.Http.HttpMethod.Post,
                statusCallbackEvent: ["initiated", "ringing", "answered", "completed"],
                client: client);
            return string.IsNullOrWhiteSpace(call.Sid)
                ? OutboundCallResult.Failure("provider_invalid_response")
                : OutboundCallResult.Success(call.Sid);
        }
        catch (ApiException exception)
        {
            return OutboundCallResult.Failure(SafeCode(exception.Status));
        }
        catch (HttpRequestException)
        {
            return OutboundCallResult.Failure("provider_network_error");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OutboundCallResult.Failure("provider_timeout");
        }
    }

    public async Task<TelephonyProviderResult> HangupCallAsync(string providerCallId, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured) return TelephonyProviderResult.Failure("provider_misconfigured");
        try
        {
            ITwilioRestClient client = new TwilioRestClient(options.AccountSid, options.AuthToken);
            _ = await CallResource.UpdateAsync(
                pathSid: providerCallId,
                status: CallResource.UpdateStatusEnum.Completed,
                client: client);
            return TelephonyProviderResult.Success();
        }
        catch (ApiException exception)
        {
            return TelephonyProviderResult.Failure(SafeCode(exception.Status));
        }
        catch (HttpRequestException)
        {
            return TelephonyProviderResult.Failure("provider_network_error");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TelephonyProviderResult.Failure("provider_timeout");
        }
    }

    private static string SafeCode(int status) => status switch
    {
        400 => "provider_rejected",
        401 or 403 => "provider_authentication_failed",
        404 => "provider_call_not_found",
        429 => "provider_rate_limited",
        >= 500 => "provider_unavailable",
        _ => "provider_error",
    };
}
