namespace PurpleGlass.WebBff;

public sealed record RealtimeVoiceRuntimeStatus(bool Ready, string State)
{
    public static RealtimeVoiceRuntimeStatus From(
        bool voiceEnabled,
        string speechRecognitionProvider,
        string speechSynthesisProvider)
    {
        if (!voiceEnabled) return new(false, "voice_disabled");
        return speechRecognitionProvider == "OpenAI" && speechSynthesisProvider == "OpenAI"
            ? new(true, "ready")
            : new(false, "voice_media_provider_incompatible");
    }
}
