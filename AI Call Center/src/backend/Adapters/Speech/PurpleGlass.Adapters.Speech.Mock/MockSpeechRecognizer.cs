using PurpleGlass.Modules.Conversation.Application;
using System.Text;

namespace PurpleGlass.Adapters.Speech.Mock;

public sealed class MockSpeechRecognizer(MockSpeechOptions options, TimeProvider timeProvider) : ISpeechRecognizer
{
    private static readonly string[] SpanishEvidence =
        ["hola", "necesito", "quiero", "dentista", "cita", "dolor", "hablar", "español", "ingles"];
    private static readonly string[] EnglishEvidence =
        ["hello", "need", "want", "dentist", "appointment", "pain", "speak", "english", "spanish"];
    public string AdapterKey => "mock-speech";

    public async Task<SpeechRecognitionResult> RecognizeAsync(
        SpeechRecognitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset started = timeProvider.GetUtcNow();
        if (options.RecognitionDelay > TimeSpan.Zero)
            await Task.Delay(options.RecognitionDelay, timeProvider, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.FailRecognition || request.Input.Simulation?.Equals("failure", StringComparison.OrdinalIgnoreCase) == true)
            return new(string.Empty, null, request.Language, started, timeProvider.GetUtcNow(), true,
                new RuntimeFailure("speech_recognition_failed", "I could not understand the audio.", true));

        if (request.Input.Simulation?.Equals("timeout", StringComparison.OrdinalIgnoreCase) == true)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        string recognizedText = request.AudioInput is null
            ? request.Input.Text.Trim()
            : DecodeAudio(request.AudioInput);
        if (string.IsNullOrWhiteSpace(recognizedText))
            return new(string.Empty, null, request.Language, started, timeProvider.GetUtcNow(), true,
                new RuntimeFailure("speech_audio_unsupported", "The fake speech provider requires synthetic text audio.", false));

        IReadOnlyList<string> detected = DetectLanguages(recognizedText);
        return new(recognizedText, options.RecognitionConfidence, request.Language,
            started, timeProvider.GetUtcNow(), true, DetectedLanguages: detected,
            DetectionConfidence: detected.Count == 1 ? options.RecognitionConfidence : null);
    }

    private static string DecodeAudio(SpeechAudioInput input) =>
        input.Format == AudioFormat.SyntheticText
            ? Encoding.UTF8.GetString(input.Audio.Span).Trim()
            : string.Empty;

    private static IReadOnlyList<string> DetectLanguages(string text)
    {
        string normalized = text.ToLowerInvariant();
        bool spanish = SpanishEvidence.Count(word => normalized.Contains(word, StringComparison.Ordinal)) >= 2;
        bool english = EnglishEvidence.Count(word => normalized.Contains(word, StringComparison.Ordinal)) >= 2;
        if (spanish && english) return ["en", "es"];
        if (spanish) return ["es"];
        if (english) return ["en"];
        return [];
    }
}
