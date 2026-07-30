namespace PurpleGlass.Modules.Conversation.Application;

public enum VoiceSessionState
{
    Connecting = 1,
    Listening = 2,
    Thinking = 3,
    Speaking = 4,
    Interrupted = 5,
    Failed = 6,
    Ended = 7,
}

public sealed record VoiceSessionIdentity(
    Guid TenantId,
    Guid LocationId,
    Guid CallId,
    string Direction,
    string Provider,
    string ProviderCallId,
    string ProviderMediaStreamId,
    Guid CorrelationId);

public sealed record RealtimeAudioFrame(
    long Sequence,
    AudioFormat Format,
    ReadOnlyMemory<byte> Audio,
    DateTimeOffset ReceivedAtUtc,
    bool SpeechStarted = false,
    bool EndOfUtterance = false);

public interface IRealtimeAudioTransport : IAsyncDisposable
{
    string Provider { get; }
    string ProviderCallId { get; }
    string ProviderMediaStreamId { get; }
    AudioFormat InputFormat { get; }

    IAsyncEnumerable<RealtimeAudioFrame> ReceiveAsync(CancellationToken cancellationToken);

    ValueTask WaitForMediaReadyAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask<RealtimeAudioSendResult> SendAsync(SynthesizedAudioChunk chunk, CancellationToken cancellationToken);

    ValueTask<RealtimePlaybackCompletion> WaitForPlaybackCompletionAsync(
        string responseId,
        CancellationToken cancellationToken);

    ValueTask ClearPlaybackAsync(CancellationToken cancellationToken);

    ValueTask CompleteAsync(string reason, CancellationToken cancellationToken);
}

public sealed record RealtimePlaybackCompletion(
    string ResponseId,
    bool MarkAcknowledged,
    bool Cleared,
    double ElapsedFromFirstMediaMs,
    double ElapsedFromMarkSentMs);

public sealed record RealtimeAudioSendResult(
    string ResponseId,
    int SourcePcmBytes,
    int SourceSamples,
    int ResampledSamples,
    int MuLawBytes,
    int MediaMessageCount,
    bool MarkSent,
    double MaximumBufferedAudioDurationMs)
{
    public static RealtimeAudioSendResult Pending { get; } = new(string.Empty, 0, 0, 0, 0, 0, false, 0);
}

public sealed record VoiceSessionStateChange(
    VoiceSessionIdentity Identity,
    Guid? ConversationId,
    VoiceSessionState State,
    string? SafeCode,
    DateTimeOffset ChangedAtUtc);

public interface IVoiceSessionStateSink
{
    ValueTask PublishAsync(VoiceSessionStateChange change, CancellationToken cancellationToken);
}

public sealed class NullVoiceSessionStateSink : IVoiceSessionStateSink
{
    public ValueTask PublishAsync(VoiceSessionStateChange change, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed record FinalizedVoiceUtterance(
    Guid TurnId,
    long FirstSequence,
    long LastSequence,
    AudioFormat Format,
    ReadOnlyMemory<byte> Audio,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    int InboundFrames = 1,
    TimeSpan? Duration = null,
    TimeSpan? QualifiedSpeechDuration = null,
    double NoiseFloor = 0,
    double EnergyMetric = 0);
