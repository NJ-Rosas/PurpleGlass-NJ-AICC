using System.Security.Cryptography;
using System.Text;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.Speech.Mock;

public sealed class MockSpeechSynthesizer(MockSpeechOptions options, TimeProvider timeProvider) : ISpeechSynthesizer
{
    public string AdapterKey => "mock-speech";

    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (options.SynthesisDelay > TimeSpan.Zero)
            await Task.Delay(options.SynthesisDelay, timeProvider, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.FailSynthesis)
            return Failure(request.Voice.VoiceId, "speech_synthesis_failed", "The response audio could not be generated.");
        if (!options.SupportedVoices.Contains(request.Voice.VoiceId))
            return Failure(request.Voice.VoiceId, "voice_not_supported", "The configured voice is unavailable.");

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.Voice.VoiceId}|{request.Language}|{request.Text}")))[..16].ToLowerInvariant();
        return new(
            $"memory://mock-speech/{hash}",
            "audio/x-purpleglass-simulator",
            TimeSpan.FromMilliseconds(Math.Max(250, request.Text.Length * 35)),
            request.Voice.VoiceId,
            new Dictionary<string, string> { ["adapter"] = AdapterKey, ["quality"] = "synthetic" });
    }

    private static SpeechSynthesisResult Failure(string voiceId, string code, string message) =>
        new(string.Empty, "application/octet-stream", null, voiceId,
            new Dictionary<string, string>(), new RuntimeFailure(code, message, false));
}
