using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.Telephony.Twilio;

public sealed class TwilioRealtimeAudioTransport : IRealtimeAudioTransport
{
    private readonly WebSocket socket;
    private readonly TwilioRealtimeAudioOptions options;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private MemoryStream? pendingPcm;
    private AudioFormat? pendingFormat;
    private long nextChunkSequence = 1;
    private long markSequence;
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

    public async ValueTask SendAsync(
        SynthesizedAudioChunk chunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ThrowIfUnavailable();
        ValidatePcmChunk(chunk);

        await writeLock.WaitAsync(cancellationToken);
        byte[] pcm = [];
        byte[] encoded = [];
        try
        {
            ThrowIfUnavailable();
            AppendChunkLocked(chunk);
            if (!chunk.IsFinal) return;

            byte[] responsePcm = TakePendingPcmLocked();
            try
            {
                pcm = chunk.Format.SampleRateHz == TwilioRealtimeAudioProtocol.TelephonySampleRate
                    ? responsePcm.ToArray()
                    : TwilioMuLawCodec.ResamplePcm16Mono(
                        responsePcm,
                        chunk.Format.SampleRateHz,
                        TwilioRealtimeAudioProtocol.TelephonySampleRate);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responsePcm);
            }
            encoded = TwilioMuLawCodec.EncodePcm16(pcm);

            for (int offset = 0; offset < encoded.Length; offset += options.MaxOutboundMediaBytes)
            {
                int length = Math.Min(options.MaxOutboundMediaBytes, encoded.Length - offset);
                string payload = Convert.ToBase64String(encoded, offset, length);
                await SendWireMessageLockedAsync(new
                {
                    @event = "media",
                    streamSid = ProviderMediaStreamId,
                    media = new { payload },
                }, cancellationToken);
            }

            if (chunk.IsFinal)
            {
                string name = $"response-{Interlocked.Increment(ref markSequence)}";
                await SendWireMessageLockedAsync(new
                {
                    @event = "mark",
                    streamSid = ProviderMediaStreamId,
                    mark = new { name },
                }, cancellationToken);
            }
        }
        finally
        {
            writeLock.Release();
            CryptographicOperations.ZeroMemory(pcm);
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public async ValueTask ClearPlaybackAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            ResetPendingPcmLocked();
            await SendWireMessageLockedAsync(new
            {
                @event = "clear",
                streamSid = ProviderMediaStreamId,
            }, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async ValueTask CompleteAsync(string reason, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
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
        finally
        {
            ResetPendingPcmLocked();
            writeLock.Release();
        }
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
        long sequence = TwilioRealtimeAudioProtocol.RequirePositiveInt64(root, "sequenceNumber");
        TwilioRealtimeAudioProtocol.RequireMatchingStreamSid(root, ProviderMediaStreamId);
        JsonElement media = TwilioRealtimeAudioProtocol.RequireObject(root, "media");
        string track = TwilioRealtimeAudioProtocol.RequireString(media, "track", 20);
        _ = TwilioRealtimeAudioProtocol.RequirePositiveInt64(media, "chunk");
        _ = TwilioRealtimeAudioProtocol.RequireInt32(media, "timestamp", 0, int.MaxValue);
        if (track == "outbound") return ReceiveEvent.None;
        if (!string.Equals(track, "inbound", StringComparison.Ordinal))
            throw TwilioRealtimeAudioProtocol.ProtocolError(
                "provider_media_track_invalid", "Provider media track was invalid.");

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
                sequence,
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
        _ = TwilioRealtimeAudioProtocol.RequireString(mark, "name", 128);
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

    private async ValueTask SendWireMessageLockedAsync<T>(T message, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message);
        if (json.Length > options.MaxJsonMessageBytes)
            throw new TwilioRealtimeAudioException(
                "provider_media_message_too_large", "Provider media message exceeded its size limit.");
        try
        {
            await socket.SendAsync(json.AsMemory(), WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (WebSocketException exception)
        {
            throw new TwilioRealtimeAudioException(
                "provider_media_send_failed", "Provider media connection failed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private void ValidatePcmChunk(SynthesizedAudioChunk chunk)
    {
        AudioFormat format = chunk.Format.Validate();
        if (!string.Equals(format.Encoding, "audio/pcm", StringComparison.OrdinalIgnoreCase)
            || format.BitsPerSample != 16
            || format.Channels != 1
            || (chunk.Audio.Length & 1) != 0)
            throw new TwilioRealtimeAudioException(
                "provider_media_pcm_unsupported", "Synthesized audio format was unsupported.");
        if (chunk.Audio.Length > options.MaxPcmChunkBytes)
            throw new TwilioRealtimeAudioException(
                "provider_media_pcm_too_large", "PCM audio exceeded its size limit.");
    }

    private void AppendChunkLocked(SynthesizedAudioChunk chunk)
    {
        if (chunk.Sequence != nextChunkSequence)
        {
            ResetPendingPcmLocked();
            throw new TwilioRealtimeAudioException(
                "provider_media_sequence_invalid", "Synthesized audio chunks were out of sequence.");
        }

        if (pendingFormat is null)
        {
            pendingFormat = chunk.Format;
            pendingPcm = new MemoryStream(Math.Min(options.MaxPcmResponseBytes, 16 * 1024));
        }
        else if (pendingFormat != chunk.Format)
        {
            ResetPendingPcmLocked();
            throw new TwilioRealtimeAudioException(
                "provider_media_pcm_unsupported", "Synthesized audio format changed within a response.");
        }

        if (pendingPcm!.Length + chunk.Audio.Length > options.MaxPcmResponseBytes)
        {
            ResetPendingPcmLocked();
            throw new TwilioRealtimeAudioException(
                "provider_media_pcm_too_large", "PCM audio response exceeded its size limit.");
        }

        pendingPcm.Write(chunk.Audio.Span);
        nextChunkSequence++;
    }

    private byte[] TakePendingPcmLocked()
    {
        byte[] response = pendingPcm?.ToArray() ?? [];
        ResetPendingPcmLocked();
        return response;
    }

    private void ResetPendingPcmLocked()
    {
        if (pendingPcm is not null)
        {
            if (pendingPcm.TryGetBuffer(out ArraySegment<byte> buffer))
                CryptographicOperations.ZeroMemory(buffer.AsSpan());
            pendingPcm.Dispose();
        }

        pendingPcm = null;
        pendingFormat = null;
        nextChunkSequence = 1;
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
}
