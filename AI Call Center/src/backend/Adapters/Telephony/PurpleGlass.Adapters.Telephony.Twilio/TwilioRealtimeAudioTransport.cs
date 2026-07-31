using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.Telephony.Twilio;

public sealed class TwilioRealtimeAudioTransport : IRealtimeAudioTransport
{
    private readonly WebSocket socket;
    private readonly TwilioRealtimeAudioOptions options;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly object playbackSynchronization = new();
    private readonly Dictionary<string, PendingPlayback> pendingMarks = new(StringComparer.Ordinal);
    private OutboundResponse? outboundResponse;
    private long markSequence;
    private long lastInboundMediaChunk;
    private int lastInboundTimestamp = -1;
    private int receiveStarted;
    private int completed;
    private int disposed;

    internal TwilioRealtimeAudioTransport(
        WebSocket socket,
        TwilioMediaStartMetadata startMetadata,
        TwilioRealtimeAudioOptions options,
        TimeProvider timeProvider)
    {
        this.socket = socket;
        this.options = options;
        this.timeProvider = timeProvider;
        StartMetadata = startMetadata;
    }

    public TwilioMediaStartMetadata StartMetadata { get; }

    public string Provider => TwilioRealtimeAudioProtocol.Provider;

    public string ProviderCallId => StartMetadata.CallSid;

    public string ProviderMediaStreamId => StartMetadata.StreamSid;

    public AudioFormat InputFormat => StartMetadata.InputFormat;

    public ValueTask WaitForMediaReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<RealtimeAudioFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref receiveStarted, 1) != 0)
            throw new InvalidOperationException("A Twilio media transport can only have one receiver.");

        while (!cancellationToken.IsCancellationRequested)
        {
            ReceiveEvent received = await ReadEventAsync(cancellationToken);
            if (received.Stopped) yield break;
            if (received.Frame is not null) yield return received.Frame;
        }
    }

    public async ValueTask<RealtimeAudioSendResult> SendAsync(
        SynthesizedAudioChunk chunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ThrowIfUnavailable();
        ValidatePcmChunk(chunk);

        await sendGate.WaitAsync(cancellationToken);
        byte[] convertedPcm = [];
        byte[] encoded = [];
        try
        {
            ThrowIfUnavailable();
            OutboundResponse response = GetOrCreateResponse(chunk);
            response.AppendSource(chunk, options.MaxPcmResponseBytes);
            convertedPcm = response.Resampler.Convert(chunk.Audio.Span, chunk.IsFinal);
            encoded = TwilioMuLawCodec.EncodePcm16(convertedPcm);
            response.AppendMuLaw(encoded);

            int pacedMediaBytes = options.OutboundPacketBytes;
            if (options.EnableOutboundPacing)
            {
                long now = timeProvider.GetTimestamp();
                response.ObserveProducerState(
                    now, chunk.IsFinal, response.BufferedMuLawBytes > 0, timeProvider);
                int requiredBufferBytes = response.RequiredBufferBytes(options, chunk.IsFinal);
                if (response.IsBuffering && response.BufferedMuLawBytes < requiredBufferBytes)
                    return response.Result(markSent: false, options);
                if (response.IsBuffering && response.BufferedMuLawBytes > 0)
                    response.BeginPacingSegment(now, options);
            }
            while (response.BufferedMuLawBytes >= pacedMediaBytes
                || (chunk.IsFinal && response.BufferedMuLawBytes > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long sendStarted = timeProvider.GetTimestamp();
                if (options.EnableOutboundPacing)
                {
                    TimeSpan remaining = response.DelayUntilNextPacket(sendStarted, timeProvider);
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, timeProvider, cancellationToken);
                    sendStarted = timeProvider.GetTimestamp();
                    response.ObserveSchedulerWake(sendStarted, options, timeProvider);
                    if (response.IsBuffering)
                    {
                        int requiredBufferBytes = response.RequiredBufferBytes(options, chunk.IsFinal);
                        if (response.BufferedMuLawBytes < requiredBufferBytes)
                            return response.Result(markSent: false, options);
                        response.BeginPacingSegment(sendStarted, options);
                    }
                }
                int length = Math.Min(pacedMediaBytes, response.BufferedMuLawBytes);
                byte[] packet = response.TakeMuLaw(length);
                string payload = Convert.ToBase64String(packet);
                CryptographicOperations.ZeroMemory(packet);
                await SendWireMessageAsync(new
                {
                    @event = "media",
                    streamSid = ProviderMediaStreamId,
                    media = new { payload },
                }, cancellationToken);
                response.RecordMediaSent(
                    length, sendStarted, timeProvider.GetTimestamp(), options, timeProvider);
            }

            if (!chunk.IsFinal)
                return response.Result(markSent: false, options);

            if (response.MediaMessageCount == 0 || response.TotalMuLawBytes == 0)
                throw new TwilioRealtimeAudioException(
                    "provider_media_pcm_invalid", "PCM audio response was empty.");

            long markSent = timeProvider.GetTimestamp();
            var pending = new PendingPlayback(response.FirstMediaSentTimestamp, markSent);
            lock (playbackSynchronization) pendingMarks.Add(response.ResponseId, pending);
            try
            {
                await SendWireMessageAsync(new
                {
                    @event = "mark",
                    streamSid = ProviderMediaStreamId,
                    mark = new { name = response.ResponseId },
                }, cancellationToken);
            }
            catch
            {
                lock (playbackSynchronization) pendingMarks.Remove(response.ResponseId);
                throw;
            }
            Activity.Current?.SetTag("voice.source_pcm_bytes", response.SourcePcmBytes);
            Activity.Current?.SetTag("voice.source_samples", response.Resampler.SourceSampleCount);
            Activity.Current?.SetTag("voice.resampled_samples", response.Resampler.OutputSampleCount);
            Activity.Current?.SetTag("voice.mulaw_bytes", response.TotalMuLawBytes);
            Activity.Current?.SetTag("voice.media_message_count", response.MediaMessageCount);
            Activity.Current?.SetTag("voice.mark_sent", true);
            Activity.Current?.SetTag("voice.playback_underflow_count", response.UnderflowCount);
            Activity.Current?.SetTag("voice.playback_max_lateness_ms", response.MaximumPacingLatenessMs);
            Activity.Current?.SetTag("voice.playback_startup_buffered_ms", response.StartupBufferedAudioDurationMs);
            Activity.Current?.SetTag("voice.playback_scheduler_late_count", response.SchedulerLateCount);
            Activity.Current?.SetTag("voice.playback_producer_starvation_count", response.ProducerStarvationCount);
            Activity.Current?.SetTag("voice.playback_remote_buffer_underflow_count", response.RemoteBufferUnderflowCount);
            Activity.Current?.SetTag("voice.playback_rebuffer_count", response.RebufferCount);
            Activity.Current?.SetTag("voice.playback_total_rebuffer_ms", response.TotalRebufferMs);
            Activity.Current?.SetTag("voice.playback_min_remote_reserve_ms", response.MinimumEstimatedRemoteReserveMs);
            Activity.Current?.SetTag("voice.playback_max_remote_reserve_ms", response.MaximumEstimatedRemoteReserveMs);
            Activity.Current?.SetTag("voice.playback_max_send_duration_ms", response.MaximumSendDurationMs);
            RealtimeAudioSendResult result = response.Result(markSent: true, options);
            outboundResponse = null;
            response.Dispose();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Activity.Current?.AddEvent(new ActivityEvent(
                "voice.media_suppressed_generation_stale",
                tags: new ActivityTagsCollection
                {
                    ["voice.media_messages_sent"] = outboundResponse?.MediaMessageCount ?? 0,
                    ["voice.safe_reason"] = "response_canceled",
                }));
            ResetOutboundResponseLocked();
            throw;
        }
        catch
        {
            ResetOutboundResponseLocked();
            throw;
        }
        finally
        {
            sendGate.Release();
            CryptographicOperations.ZeroMemory(convertedPcm);
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public async ValueTask<RealtimePlaybackCompletion> WaitForPlaybackCompletionAsync(
        string responseId,
        CancellationToken cancellationToken)
    {
        PendingPlayback pending;
        lock (playbackSynchronization)
        {
            if (!pendingMarks.TryGetValue(responseId, out pending!))
                throw new TwilioRealtimeAudioException(
                    "provider_playback_unknown", "Provider playback state was unavailable.");
        }
        try { return await pending.Completion.Task.WaitAsync(cancellationToken); }
        finally
        {
            lock (playbackSynchronization)
            {
                if (pendingMarks.TryGetValue(responseId, out PendingPlayback? current)
                    && ReferenceEquals(current, pending))
                    pendingMarks.Remove(responseId);
            }
        }
    }

    public async ValueTask ClearPlaybackAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            ResetOutboundResponseLocked();
            KeyValuePair<string, PendingPlayback>[] cleared;
            lock (playbackSynchronization)
            {
                cleared = pendingMarks.ToArray();
                foreach (KeyValuePair<string, PendingPlayback> entry in cleared)
                    entry.Value.MarkCleared();
            }
            await SendWireMessageAsync(new
            {
                @event = "clear",
                streamSid = ProviderMediaStreamId,
            }, cancellationToken);
            long now = timeProvider.GetTimestamp();
            foreach ((string responseId, PendingPlayback pending) in cleared)
                pending.Completion.TrySetResult(pending.Result(responseId, false, true, timeProvider, now));
            Activity.Current?.AddEvent(new ActivityEvent("voice.twilio_clear_sent"));
        }
        finally { sendGate.Release(); }
    }

    public async ValueTask CompleteAsync(string reason, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            ResetOutboundResponseLocked();
            await writeLock.WaitAsync(cancellationToken);
            try
            {
                string safeReason = SafeCloseReason(reason);
                if (socket.State == WebSocketState.Open)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, safeReason, cancellationToken);
                else if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, safeReason, cancellationToken);
            }
            catch (WebSocketException)
            {
            }
            finally { writeLock.Release(); }
        }
        finally { sendGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (Volatile.Read(ref completed) == 0)
        {
            using var closeSource = new CancellationTokenSource(options.CloseTimeout);
            try { await CompleteAsync("voice_session_ended", closeSource.Token); }
            catch (OperationCanceledException) { }
        }
        socket.Dispose();
        sendGate.Dispose();
        writeLock.Dispose();
    }

    private async ValueTask<ReceiveEvent> ReadEventAsync(CancellationToken cancellationToken)
    {
        using TwilioJsonMessage? message = await TwilioRealtimeAudioProtocol.ReceiveMessageAsync(
            socket,
            options.MaxJsonMessageBytes,
            cancellationToken);
        if (message is null) return ReceiveEvent.Stop;

        JsonElement root = message.Document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw TwilioRealtimeAudioProtocol.ProtocolError(
                "provider_media_message_invalid", "Provider media message was invalid.");
        string eventName = TwilioRealtimeAudioProtocol.RequireString(root, "event", 20);
        return eventName switch
        {
            "media" => ParseMedia(root),
            "mark" => ParseMark(root),
            "dtmf" => ParseDtmf(root),
            "stop" => ParseStop(root),
            "connected" or "start" => throw TwilioRealtimeAudioProtocol.ProtocolError(
                "provider_media_event_order_invalid", "Provider media event order was invalid."),
            _ => throw TwilioRealtimeAudioProtocol.ProtocolError(
                "provider_media_event_unsupported", "Provider media event was unsupported."),
        };
    }

    private ReceiveEvent ParseMedia(JsonElement root)
    {
        _ = TwilioRealtimeAudioProtocol.RequirePositiveInt64(root, "sequenceNumber");
        TwilioRealtimeAudioProtocol.RequireMatchingStreamSid(root, ProviderMediaStreamId);
        JsonElement media = TwilioRealtimeAudioProtocol.RequireObject(root, "media");
        string track = TwilioRealtimeAudioProtocol.RequireString(media, "track", 20);
        long mediaChunk = TwilioRealtimeAudioProtocol.RequirePositiveInt64(media, "chunk");
        int timestamp = TwilioRealtimeAudioProtocol.RequireInt32(media, "timestamp", 0, int.MaxValue);
        if (track == "outbound") return ReceiveEvent.None;
        if (!string.Equals(track, "inbound", StringComparison.Ordinal))
            throw TwilioRealtimeAudioProtocol.ProtocolError(
                "provider_media_track_invalid", "Provider media track was invalid.");
        if (mediaChunk <= lastInboundMediaChunk) return ReceiveEvent.None;
        if (timestamp < lastInboundTimestamp)
            throw TwilioRealtimeAudioProtocol.ProtocolError(
                "provider_media_timestamp_invalid", "Provider media timestamp moved backwards.");
        lastInboundMediaChunk = mediaChunk;
        lastInboundTimestamp = timestamp;

        string payload = TwilioRealtimeAudioProtocol.RequireString(
            media,
            "payload",
            options.MaxBase64PayloadCharacters);
        int maximumDecodedLength = checked(((payload.Length + 3) / 4) * 3);
        var encoded = new byte[maximumDecodedLength];
        try
        {
            if (!Convert.TryFromBase64String(payload, encoded, out int decodedLength)
                || decodedLength == 0
                || decodedLength > options.MaxDecodedMediaBytes)
                throw TwilioRealtimeAudioProtocol.ProtocolError(
                    "provider_media_payload_invalid", "Provider media payload was invalid.");
            byte[] pcm = TwilioMuLawCodec.DecodeToPcm16(encoded.AsSpan(0, decodedLength));
            return new ReceiveEvent(new RealtimeAudioFrame(
                mediaChunk,
                InputFormat,
                pcm,
                timeProvider.GetUtcNow()), false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private ReceiveEvent ParseMark(JsonElement root)
    {
        _ = TwilioRealtimeAudioProtocol.RequirePositiveInt64(root, "sequenceNumber");
        TwilioRealtimeAudioProtocol.RequireMatchingStreamSid(root, ProviderMediaStreamId);
        JsonElement mark = TwilioRealtimeAudioProtocol.RequireObject(root, "mark");
        string name = TwilioRealtimeAudioProtocol.RequireString(mark, "name", 128);
        PendingPlayback? pending;
        lock (playbackSynchronization) pendingMarks.TryGetValue(name, out pending);
        bool acknowledged = pending is not null && !pending.IsCleared;
        if (pending is not null && !pending.IsCleared)
        {
            long now = timeProvider.GetTimestamp();
            pending.Completion.TrySetResult(pending.Result(name, true, false, timeProvider, now));
        }
        Activity.Current?.SetTag("voice.mark_acknowledged", acknowledged);
        Activity.Current?.AddEvent(new ActivityEvent(
            "voice.playback_mark_acknowledged",
            tags: new ActivityTagsCollection
            {
                ["voice.response_id"] = name,
                ["voice.mark_acknowledged"] = acknowledged,
            }));
        return ReceiveEvent.None;
    }

    private ReceiveEvent ParseDtmf(JsonElement root)
    {
        _ = TwilioRealtimeAudioProtocol.RequirePositiveInt64(root, "sequenceNumber");
        TwilioRealtimeAudioProtocol.RequireMatchingStreamSid(root, ProviderMediaStreamId);
        JsonElement dtmf = TwilioRealtimeAudioProtocol.RequireObject(root, "dtmf");
        string track = TwilioRealtimeAudioProtocol.RequireString(dtmf, "track", 20);
        string digit = TwilioRealtimeAudioProtocol.RequireString(dtmf, "digit", 1);
        if (!string.Equals(track, "inbound_track", StringComparison.Ordinal)
            || "0123456789*#ABCD".IndexOf(digit[0], StringComparison.Ordinal) < 0)
            throw TwilioRealtimeAudioProtocol.ProtocolError(
                "provider_media_dtmf_invalid", "Provider media DTMF event was invalid.");
        return ReceiveEvent.None;
    }

    private ReceiveEvent ParseStop(JsonElement root)
    {
        _ = TwilioRealtimeAudioProtocol.RequirePositiveInt64(root, "sequenceNumber");
        TwilioRealtimeAudioProtocol.RequireMatchingStreamSid(root, ProviderMediaStreamId);
        JsonElement stop = TwilioRealtimeAudioProtocol.RequireObject(root, "stop");
        string accountSid = TwilioRealtimeAudioProtocol.RequireString(
            stop, "accountSid", TwilioRealtimeAudioProtocol.SidLength);
        string callSid = TwilioRealtimeAudioProtocol.RequireString(
            stop, "callSid", TwilioRealtimeAudioProtocol.SidLength);
        if (!string.Equals(accountSid, StartMetadata.AccountSid, StringComparison.Ordinal)
            || !string.Equals(callSid, ProviderCallId, StringComparison.Ordinal))
            throw TwilioRealtimeAudioProtocol.ProtocolError(
                "provider_media_stop_mismatch", "Provider media stop event did not match the active session.");
        return ReceiveEvent.Stop;
    }

    private async ValueTask SendWireMessageAsync<T>(T message, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(message);
            if (json.Length > options.MaxJsonMessageBytes)
                throw new TwilioRealtimeAudioException(
                    "provider_media_message_too_large", "Provider media message exceeded its size limit.");
            try { await socket.SendAsync(json.AsMemory(), WebSocketMessageType.Text, true, cancellationToken); }
            finally { CryptographicOperations.ZeroMemory(json); }
        }
        catch (WebSocketException exception)
        {
            throw new TwilioRealtimeAudioException(
                "provider_media_send_failed", "Provider media connection failed.", exception);
        }
        finally { writeLock.Release(); }
    }

    private void ValidatePcmChunk(SynthesizedAudioChunk chunk)
    {
        AudioFormat format = chunk.Format.Validate();
        if (!string.Equals(format.Encoding, "audio/pcm", StringComparison.OrdinalIgnoreCase)
            || format.BitsPerSample != 16
            || format.Channels != 1)
            throw new TwilioRealtimeAudioException(
                "provider_media_pcm_unsupported", "Synthesized audio format was unsupported.");
        if (chunk.Audio.Length > options.MaxPcmChunkBytes)
            throw new TwilioRealtimeAudioException(
                "provider_media_pcm_too_large", "PCM audio exceeded its size limit.");
    }

    private OutboundResponse GetOrCreateResponse(SynthesizedAudioChunk chunk)
    {
        if (outboundResponse is null)
        {
            if (chunk.Sequence != 1)
                throw new TwilioRealtimeAudioException(
                    "provider_media_sequence_invalid", "Synthesized audio chunks were out of sequence.");
            outboundResponse = new OutboundResponse(
                $"response-{Interlocked.Increment(ref markSequence)}",
                chunk.Format);
        }
        else if (outboundResponse.Format != chunk.Format)
        {
            ResetOutboundResponseLocked();
            throw new TwilioRealtimeAudioException(
                "provider_media_pcm_unsupported", "Synthesized audio format changed within a response.");
        }
        return outboundResponse;
    }

    private void ResetOutboundResponseLocked()
    {
        if (outboundResponse is null) return;
        outboundResponse.Dispose();
        outboundResponse = null;
    }

    private sealed class OutboundResponse : IDisposable
    {
        private readonly MemoryStream pendingMuLaw = new();
        private int pendingReadOffset;
        private long nextSequence = 1;
        private long bufferedUntilTimestamp;
        private long nextPacketTargetTimestamp;
        private int immediatePacketsRemaining;
        private bool playbackStarted;
        private bool isBuffering = true;
        private bool rebufferPending;
        private long rebufferStartedTimestamp;
        private bool currentPacketIsPaced;
        private double currentSchedulerLatenessMs;
        private double pacingLatenessTotalMs;
        private int pacedPacketCount;
        private bool minimumRemoteReserveObserved;
        private bool initialReserveComplete;

        public OutboundResponse(string responseId, AudioFormat format)
        {
            ResponseId = responseId;
            Format = format;
            Resampler = new StreamingPcm16Resampler(
                format.SampleRateHz,
                TwilioRealtimeAudioProtocol.TelephonySampleRate);
        }

        public string ResponseId { get; }
        public AudioFormat Format { get; }
        public StreamingPcm16Resampler Resampler { get; }
        public long FirstMediaSentTimestamp { get; private set; }
        public int SourcePcmBytes { get; private set; }
        public int TotalMuLawBytes { get; private set; }
        public int MediaMessageCount { get; private set; }
        public int BufferedMuLawBytes => checked((int)pendingMuLaw.Length - pendingReadOffset);
        public bool IsBuffering => isBuffering;
        public int UnderflowCount { get; private set; }
        public int SchedulerLateCount { get; private set; }
        public int ProducerStarvationCount { get; private set; }
        public int RemoteBufferUnderflowCount { get; private set; }
        public int RebufferCount { get; private set; }
        public double TotalRebufferMs { get; private set; }
        public double StartupBufferedAudioDurationMs { get; private set; }
        public double MaximumBufferedAudioDurationMs { get; private set; }
        public double MinimumEstimatedRemoteReserveMs { get; private set; }
        public double MaximumEstimatedRemoteReserveMs { get; private set; }
        public double MaximumPacingLatenessMs { get; private set; }
        public double MaximumSendDurationMs { get; private set; }

        public void AppendSource(SynthesizedAudioChunk chunk, int maximumResponseBytes)
        {
            if (chunk.Sequence != nextSequence)
                throw new TwilioRealtimeAudioException(
                    "provider_media_sequence_invalid", "Synthesized audio chunks were out of sequence.");
            nextSequence++;
            int nextLength = checked(SourcePcmBytes + chunk.Audio.Length);
            if (nextLength > maximumResponseBytes)
                throw new TwilioRealtimeAudioException(
                    "provider_media_pcm_too_large", "PCM audio response exceeded its size limit.");
            SourcePcmBytes = nextLength;
        }

        public void AppendMuLaw(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return;
            pendingMuLaw.Position = pendingMuLaw.Length;
            pendingMuLaw.Write(bytes);
            TotalMuLawBytes = checked(TotalMuLawBytes + bytes.Length);
        }

        public byte[] TakeMuLaw(int length)
        {
            if (length <= 0 || length > BufferedMuLawBytes) throw new ArgumentOutOfRangeException(nameof(length));
            var packet = new byte[length];
            pendingMuLaw.Position = pendingReadOffset;
            int read = pendingMuLaw.Read(packet, 0, length);
            if (read != length) throw new InvalidOperationException("Buffered media was incomplete.");
            pendingReadOffset += length;
            if (pendingReadOffset == pendingMuLaw.Length)
            {
                pendingMuLaw.SetLength(0);
                pendingReadOffset = 0;
            }
            return packet;
        }

        public int RequiredBufferBytes(TwilioRealtimeAudioOptions options, bool providerCompleted) =>
            providerCompleted
                ? Math.Min(BufferedMuLawBytes, options.OutboundStartupBufferBytes)
                : options.OutboundStartupBufferBytes;

        public void ObserveProducerState(
            long timestamp,
            bool providerCompleted,
            bool hasPendingAudio,
            TimeProvider provider)
        {
            if (!playbackStarted || isBuffering || timestamp <= bufferedUntilTimestamp) return;
            ObserveRemoteReserve(timestamp, provider);
            if (providerCompleted && !hasPendingAudio) return;
            StartUnderflow(bufferedUntilTimestamp, producerStarved: true);
        }

        public void BeginPacingSegment(
            long timestamp,
            TwilioRealtimeAudioOptions options)
        {
            bool startingPlayback = !playbackStarted;
            isBuffering = false;
            immediatePacketsRemaining = Math.Max(1,
                (int)(options.OutboundStartupBufferDuration.Ticks / options.OutboundPacketDuration.Ticks));
            if (startingPlayback)
            {
                StartupBufferedAudioDurationMs = Math.Max(
                    StartupBufferedAudioDurationMs,
                    Math.Min(BufferedMuLawBytes, options.OutboundStartupBufferBytes)
                        * 1000d / TwilioRealtimeAudioProtocol.TelephonySampleRate);
                bufferedUntilTimestamp = timestamp;
            }
            else
            {
                rebufferPending = true;
            }
            nextPacketTargetTimestamp = timestamp;
        }

        public TimeSpan DelayUntilNextPacket(long timestamp, TimeProvider provider)
        {
            if (immediatePacketsRemaining > 0) return TimeSpan.Zero;
            TimeSpan remaining = provider.GetElapsedTime(timestamp, nextPacketTargetTimestamp);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        public void ObserveSchedulerWake(
            long timestamp,
            TwilioRealtimeAudioOptions options,
            TimeProvider provider)
        {
            currentPacketIsPaced = immediatePacketsRemaining == 0;
            currentSchedulerLatenessMs = 0;
            if (!currentPacketIsPaced) return;

            currentSchedulerLatenessMs = Math.Max(0,
                provider.GetElapsedTime(nextPacketTargetTimestamp, timestamp).TotalMilliseconds);
            pacingLatenessTotalMs += currentSchedulerLatenessMs;
            pacedPacketCount++;
            if (currentSchedulerLatenessMs > 0)
            {
                SchedulerLateCount++;
                MaximumPacingLatenessMs = Math.Max(
                    MaximumPacingLatenessMs, currentSchedulerLatenessMs);
            }

            if (playbackStarted && timestamp > bufferedUntilTimestamp)
            {
                ObserveRemoteReserve(timestamp, provider);
                StartUnderflow(bufferedUntilTimestamp, producerStarved: false);
            }
        }

        public void RecordMediaSent(
            int length,
            long sendStartedTimestamp,
            long sendCompletedTimestamp,
            TwilioRealtimeAudioOptions options,
            TimeProvider provider)
        {
            if (MediaMessageCount == 0)
            {
                FirstMediaSentTimestamp = sendCompletedTimestamp;
            }
            if (options.EnableOutboundPacing)
            {
                double sendDurationMs = Math.Max(0,
                    provider.GetElapsedTime(sendStartedTimestamp, sendCompletedTimestamp).TotalMilliseconds);
                MaximumSendDurationMs = Math.Max(MaximumSendDurationMs, sendDurationMs);
                bool wasImmediate = immediatePacketsRemaining > 0;
                long previousBufferedUntil = bufferedUntilTimestamp;

                if (playbackStarted
                    && !rebufferPending
                    && sendCompletedTimestamp > previousBufferedUntil)
                {
                    ObserveRemoteReserve(sendCompletedTimestamp, provider);
                    StartUnderflow(previousBufferedUntil, producerStarved: false);
                    isBuffering = false;
                    rebufferPending = true;
                }

                if (immediatePacketsRemaining > 0)
                {
                    immediatePacketsRemaining--;
                }
                else if (currentPacketIsPaced)
                {
                    nextPacketTargetTimestamp = currentSchedulerLatenessMs
                        > options.MaximumPacingLateness.TotalMilliseconds
                        ? Add(sendStartedTimestamp, options.OutboundPacketDuration, provider)
                        : Add(nextPacketTargetTimestamp, options.OutboundPacketDuration, provider);
                }

                if (sendCompletedTimestamp > bufferedUntilTimestamp)
                    bufferedUntilTimestamp = sendCompletedTimestamp;
                bufferedUntilTimestamp = Add(
                    bufferedUntilTimestamp,
                    TimeSpan.FromSeconds(length / (double)TwilioRealtimeAudioProtocol.TelephonySampleRate),
                    provider);

                if (rebufferPending)
                {
                    RebufferCount++;
                    TotalRebufferMs += Math.Max(0,
                        provider.GetElapsedTime(rebufferStartedTimestamp, sendCompletedTimestamp)
                            .TotalMilliseconds);
                    rebufferPending = false;
                }

                if (!initialReserveComplete && immediatePacketsRemaining == 0)
                    initialReserveComplete = true;
                ObserveRemoteReserve(
                    sendCompletedTimestamp, provider, includeMinimum: initialReserveComplete);
                int reserveDeficitPackets = PacketsRequiredToRestoreReserve(
                    sendCompletedTimestamp, options, provider);
                immediatePacketsRemaining = Math.Max(
                    immediatePacketsRemaining, reserveDeficitPackets);
                if (immediatePacketsRemaining == 0 && wasImmediate)
                {
                    nextPacketTargetTimestamp = Add(
                        sendCompletedTimestamp, options.OutboundPacketDuration, provider);
                }
                if (immediatePacketsRemaining == 0)
                {
                    double reserveMs = EstimatedRemoteReserveMs(sendCompletedTimestamp, provider);
                    double minimumDelayMs = Math.Max(0,
                        reserveMs + options.OutboundPacketDuration.TotalMilliseconds
                            - options.OutboundStartupBufferDuration.TotalMilliseconds);
                    long reserveBoundDeadline = Add(
                        sendCompletedTimestamp, TimeSpan.FromMilliseconds(minimumDelayMs), provider);
                    nextPacketTargetTimestamp = Math.Max(
                        nextPacketTargetTimestamp, reserveBoundDeadline);
                }
                playbackStarted = true;
            }
            MediaMessageCount++;
        }

        private void StartUnderflow(long exhaustedTimestamp, bool producerStarved)
        {
            if (isBuffering) return;
            isBuffering = true;
            rebufferStartedTimestamp = exhaustedTimestamp;
            UnderflowCount++;
            RemoteBufferUnderflowCount++;
            if (producerStarved) ProducerStarvationCount++;
            MinimumEstimatedRemoteReserveMs = 0;
            minimumRemoteReserveObserved = true;
        }

        private int PacketsRequiredToRestoreReserve(
            long timestamp,
            TwilioRealtimeAudioOptions options,
            TimeProvider provider)
        {
            double reserveMs = EstimatedRemoteReserveMs(timestamp, provider);
            double deficitMs = Math.Max(
                0, options.OutboundStartupBufferDuration.TotalMilliseconds - reserveMs);
            return (int)Math.Floor(
                (deficitMs + 0.000_001) / options.OutboundPacketDuration.TotalMilliseconds);
        }

        private void ObserveRemoteReserve(
            long timestamp,
            TimeProvider provider,
            bool includeMinimum = true)
        {
            double reserveMs = EstimatedRemoteReserveMs(timestamp, provider);
            if (includeMinimum && !minimumRemoteReserveObserved)
            {
                MinimumEstimatedRemoteReserveMs = reserveMs;
                minimumRemoteReserveObserved = true;
            }
            else if (includeMinimum)
            {
                MinimumEstimatedRemoteReserveMs = Math.Min(
                    MinimumEstimatedRemoteReserveMs, reserveMs);
            }
            MaximumEstimatedRemoteReserveMs = Math.Max(
                MaximumEstimatedRemoteReserveMs, reserveMs);
            MaximumBufferedAudioDurationMs = MaximumEstimatedRemoteReserveMs;
        }

        private double EstimatedRemoteReserveMs(long timestamp, TimeProvider provider) =>
            timestamp >= bufferedUntilTimestamp
                ? 0
                : provider.GetElapsedTime(timestamp, bufferedUntilTimestamp).TotalMilliseconds;

        private static long Add(long timestamp, TimeSpan duration, TimeProvider provider) =>
            checked(timestamp + (long)Math.Round(
                duration.TotalSeconds * provider.TimestampFrequency,
                MidpointRounding.AwayFromZero));

        public RealtimeAudioSendResult Result(bool markSent, TwilioRealtimeAudioOptions options) => new(
            ResponseId,
            SourcePcmBytes,
            Resampler.SourceSampleCount,
            Resampler.OutputSampleCount,
            TotalMuLawBytes,
            MediaMessageCount,
            markSent,
            options.EnableOutboundPacing ? MaximumBufferedAudioDurationMs : 0,
            options.EnableOutboundPacing ? options.OutboundPacketDuration.TotalMilliseconds : 0,
            options.EnableOutboundPacing ? StartupBufferedAudioDurationMs : 0,
            options.EnableOutboundPacing ? UnderflowCount : 0,
            pacedPacketCount == 0 ? 0 : pacingLatenessTotalMs / pacedPacketCount,
            MaximumPacingLatenessMs,
            options.EnableOutboundPacing ? SchedulerLateCount : 0,
            options.EnableOutboundPacing ? ProducerStarvationCount : 0,
            options.EnableOutboundPacing ? RemoteBufferUnderflowCount : 0,
            options.EnableOutboundPacing ? RebufferCount : 0,
            options.EnableOutboundPacing ? TotalRebufferMs : 0,
            options.EnableOutboundPacing ? MinimumEstimatedRemoteReserveMs : 0,
            options.EnableOutboundPacing ? MaximumEstimatedRemoteReserveMs : 0,
            options.EnableOutboundPacing ? MaximumSendDurationMs : 0);

        public void Dispose()
        {
            if (pendingMuLaw.TryGetBuffer(out ArraySegment<byte> buffer))
                CryptographicOperations.ZeroMemory(buffer.AsSpan());
            pendingMuLaw.Dispose();
        }
    }

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref completed) != 0
            || socket.State is not WebSocketState.Open)
            throw new TwilioRealtimeAudioException(
                "provider_media_closed", "Provider media connection was closed.");
    }

    private static string SafeCloseReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "voice_session_ended";
        string safe = new(reason.Trim().ToLowerInvariant()
            .Where(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')
            .Take(60)
            .ToArray());
        return safe.Length == 0 ? "voice_session_ended" : safe;
    }

    private readonly record struct ReceiveEvent(RealtimeAudioFrame? Frame, bool Stopped)
    {
        public static ReceiveEvent None => new(null, false);
        public static ReceiveEvent Stop => new(null, true);
    }

    private sealed class PendingPlayback(long firstMediaTimestamp, long markSentTimestamp)
    {
        private int cleared;

        public TaskCompletionSource<RealtimePlaybackCompletion> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsCleared => Volatile.Read(ref cleared) != 0;

        public void MarkCleared() => Interlocked.Exchange(ref cleared, 1);

        public RealtimePlaybackCompletion Result(
            string responseId,
            bool acknowledged,
            bool cleared,
            TimeProvider provider,
            long completedTimestamp) => new(
                responseId,
                acknowledged,
                cleared,
                provider.GetElapsedTime(firstMediaTimestamp, completedTimestamp).TotalMilliseconds,
                provider.GetElapsedTime(markSentTimestamp, completedTimestamp).TotalMilliseconds);
    }
}
