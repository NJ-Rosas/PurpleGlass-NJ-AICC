using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PurpleGlass.Adapters.Telephony.Twilio;
using PurpleGlass.Adapters.Speech.Mock;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.UnitTests;

public sealed class TwilioRealtimeAudioTests
{
    private const string AccountSid = "AC11111111111111111111111111111111";
    private const string OtherAccountSid = "AC99999999999999999999999999999999";
    private const string CallSid = "CA22222222222222222222222222222222";
    private const string StreamSid = "MZ33333333333333333333333333333333";

    [Fact]
    public async Task SimulatorSpeechIsRejectedBeforeWritingToTwilioMedia()
    {
        var synthesizer = new MockSpeechSynthesizer(new MockSpeechOptions(), TimeProvider.System);
        SpeechSynthesisResult synthesis = await synthesizer.SynthesizeAsync(
            new SpeechSynthesisRequest(
                new RuntimeInvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    Guid.NewGuid(), Guid.NewGuid(), "test-trace"),
                "Synthetic greeting", "en-US", new VoiceConfiguration("alloy")),
            default);
        SynthesizedAudioChunk chunk = Assert.Single(synthesis.AudioChunks!);
        var socket = InitializedSocket();
        TwilioRealtimeAudioTransport transport = await InitializeAsync(socket);

        TwilioRealtimeAudioException exception = await Assert.ThrowsAsync<TwilioRealtimeAudioException>(
            async () => await transport.SendAsync(chunk, default));

        Assert.Equal("provider_media_pcm_unsupported", exception.Code);
        Assert.Empty(socket.SentTextMessages);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task MuLawPcmRoundTripPreservesAudioAndDuplicateSequenceForCoreDeduplication()
    {
        byte[] sourceMuLaw = [0xff, 0x00, 0x80];
        string payload = Convert.ToBase64String(sourceMuLaw);
        var inboundSocket = InitializedSocket();
        inboundSocket.EnqueueJson(MediaMessage(2, 1, payload));
        inboundSocket.EnqueueJson(MediaMessage(2, 1, payload));
        inboundSocket.EnqueueJson(StopMessage(3));
        TwilioRealtimeAudioTransport inbound = await InitializeAsync(inboundSocket);

        var frames = new List<RealtimeAudioFrame>();
        await foreach (RealtimeAudioFrame frame in inbound.ReceiveAsync(default)) frames.Add(frame);

        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.Equal(2, frame.Sequence));
        Assert.All(frames, frame => Assert.Equal(AudioFormat.Pcm16(), frame.Format));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(frames[0].Audio.Span));
        Assert.Equal(-32_124, BinaryPrimitives.ReadInt16LittleEndian(frames[0].Audio.Span[2..]));
        Assert.Equal(32_124, BinaryPrimitives.ReadInt16LittleEndian(frames[0].Audio.Span[4..]));

        var outboundSocket = InitializedSocket();
        TwilioRealtimeAudioTransport outbound = await InitializeAsync(outboundSocket);
        await outbound.SendAsync(new SynthesizedAudioChunk(
            1,
            AudioFormat.Pcm16(),
            frames[0].Audio,
            IsFinal: true), default);

        Assert.Equal(2, outboundSocket.SentTextMessages.Count);
        using JsonDocument media = JsonDocument.Parse(outboundSocket.SentTextMessages[0]);
        Assert.Equal("media", media.RootElement.GetProperty("event").GetString());
        Assert.Equal(StreamSid, media.RootElement.GetProperty("streamSid").GetString());
        byte[] returnedMuLaw = Convert.FromBase64String(
            media.RootElement.GetProperty("media").GetProperty("payload").GetString()!);
        Assert.Equal(sourceMuLaw, returnedMuLaw);

        using JsonDocument mark = JsonDocument.Parse(outboundSocket.SentTextMessages[1]);
        Assert.Equal("mark", mark.RootElement.GetProperty("event").GetString());
        Assert.Equal(StreamSid, mark.RootElement.GetProperty("streamSid").GetString());
        Assert.Equal("response-1", mark.RootElement.GetProperty("mark").GetProperty("name").GetString());

        await inbound.DisposeAsync();
        await outbound.DisposeAsync();
    }

    [Fact]
    public async Task OutboundPcmIsResampledToEightKilohertz()
    {
        var socket = InitializedSocket();
        TwilioRealtimeAudioTransport transport = await InitializeAsync(socket);
        var pcm16Khz = new byte[8];

        await transport.SendAsync(new SynthesizedAudioChunk(
            1,
            AudioFormat.Pcm16(16_000),
            pcm16Khz), default);

        using JsonDocument media = JsonDocument.Parse(Assert.Single(socket.SentTextMessages));
        byte[] encoded = Convert.FromBase64String(
            media.RootElement.GetProperty("media").GetProperty("payload").GetString()!);
        Assert.Equal(2, encoded.Length);
        Assert.All(encoded, value => Assert.Equal(0xff, value));
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task ClearPlaybackSendsTwilioClearMessage()
    {
        var socket = InitializedSocket();
        TwilioRealtimeAudioTransport transport = await InitializeAsync(socket);

        await transport.ClearPlaybackAsync(default);

        using JsonDocument clear = JsonDocument.Parse(Assert.Single(socket.SentTextMessages));
        Assert.Equal("clear", clear.RootElement.GetProperty("event").GetString());
        Assert.Equal(StreamSid, clear.RootElement.GetProperty("streamSid").GetString());
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task OutboundPcmBoundIsEnforcedBeforeWriting()
    {
        var socket = InitializedSocket();
        var options = new TwilioRealtimeAudioOptions { MaxPcmChunkBytes = 320 };
        TwilioRealtimeAudioTransport transport = await InitializeAsync(socket, options: options);

        TwilioRealtimeAudioException exception = await Assert.ThrowsAsync<TwilioRealtimeAudioException>(
            async () => await transport.SendAsync(new SynthesizedAudioChunk(
                1,
                AudioFormat.Pcm16(),
                new byte[322]), default));

        Assert.Equal("provider_media_pcm_too_large", exception.Code);
        Assert.Empty(socket.SentTextMessages);
        await transport.DisposeAsync();
    }

    [Theory]
    [InlineData("account", "provider_account_mismatch")]
    [InlineData("format", "provider_media_format_unsupported")]
    [InlineData("call", "provider_call_invalid")]
    [InlineData("stream", "provider_media_stream_invalid")]
    public async Task InvalidStartMetadataIsRejected(string scenario, string expectedCode)
    {
        string accountSid = scenario == "account" ? OtherAccountSid : AccountSid;
        string callSid = scenario == "call" ? "XX22222222222222222222222222222222" : CallSid;
        string streamSid = scenario == "stream" ? "XX33333333333333333333333333333333" : StreamSid;
        string encoding = scenario == "format" ? "audio/ogg" : "audio/x-mulaw";
        var socket = new InMemoryWebSocket();
        socket.EnqueueJson(ConnectedMessage());
        socket.EnqueueJson(StartMessage(accountSid, callSid, streamSid, encoding));

        TwilioRealtimeAudioException exception = await Assert.ThrowsAsync<TwilioRealtimeAudioException>(
            () => new TwilioRealtimeAudioTransportFactory().InitializeAsync(
                socket,
                AccountSid,
                new TwilioRealtimeAudioOptions(),
                default));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    private static InMemoryWebSocket InitializedSocket()
    {
        var socket = new InMemoryWebSocket();
        socket.EnqueueJson(ConnectedMessage());
        socket.EnqueueJson(StartMessage(AccountSid, CallSid, StreamSid, "audio/x-mulaw"));
        return socket;
    }

    private static Task<TwilioRealtimeAudioTransport> InitializeAsync(
        InMemoryWebSocket socket,
        string expectedAccountSid = AccountSid,
        TwilioRealtimeAudioOptions? options = null) =>
        new TwilioRealtimeAudioTransportFactory().InitializeAsync(
            socket,
            expectedAccountSid,
            options ?? new TwilioRealtimeAudioOptions(),
            default);

    private static object ConnectedMessage() => new
    {
        @event = "connected",
        protocol = "Call",
        version = "1.0.0",
    };

    private static object StartMessage(
        string accountSid,
        string callSid,
        string streamSid,
        string encoding) => new
        {
            @event = "start",
            sequenceNumber = "1",
            start = new
            {
                accountSid,
                streamSid,
                callSid,
                tracks = new[] { "inbound" },
                mediaFormat = new { encoding, sampleRate = 8_000, channels = 1 },
                customParameters = new
                {
                    tenantId = Guid.NewGuid(),
                    locationId = Guid.NewGuid(),
                },
            },
            streamSid,
        };

    private static object MediaMessage(long sequence, long chunk, string payload) => new
    {
        @event = "media",
        sequenceNumber = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        media = new
        {
            track = "inbound",
            chunk = chunk.ToString(System.Globalization.CultureInfo.InvariantCulture),
            timestamp = "0",
            payload,
        },
        streamSid = StreamSid,
    };

    private static object StopMessage(long sequence) => new
    {
        @event = "stop",
        sequenceNumber = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        stop = new { accountSid = AccountSid, callSid = CallSid },
        streamSid = StreamSid,
    };

    private sealed class InMemoryWebSocket : WebSocket
    {
        private readonly Queue<byte[]> inboundMessages = new();
        private readonly MemoryStream outboundMessage = new();
        private byte[]? currentInbound;
        private int currentInboundOffset;
        private WebSocketState state = WebSocketState.Open;
        private WebSocketCloseStatus? closeStatus;
        private string? closeStatusDescription;

        public List<string> SentTextMessages { get; } = [];

        public override WebSocketCloseStatus? CloseStatus => closeStatus;

        public override string? CloseStatusDescription => closeStatusDescription;

        public override WebSocketState State => state;

        public override string? SubProtocol => null;

        public void EnqueueJson(object value) =>
            inboundMessages.Enqueue(JsonSerializer.SerializeToUtf8Bytes(value));

        public override void Abort() => state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.closeStatus = closeStatus;
            closeStatusDescription = statusDescription;
            state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.closeStatus = closeStatus;
            closeStatusDescription = statusDescription;
            state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            outboundMessage.Dispose();
            state = WebSocketState.Closed;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            ValueWebSocketReceiveResult result = ReceiveCore(
                new Memory<byte>(buffer.Array!, buffer.Offset, buffer.Count), cancellationToken);
            return Task.FromResult(new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage));
        }

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ReceiveCore(buffer, cancellationToken));

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            CaptureSend(new ReadOnlyMemory<byte>(buffer.Array!, buffer.Offset, buffer.Count), messageType, endOfMessage, cancellationToken);
            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            CaptureSend(buffer, messageType, endOfMessage, cancellationToken);
            return ValueTask.CompletedTask;
        }

        private ValueWebSocketReceiveResult ReceiveCore(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (currentInbound is null)
            {
                if (!inboundMessages.TryDequeue(out currentInbound))
                {
                    state = WebSocketState.CloseReceived;
                    return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                }
                currentInboundOffset = 0;
            }

            int count = Math.Min(buffer.Length, currentInbound.Length - currentInboundOffset);
            currentInbound.AsMemory(currentInboundOffset, count).CopyTo(buffer);
            currentInboundOffset += count;
            bool endOfMessage = currentInboundOffset == currentInbound.Length;
            if (endOfMessage) currentInbound = null;
            return new ValueWebSocketReceiveResult(count, WebSocketMessageType.Text, endOfMessage);
        }

        private void CaptureSend(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WebSocketState.Open, state);
            Assert.Equal(WebSocketMessageType.Text, messageType);
            outboundMessage.Write(buffer.Span);
            if (!endOfMessage) return;
            SentTextMessages.Add(Encoding.UTF8.GetString(outboundMessage.GetBuffer(), 0, checked((int)outboundMessage.Length)));
            outboundMessage.SetLength(0);
        }
    }
}
