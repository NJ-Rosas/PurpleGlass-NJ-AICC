using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.Speech.Mock;

public sealed class MockSpeechRecognizer(MockSpeechOptions options, TimeProvider timeProvider) : ISpeechRecognizer
{
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

        return new(request.Input.Text.Trim(), options.RecognitionConfidence, request.Language,
            started, timeProvider.GetUtcNow(), true);
    }
}
