namespace PurpleGlass.Adapters.Telephony.Twilio;

public sealed class TwilioTelephonyOptions
{
    public string AccountSid { get; init; } = string.Empty;
    public string AuthToken { get; init; } = string.Empty;
    public string PublicBaseUrl { get; init; } = string.Empty;

    public bool CredentialsConfigured => AccountSid.StartsWith("AC", StringComparison.Ordinal)
        && AccountSid.Length == 34 && !string.IsNullOrWhiteSpace(AuthToken);

    public bool IsConfigured => CredentialsConfigured
        && Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out Uri? publicBaseUrl)
        && publicBaseUrl.Scheme == Uri.UriSchemeHttps;
}
