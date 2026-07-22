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

public sealed record SpeechRecognitionRequest(
    RuntimeInvocationContext Context,
    string Language,
    SimulatedUtteranceInput Input);

public sealed record SpeechRecognitionResult(
    string RecognizedText,
    decimal? Confidence,
    string Language,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    bool IsFinal,
    RuntimeFailure? Failure = null);

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
    RuntimeFailure? Failure = null);

public interface ISpeechSynthesizer
{
    string AdapterKey { get; }

    Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken);
}
