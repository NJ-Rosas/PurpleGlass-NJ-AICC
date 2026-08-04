using System.Runtime.CompilerServices;

namespace PurpleGlass.Modules.Conversation.Application;

public sealed record RuntimeInvocationContext(
    Guid TenantId,
    Guid LocationId,
    Guid CallId,
    Guid ConversationId,
    Guid CorrelationId,
    Guid? CausationId,
    string? TraceId);

public sealed record SimulatedUtteranceInput(string Text, string? Simulation = null);

public sealed record RuntimeFailure(string Code, string SafeMessage, bool Retryable);

public sealed record SpeechRecognitionResponseDiagnostic(
    string ProviderOperation,
    string Stage,
    string ResultCategory,
    string HttpStatusCategory,
    string ContentTypeCategory,
    string ResponseShapeCategory,
    bool TranscriptPresent,
    bool LanguageMetadataPresent,
    string LanguageMetadataCategory);

public sealed record AudioFormat(string Encoding, int SampleRateHz, int Channels, int BitsPerSample)
{
    public static AudioFormat Pcm16(int sampleRateHz = 8_000, int channels = 1) =>
        new("audio/pcm", sampleRateHz, channels, 16);

    public static AudioFormat SyntheticText => new("audio/x-purpleglass-text", 1, 1, 8);

    public AudioFormat Validate()
    {
        if (string.IsNullOrWhiteSpace(Encoding) || Encoding.Length > 100)
            throw new ArgumentException("A bounded audio encoding is required.", nameof(Encoding));
        if (SampleRateHz is < 1 or > 192_000)
            throw new ArgumentOutOfRangeException(nameof(SampleRateHz));
        if (Channels is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(Channels));
        if (BitsPerSample is not (8 or 16 or 24 or 32))
            throw new ArgumentOutOfRangeException(nameof(BitsPerSample));
        return this;
    }
}

public sealed record SpeechAudioInput(
    AudioFormat Format,
    ReadOnlyMemory<byte> Audio,
    string IdempotencyKey);

public sealed record SynthesizedAudioChunk(
    long Sequence,
    AudioFormat Format,
    ReadOnlyMemory<byte> Audio,
    bool IsFinal = false);

public sealed record SpeechRecognitionRequest(
    RuntimeInvocationContext Context,
    string Language,
    SimulatedUtteranceInput Input,
    SpeechAudioInput? AudioInput = null);

public sealed record SpeechRecognitionResult(
    string RecognizedText,
    decimal? Confidence,
    string Language,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    bool IsFinal,
    RuntimeFailure? Failure = null,
    IReadOnlyList<string>? DetectedLanguages = null,
    decimal? DetectionConfidence = null,
    SpeechRecognitionResponseDiagnostic? ResponseDiagnostic = null)
{
    public IReadOnlyList<string> DetectedLanguageCodes => DetectedLanguages ?? [];
}

public interface ISpeechRecognizer
{
    string AdapterKey { get; }

    Task<SpeechRecognitionResult> RecognizeAsync(
        SpeechRecognitionRequest request,
        CancellationToken cancellationToken);
}

public sealed record SpeechSynthesisRequest(
    RuntimeInvocationContext Context,
    string Text,
    string Language,
    VoiceConfiguration Voice);

public sealed record SpeechSynthesisResult(
    string AudioReference,
    string ContentType,
    TimeSpan? Duration,
    string VoiceId,
    IReadOnlyDictionary<string, string> Metadata,
    RuntimeFailure? Failure = null,
    IReadOnlyList<SynthesizedAudioChunk>? AudioChunks = null);

public sealed record SpeechSynthesisStreamUpdate(
    SynthesizedAudioChunk? AudioChunk = null,
    SpeechSynthesisResult? Completion = null)
{
    public static SpeechSynthesisStreamUpdate Audio(SynthesizedAudioChunk chunk) => new(chunk);

    public static SpeechSynthesisStreamUpdate Completed(SpeechSynthesisResult result) => new(Completion: result);
}

public interface ISpeechSynthesizer
{
    string AdapterKey { get; }

    Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken);

    async IAsyncEnumerable<SpeechSynthesisStreamUpdate> SynthesizeStreamingAsync(
        SpeechSynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SpeechSynthesisResult result = await SynthesizeAsync(request, cancellationToken);
        if (result.Failure is null)
        {
            foreach (SynthesizedAudioChunk chunk in result.AudioChunks ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return SpeechSynthesisStreamUpdate.Audio(chunk);
            }
        }
        yield return SpeechSynthesisStreamUpdate.Completed(result);
    }
}
