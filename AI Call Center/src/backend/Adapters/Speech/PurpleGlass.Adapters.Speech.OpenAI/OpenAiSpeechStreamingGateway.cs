using System.Runtime.CompilerServices;
using OpenAI.Audio;

namespace PurpleGlass.Adapters.Speech.OpenAI;

public sealed record OpenAiSpeechStreamUpdate(
    ReadOnlyMemory<byte> Audio,
    bool Completed,
    int InputTokens = 0,
    int OutputTokens = 0);

public interface IOpenAiSpeechStreamingGateway
{
    IAsyncEnumerable<OpenAiSpeechStreamUpdate> GenerateAsync(
        string text,
        string voice,
        string? instructions,
        decimal speakingRate,
        CancellationToken cancellationToken);
}

public sealed class OpenAiSpeechStreamingGateway(AudioClient client) : IOpenAiSpeechStreamingGateway
{
#pragma warning disable OPENAI001
    public async IAsyncEnumerable<OpenAiSpeechStreamUpdate> GenerateAsync(
        string text,
        string voice,
        string? instructions,
        decimal speakingRate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = new SpeechGenerationOptions
        {
            ResponseFormat = GeneratedSpeechFormat.Pcm,
            SpeedRatio = (float)speakingRate,
            Instructions = instructions,
        };
        await foreach (StreamingSpeechUpdate update in client.GenerateSpeechStreamingAsync(
            text,
            new GeneratedSpeechVoice(voice),
            options,
            cancellationToken))
        {
            if (update is StreamingSpeechAudioDeltaUpdate delta)
            {
                byte[] bytes = delta.AudioBytes.ToArray();
                if (bytes.Length > 0) yield return new(bytes, Completed: false);
            }
            else if (update is StreamingSpeechAudioDoneUpdate done)
            {
                yield return new(
                    ReadOnlyMemory<byte>.Empty,
                    Completed: true,
                    done.Usage?.InputTokenCount ?? 0,
                    done.Usage?.OutputTokenCount ?? 0);
            }
        }
    }
#pragma warning restore OPENAI001
}
