namespace PurpleGlass.WebBff;

public static class VoiceProviderConfigurationValidator
{
    public static void Validate(
        string speechToText,
        string textToSpeech,
        string languageModel,
        bool realSpeechEnabled,
        bool realAiEnabled)
    {
        if (speechToText == "OpenAI" && !realSpeechEnabled)
            throw Invalid("SpeechToText:Provider=OpenAI requires Providers:EnableRealSpeech=true");
        if (textToSpeech == "OpenAI" && !realSpeechEnabled)
            throw Invalid("TextToSpeech:Provider=OpenAI requires Providers:EnableRealSpeech=true");
        if (languageModel == "OpenAI" && !realAiEnabled)
            throw Invalid("LanguageModel:Provider=OpenAI requires Providers:EnableRealAI=true");
    }

    private static InvalidOperationException Invalid(string detail) =>
        new($"voice_provider_configuration_invalid: {detail}.");
}
