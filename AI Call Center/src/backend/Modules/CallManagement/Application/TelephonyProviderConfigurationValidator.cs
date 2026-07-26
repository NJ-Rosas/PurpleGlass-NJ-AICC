namespace PurpleGlass.Modules.CallManagement.Application;

public static class TelephonyProviderConfigurationValidator
{
    public static void Validate(string provider, bool realTelephonyEnabled)
    {
        if (provider.Equals("Twilio", StringComparison.OrdinalIgnoreCase) && !realTelephonyEnabled)
            throw new InvalidOperationException(
                "telephony_provider_configuration_invalid: Telephony:Provider=Twilio requires Providers:EnableRealTelephony=true.");
    }
}
