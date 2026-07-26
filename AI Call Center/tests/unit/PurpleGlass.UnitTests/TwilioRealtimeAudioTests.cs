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
    public async Task MuLawPcmRoundTripPreservesAudioAndDropsDuplicateMediaChunk()
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

        Assert.Single(frames);
        Assert.Equal(1, frames[0].Sequence);
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
            pcm16Khz,
            IsFinal: true), default);

        Assert.Equal(2, socket.SentTextMessages.Count);
        using JsonDocument media = JsonDocument.Parse(socket.SentTextMessages[0]);
        byte[] encoded = Convert.FromBase64String(
            media.RootElement.GetProperty("media").GetProperty("payload").GetString()!);
        Assert.Equal(2, encoded.Length);
        Assert.All(encoded, value => Assert.Equal(0xff, value));
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task CompleteResponseReturnsMeasuredMediaAndRejectsEmptyOutput()
    {
        var socket = InitializedSocket();
        TwilioRealtimeAudioTransport transport = await InitializeAsync(socket);
        byte[] source = SinePcm(24_000, 1_000, 0.1, 10_000);

        RealtimeAudioSendResult result = await transport.SendAsync(new SynthesizedAudioChunk(
            1, AudioFormat.Pcm16(24_000), source, IsFinal: true), default);

        Assert.Equal(source.Length, result.SourcePcmBytes);
        Assert.Equal(source.Length / sizeof(short), result.SourceSamples);
        Assert.Equal(800, result.ResampledSamples);
        Assert.Equal(800, result.MuLawBytes);
        Assert.Equal(1, result.MediaMessageCount);
        Assert.True(result.MarkSent);
        Assert.Equal(["media", "mark"], socket.SentTextMessages.Select(message =>
        {
            using JsonDocument json = JsonDocument.Parse(message);
            return json.RootElement.GetProperty("event").GetString()!;
        }).ToArray());
        await transport.DisposeAsync();

        var emptySocket = InitializedSocket();
        TwilioRealtimeAudioTransport emptyTransport = await InitializeAsync(emptySocket);
        TwilioRealtimeAudioException exception = await Assert.ThrowsAsync<TwilioRealtimeAudioException>(async () =>
            await emptyTransport.SendAsync(new SynthesizedAudioChunk(
                1, AudioFormat.Pcm16(24_000), ReadOnlyMemory<byte>.Empty, IsFinal: true), default));
        Assert.Equal("provider_media_pcm_invalid", exception.Code);
        Assert.Empty(emptySocket.SentTextMessages);
        await emptyTransport.DisposeAsync();
    }

    [Fact]
    public async Task OutboundTrackAndDuplicateInboundChunksCannotBecomeCallerFrames()
    {
        var socket = InitializedSocket();
        string silence = Convert.ToBase64String(Enumerable.Repeat((byte)0xff, 160).ToArray());
        socket.EnqueueJson(MediaMessage(2, 1, silence, track: "outbound", timestamp: 0));
        socket.EnqueueJson(MediaMessage(3, 1, silence, track: "inbound", timestamp: 0));
        socket.EnqueueJson(MediaMessage(4, 1, silence, track: "inbound", timestamp: 0));
        socket.EnqueueJson(MediaMessage(5, 2, silence, track: "inbound", timestamp: 20));
        socket.EnqueueJson(StopMessage(6));
        TwilioRealtimeAudioTransport transport = await InitializeAsync(socket);

        var frames = new List<RealtimeAudioFrame>();
        await foreach (RealtimeAudioFrame frame in transport.ReceiveAsync(default)) frames.Add(frame);

        Assert.Equal([1L, 2L], frames.Select(frame => frame.Sequence).ToArray());
        Assert.All(frames, frame => Assert.All(frame.Audio.ToArray(), value => Assert.Equal(0, value)));
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task DownsamplingAttenuatesEnergyAboveTelephoneNyquistWithoutMutingSpeechBand()
    {
        byte[] speechBand = SinePcm(24_000, 1_000, 0.25, 12_000);
        byte[] aboveNyquist = SinePcm(24_000, 6_000, 0.25, 12_000);

        byte[] speechBandMuLaw = await SendAndCollectMuLawAsync(speechBand, 24_000, speechBand.Length);
        byte[] aboveNyquistMuLaw = await SendAndCollectMuLawAsync(aboveNyquist, 24_000, aboveNyquist.Length);
        byte[] speechBandPcm = await DecodeMuLawAsync(speechBandMuLaw);
        byte[] aboveNyquistPcm = await DecodeMuLawAsync(aboveNyquistMuLaw);

        double speechBandRms = Rms(speechBandPcm, edgeSamplesToIgnore: 40);
        double aboveNyquistRms = Rms(aboveNyquistPcm, edgeSamplesToIgnore: 40);
        Assert.True(speechBandRms > 7_000, $"Speech-band RMS was unexpectedly low: {speechBandRms}.");
        Assert.True(aboveNyquistRms < speechBandRms * 0.05,
            $"Aliased RMS {aboveNyquistRms} was not sufficiently below speech-band RMS {speechBandRms}.");
    }

    [Theory]
    [InlineData(4_798)]
    [InlineData(4_800)]
    [InlineData(4_802)]
    public async Task SplitChunksProduceExactlyTheSameAudioAsOneContiguousResponse(int firstChunkLength)
    {
        byte[] source = WordLikePcm();
        byte[] contiguous = await SendAndCollectMuLawAsync(source, 24_000, source.Length);
        byte[] split = await SendAndCollectMuLawAsync(
            source, 24_000, firstChunkLength, 4_800, source.Length - firstChunkLength - 4_800);

        Assert.Equal(contiguous, split);
    }

    [Fact]
    public async Task FinalPartialChunkUsesOnlyValidSamplesAndAddsNoPadding()
    {
        byte[] source = SinePcm(24_000, 900, 0.10004, 8_000);

        byte[] encoded = await SendAndCollectMuLawAsync(source, 24_000, 4_800, source.Length - 4_800);

        int sourceSamples = source.Length / sizeof(short);
        int expectedSamples = (int)Math.Round(sourceSamples / 3d, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedSamples, encoded.Length);
    }

    [Fact]
    public async Task MuLawEncodingMatchesKnownPcmVectors()
    {
        short[] samples = [0, 1, -1, 32_124, -32_124];
        var pcm = new byte[samples.Length * sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * sizeof(short)), samples[index]);

        byte[] encoded = await SendAndCollectMuLawAsync(pcm, 8_000, pcm.Length);

        Assert.Equal(new byte[] { 0xff, 0xff, 0x7f, 0x80, 0x00 }, encoded);
    }

    [Fact]
    public async Task ClearDropsAnIncompleteResponseBeforeTheNextResponse()
    {
        var socket = InitializedSocket();
        TwilioRealtimeAudioTransport transport = await InitializeAsync(socket);
        await transport.SendAsync(new SynthesizedAudioChunk(
            1, AudioFormat.Pcm16(24_000), SinePcm(24_000, 1_000, 0.1, 10_000)), default);

        await transport.ClearPlaybackAsync(default);
        await transport.SendAsync(new SynthesizedAudioChunk(
            1, AudioFormat.Pcm16(24_000), new byte[4_800], IsFinal: true), default);

        Assert.Equal(3, socket.SentTextMessages.Count);
        using JsonDocument clear = JsonDocument.Parse(socket.SentTextMessages[0]);
        Assert.Equal("clear", clear.RootElement.GetProperty("event").GetString());
        using JsonDocument media = JsonDocument.Parse(socket.SentTextMessages[1]);
        byte[] encoded = Convert.FromBase64String(
            media.RootElement.GetProperty("media").GetProperty("payload").GetString()!);
        Assert.Equal(800, encoded.Length);
        Assert.All(encoded, value => Assert.Equal(0xff, value));
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task CancellationBetweenChunksCannotLeakIntoTheNextResponse()
    {
        var socket = InitializedSocket();
        TwilioRealtimeAudioTransport transport = await InitializeAsync(socket);
        await transport.SendAsync(new SynthesizedAudioChunk(
            1, AudioFormat.Pcm16(24_000), SinePcm(24_000, 1_000, 0.1, 10_000)), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await transport.SendAsync(new SynthesizedAudioChunk(
                2, AudioFormat.Pcm16(24_000), new byte[4_800], IsFinal: true), cancellation.Token));
        await transport.ClearPlaybackAsync(default);
        await transport.SendAsync(new SynthesizedAudioChunk(
            1, AudioFormat.Pcm16(8_000), new byte[320], IsFinal: true), default);

        Assert.Equal(3, socket.SentTextMessages.Count);
        using JsonDocument media = JsonDocument.Parse(socket.SentTextMessages[1]);
        byte[] encoded = Convert.FromBase64String(
            media.RootElement.GetProperty("media").GetProperty("payload").GetString()!);
        Assert.Equal(160, encoded.Length);
        Assert.All(encoded, value => Assert.Equal(0xff, value));
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task MultipleResponsesUseIndependentAudioAndMarks()
    {
        var socket = InitializedSocket();
        TwilioRealtimeAudioTransport transport = await InitializeAsync(socket);

        await transport.SendAsync(new SynthesizedAudioChunk(
            1, AudioFormat.Pcm16(8_000), new byte[320], IsFinal: true), default);
        await transport.SendAsync(new SynthesizedAudioChunk(
            1, AudioFormat.Pcm16(8_000), new byte[640], IsFinal: true), default);

        Assert.Equal(4, socket.SentTextMessages.Count);
        using JsonDocument firstMedia = JsonDocument.Parse(socket.SentTextMessages[0]);
        using JsonDocument firstMark = JsonDocument.Parse(socket.SentTextMessages[1]);
        using JsonDocument secondMedia = JsonDocument.Parse(socket.SentTextMessages[2]);
        using JsonDocument secondMark = JsonDocument.Parse(socket.SentTextMessages[3]);
        Assert.Equal(160, Convert.FromBase64String(
            firstMedia.RootElement.GetProperty("media").GetProperty("payload").GetString()!).Length);
        Assert.Equal("response-1", firstMark.RootElement.GetProperty("mark").GetProperty("name").GetString());
        Assert.Equal(320, Convert.FromBase64String(
            secondMedia.RootElement.GetProperty("media").GetProperty("payload").GetString()!).Length);
        Assert.Equal("response-2", secondMark.RootElement.GetProperty("mark").GetProperty("name").GetString());
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

    private static async Task<byte[]> SendAndCollectMuLawAsync(
        byte[] pcm,
        int sampleRate,
        params int[] chunkLengths)
    {
        Assert.Equal(pcm.Length, chunkLengths.Sum());
        var socket = InitializedSocket();
        TwilioRealtimeAudioTransport transport = await InitializeAsync(socket);
        int offset = 0;
        for (int index = 0; index < chunkLengths.Length; index++)
        {
            int length = chunkLengths[index];
            await transport.SendAsync(new SynthesizedAudioChunk(
                index + 1,
                AudioFormat.Pcm16(sampleRate),
                pcm.AsMemory(offset, length),
                IsFinal: index == chunkLengths.Length - 1), default);
            offset += length;
        }

        var encoded = new List<byte>();
        foreach (string message in socket.SentTextMessages)
        {
            using JsonDocument json = JsonDocument.Parse(message);
            if (json.RootElement.GetProperty("event").GetString() != "media") continue;
            encoded.AddRange(Convert.FromBase64String(
                json.RootElement.GetProperty("media").GetProperty("payload").GetString()!));
        }

        await transport.DisposeAsync();
        return encoded.ToArray();
    }

    private static async Task<byte[]> DecodeMuLawAsync(byte[] encoded)
    {
        var socket = InitializedSocket();
        socket.EnqueueJson(MediaMessage(2, 1, Convert.ToBase64String(encoded)));
        socket.EnqueueJson(StopMessage(3));
        TwilioRealtimeAudioTransport transport = await InitializeAsync(socket);
        var decoded = new List<byte>();
        await foreach (RealtimeAudioFrame frame in transport.ReceiveAsync(default))
            decoded.AddRange(frame.Audio.ToArray());
        await transport.DisposeAsync();
        return decoded.ToArray();
    }

    private static byte[] SinePcm(int sampleRate, double frequency, double seconds, double amplitude)
    {
        int samples = (int)Math.Round(sampleRate * seconds, MidpointRounding.AwayFromZero);
        var pcm = new byte[samples * sizeof(short)];
        for (int index = 0; index < samples; index++)
        {
            short sample = (short)Math.Round(
                amplitude * Math.Sin(2 * Math.PI * frequency * index / sampleRate),
                MidpointRounding.AwayFromZero);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * sizeof(short)), sample);
        }
        return pcm;
    }

    private static byte[] WordLikePcm()
    {
        var pcm = new byte[14_400];
        byte[] firstBurst = SinePcm(24_000, 900, 0.08, 10_000);
        byte[] fricativeBurst = SinePcm(24_000, 6_000, 0.04, 6_000);
        firstBurst.CopyTo(pcm, 960);
        fricativeBurst.CopyTo(pcm, 7_200);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(12_000), 12_000);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(12_002), -12_000);
        return pcm;
    }

    private static double Rms(byte[] pcm, int edgeSamplesToIgnore)
    {
        int sampleCount = pcm.Length / sizeof(short);
        double sumSquares = 0;
        int measured = 0;
        for (int index = edgeSamplesToIgnore; index < sampleCount - edgeSamplesToIgnore; index++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * sizeof(short)));
            sumSquares += (double)sample * sample;
            measured++;
        }
        return Math.Sqrt(sumSquares / measured);
    }

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

    private static object MediaMessage(
        long sequence,
        long chunk,
        string payload,
        string track = "inbound",
        int timestamp = 0) => new
    {
        @event = "media",
        sequenceNumber = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        media = new
        {
            track,
            chunk = chunk.ToString(System.Globalization.CultureInfo.InvariantCulture),
            timestamp = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
