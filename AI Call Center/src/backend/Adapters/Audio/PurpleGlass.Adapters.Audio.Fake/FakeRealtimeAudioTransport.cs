using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.Audio.Fake;

public sealed class FakeRealtimeAudioTransport : IRealtimeAudioTransport
{
    private const int MaximumCapturedOutputChunks = 256;
    private readonly Channel<RealtimeAudioFrame> inbound;
    private readonly List<SynthesizedAudioChunk> outbound = [];
    private readonly object synchronization = new();
    private long nextSequence;
    private long responseSequence;
    private readonly Dictionary<string, TaskCompletionSource<RealtimePlaybackCompletion>> pendingPlayback = [];
    private TaskCompletionSource mediaReady = CompletedSignal();
    private int mediaReadyWaitCount;
    private bool disposed;

    public FakeRealtimeAudioTransport(
        string providerCallId,
        string? mediaStreamId = null,
        int inputCapacity = 32)
    {
        if (string.IsNullOrWhiteSpace(providerCallId))
            throw new ArgumentException("Provider call identity is required.", nameof(providerCallId));
        if (inputCapacity is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(inputCapacity));
        ProviderCallId = providerCallId.Trim();
        ProviderMediaStreamId = string.IsNullOrWhiteSpace(mediaStreamId)
            ? $"FM{Guid.NewGuid():N}" : mediaStreamId.Trim();
        inbound = Channel.CreateBounded<RealtimeAudioFrame>(new BoundedChannelOptions(inputCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public string Provider => "Fake";
    public string ProviderCallId { get; }
    public string ProviderMediaStreamId { get; }
    public AudioFormat InputFormat => AudioFormat.SyntheticText;
    public int ClearPlaybackCount { get; private set; }
    public string? CompletionReason { get; private set; }
    public bool AutoAcknowledgePlayback { get; set; } = true;
    public int MediaReadyWaitCount => Volatile.Read(ref mediaReadyWaitCount);

    public IReadOnlyList<SynthesizedAudioChunk> OutboundChunks
    {
        get { lock (synchronization) return outbound.ToArray(); }
    }

    public async ValueTask QueueUtteranceAsync(
        string text,
        long? sequence = null,
        CancellationToken cancellationToken = default)
    {
        byte[] audio = Encoding.UTF8.GetBytes(text.Trim());
        long frameSequence = sequence ?? Interlocked.Increment(ref nextSequence);
        await QueueFrameAsync(new RealtimeAudioFrame(
            frameSequence, AudioFormat.SyntheticText, audio, DateTimeOffset.UtcNow,
            SpeechStarted: true, EndOfUtterance: true), cancellationToken);
    }

    public ValueTask QueueFrameAsync(RealtimeAudioFrame frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return inbound.Writer.WriteAsync(frame, cancellationToken);
    }

    public void CompleteInput(Exception? error = null) => inbound.Writer.TryComplete(error);

    public async IAsyncEnumerable<RealtimeAudioFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (RealtimeAudioFrame frame in inbound.Reader.ReadAllAsync(cancellationToken))
            yield return frame;
    }

    public async ValueTask WaitForMediaReadyAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref mediaReadyWaitCount);
        Task ready;
        lock (synchronization) ready = mediaReady.Task;
        await ready.WaitAsync(cancellationToken);
    }

    public void HoldMediaReady()
    {
        lock (synchronization)
            mediaReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleaseMediaReady()
    {
        lock (synchronization) mediaReady.TrySetResult();
    }

    public ValueTask<RealtimeAudioSendResult> SendAsync(SynthesizedAudioChunk chunk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (synchronization)
        {
            if (outbound.Count == MaximumCapturedOutputChunks) outbound.RemoveAt(0);
            outbound.Add(chunk);
        }
        if (!chunk.IsFinal) return ValueTask.FromResult(RealtimeAudioSendResult.Pending);
        string responseId = $"fake-response-{Interlocked.Increment(ref responseSequence)}";
        lock (synchronization)
        {
            var completion = new TaskCompletionSource<RealtimePlaybackCompletion>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pendingPlayback[responseId] = completion;
            if (AutoAcknowledgePlayback)
                completion.TrySetResult(new(responseId, true, false, 0, 0));
        }
        return ValueTask.FromResult(new RealtimeAudioSendResult(
            responseId, chunk.Audio.Length, chunk.Audio.Length,
            chunk.Audio.Length, chunk.Audio.Length, 1, true, 0));
    }

    public async ValueTask<RealtimePlaybackCompletion> WaitForPlaybackCompletionAsync(
        string responseId,
        CancellationToken cancellationToken)
    {
        Task<RealtimePlaybackCompletion> completion;
        lock (synchronization)
            completion = pendingPlayback[responseId].Task;
        try { return await completion.WaitAsync(cancellationToken); }
        finally
        {
            lock (synchronization) pendingPlayback.Remove(responseId);
        }
    }

    public ValueTask ClearPlaybackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearPlaybackCount++;
        lock (synchronization)
        {
            foreach ((string responseId, TaskCompletionSource<RealtimePlaybackCompletion> completion) in pendingPlayback)
                completion.TrySetResult(new(responseId, false, true, 0, 0));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(string reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompletionReason = reason;
        inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
