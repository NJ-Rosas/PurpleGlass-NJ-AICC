using PurpleGlass.Modules.CallManagement.Application;
using Twilio.Security;

namespace PurpleGlass.Adapters.Telephony.Twilio;

public sealed class TwilioWebhookVerifier(TwilioTelephonyOptions options) : ITelephonyWebhookVerifier
{
    public string Provider => "Twilio";

    public bool Verify(string requestUrl, IReadOnlyDictionary<string, string> parameters, string signature)
    {
        if (!options.CredentialsConfigured || string.IsNullOrWhiteSpace(signature)) return false;
        var validator = new RequestValidator(options.AuthToken);
        return validator.Validate(requestUrl, parameters.ToDictionary(pair => pair.Key, pair => pair.Value), signature);
    }
}
