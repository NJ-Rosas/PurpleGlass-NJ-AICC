namespace PurpleGlass.Modules.Conversation.Application;

public sealed class DisabledAiConversationRuntime : IAiConversationRuntime
{
    public string AdapterKey => "disabled";

    public Task<AiResponseResult> GenerateAsync(AiResponseRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new AiResponseResult(
            string.Empty, null, false, null, false,
            new AiUsageMetadata(0, 0, "none"), request.Configuration.Version,
            new RuntimeFailure("language_model_disabled", "The language model provider is disabled.", false)));
}

public sealed class DisabledSpeechProvider : ISpeechRecognizer, ISpeechSynthesizer
{
    public string AdapterKey => "disabled";

    public Task<SpeechRecognitionResult> RecognizeAsync(
        SpeechRecognitionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SpeechRecognitionResult(
            string.Empty, null, request.Language, null, null, true,
            new RuntimeFailure("speech_recognition_disabled", "The speech recognition provider is disabled.", false)));

    public Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SpeechSynthesisResult(
            string.Empty, "application/octet-stream", null, request.Voice.VoiceId,
            new Dictionary<string, string>(),
            new RuntimeFailure("speech_synthesis_disabled", "The speech synthesis provider is disabled.", false)));
}
