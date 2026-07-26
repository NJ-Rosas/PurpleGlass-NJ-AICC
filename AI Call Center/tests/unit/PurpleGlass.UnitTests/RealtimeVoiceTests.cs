using System.Globalization;
using System.Buffers.Binary;
using System.Text;
using PurpleGlass.Adapters.AI.Mock;
using PurpleGlass.Adapters.Audio.Fake;
using PurpleGlass.Adapters.Speech.Mock;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.WebBff;

namespace PurpleGlass.UnitTests;

public sealed class RealtimeVoiceTests
{
    private static readonly RuntimeInvocationContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test-trace");

    [Theory]
    [InlineData("OpenAI", "Fake", "Fake", false, false, "SpeechToText:Provider=OpenAI", "Providers:EnableRealSpeech=true")]
    [InlineData("Fake", "OpenAI", "Fake", false, false, "TextToSpeech:Provider=OpenAI", "Providers:EnableRealSpeech=true")]
    [InlineData("Fake", "Fake", "OpenAI", true, false, "LanguageModel:Provider=OpenAI", "Providers:EnableRealAI=true")]
    public void ProviderSwitchMismatchIdentifiesExactSettingAndExpectedValue(
        string speechToText, string textToSpeech, string languageModel,
        bool realSpeech, bool realAi, string invalidSetting, string expectedSetting)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            VoiceProviderConfigurationValidator.Validate(
                speechToText, textToSpeech, languageModel, realSpeech, realAi));

        Assert.Contains("voice_provider_configuration_invalid", exception.Message, StringComparison.Ordinal);
        Assert.Contains(invalidSetting, exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedSetting, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Task10ProviderSwitchesAreCompatible() =>
        VoiceProviderConfigurationValidator.Validate("OpenAI", "OpenAI", "Fake", true, false);

    [Fact]
    public void ExplicitEndpointFinalizesOneDeterministicUtterance()
    {
        using var detector = new RealtimeTurnDetector(Options());
        DateTimeOffset receivedAt = DateTimeOffset.Parse("2026-07-26T12:00:00Z", CultureInfo.InvariantCulture);
        RealtimeAudioFrame frame = Frame(7, "hello", receivedAt, speechStarted: true, endOfUtterance: true);

        TurnDetectionResult result = detector.Push(frame);

        Assert.True(result.SpeechStarted);
        Assert.False(result.Duplicate);
        FinalizedVoiceUtterance utterance = Assert.IsType<FinalizedVoiceUtterance>(result.FinalizedUtterance);
        Assert.Equal(7, utterance.FirstSequence);
        Assert.Equal(7, utterance.LastSequence);
        Assert.Equal(AudioFormat.SyntheticText, utterance.Format);
        Assert.Equal("hello", Encoding.UTF8.GetString(utterance.Audio.Span));
        Assert.Equal(receivedAt, utterance.StartedAtUtc);
        Assert.Equal(receivedAt, utterance.EndedAtUtc);
        Assert.False(detector.IsSpeechActive);

        using var replay = new RealtimeTurnDetector(Options());
        Assert.Equal(utterance.TurnId, replay.Push(frame).FinalizedUtterance!.TurnId);
    }

    [Fact]
    public void DuplicateFrameIsSuppressedWithoutDuplicatingTranscriptAudio()
    {
        using var detector = new RealtimeTurnDetector(Options());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _ = detector.Push(Frame(10, "once", now, speechStarted: true));

        TurnDetectionResult duplicate = detector.Push(Frame(
            10, "duplicate", now.AddMilliseconds(20), speechStarted: true, endOfUtterance: true));

        Assert.True(duplicate.Duplicate);
        Assert.Null(duplicate.FinalizedUtterance);
        FinalizedVoiceUtterance flushed = Assert.IsType<FinalizedVoiceUtterance>(detector.Flush());
        Assert.Equal(10, flushed.FirstSequence);
        Assert.Equal(10, flushed.LastSequence);
        Assert.Equal("once", Encoding.UTF8.GetString(flushed.Audio.Span));
    }

    [Fact]
    public void ConfiguredSilenceFinalizesActiveCallerTurn()
    {
        RealtimeVoiceOptions options = Options() with
        {
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(40),
        };
        using var detector = new RealtimeTurnDetector(options);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _ = detector.Push(Frame(1, "caller words", now, speechStarted: true));

        TurnDetectionResult firstSilence = detector.Push(Silence(2, now.AddMilliseconds(20)));
        TurnDetectionResult endpoint = detector.Push(Silence(3, now.AddMilliseconds(40)));

        Assert.Null(firstSilence.FinalizedUtterance);
        Assert.NotNull(endpoint.FinalizedUtterance);
        Assert.Equal(1, endpoint.FinalizedUtterance.FirstSequence);
        Assert.Equal(3, endpoint.FinalizedUtterance.LastSequence);
        Assert.False(detector.IsSpeechActive);
    }

    [Fact]
    public void SilenceLowNoiseAndShortTransientNeverBecomeCallerUtterances()
    {
        using var detector = new RealtimeTurnDetector(Options());
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-26T12:00:00Z", CultureInfo.InvariantCulture);
        var finalized = new List<FinalizedVoiceUtterance>();
        var rejected = new List<RejectedVoiceCandidate>();
        long sequence = 1;

        for (int index = 0; index < 150; index++)
            Capture(detector.Push(PcmFrame(sequence++, now, amplitude: 0)), finalized, rejected);
        for (int index = 0; index < 150; index++)
            Capture(detector.Push(PcmFrame(sequence++, now, amplitude: 200)), finalized, rejected);
        Capture(detector.Push(PcmFrame(sequence++, now, amplitude: 12_000)), finalized, rejected);
        for (int index = 0; index < 30; index++)
            Capture(detector.Push(PcmFrame(sequence++, now, amplitude: 0)), finalized, rejected);

        Assert.Empty(finalized);
        Assert.False(detector.IsSpeechActive);
        RejectedVoiceCandidate transient = Assert.Single(rejected);
        Assert.Equal("speech_too_short", transient.DiscardReason);
        Assert.False(detector.Flush() is not null);
    }

    [Fact]
    public void QualifiedSpeechSeparatedByBriefPauseProducesExactlyOneUtterance()
    {
        RealtimeVoiceOptions options = Options() with
        {
            MinimumSpeechDuration = TimeSpan.FromMilliseconds(120),
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(500),
        };
        using var detector = new RealtimeTurnDetector(options);
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-26T12:00:00Z", CultureInfo.InvariantCulture);
        var finalized = new List<FinalizedVoiceUtterance>();
        var rejected = new List<RejectedVoiceCandidate>();
        long sequence = 1;

        for (int index = 0; index < 8; index++)
            Capture(detector.Push(PcmFrame(sequence++, now, amplitude: 8_000)), finalized, rejected);
        for (int index = 0; index < 5; index++)
            Capture(detector.Push(PcmFrame(sequence++, now, amplitude: 0)), finalized, rejected);
        for (int index = 0; index < 8; index++)
            Capture(detector.Push(PcmFrame(sequence++, now, amplitude: 8_000)), finalized, rejected);
        for (int index = 0; index < 25; index++)
            Capture(detector.Push(PcmFrame(sequence++, now, amplitude: 0)), finalized, rejected);

        FinalizedVoiceUtterance utterance = Assert.Single(finalized);
        Assert.Empty(rejected);
        Assert.Equal(46, utterance.InboundFrames);
        Assert.Equal(TimeSpan.FromMilliseconds(920), utterance.Duration);
        Assert.False(detector.IsSpeechActive);
    }

    [Fact]
    public void VoiceResponsePolicyNormalizesAndConstrainsLongResponses()
    {
        const string response = "  First concise sentence.   This second sentence is intentionally too long for spoken output.  ";

        string constrained = VoiceResponsePolicy.Constrain(response, 40);

        Assert.Equal("First concise sentence.", constrained);
        Assert.InRange(constrained.Length, 1, 40);
        Assert.Equal("I'm sorry, I'm having trouble responding right now.",
            VoiceResponsePolicy.Constrain("   ", 80));
    }

    [Fact]
    public void VoiceResponsePolicyNeverExceedsHardLimitWithoutWordBoundaries()
    {
        string constrained = VoiceResponsePolicy.Constrain(new string('x', 100), 20);

        Assert.Equal(20, constrained.Length);
        Assert.EndsWith("…", constrained, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FakeSpeechRecognitionMapsSyntheticAudioDeterministically()
    {
        var recognizer = new MockSpeechRecognizer(
            new MockSpeechOptions { RecognitionConfidence = 0.93m }, TimeProvider.System);
        byte[] audio = Encoding.UTF8.GetBytes("  can you hear me  ");
        var request = new SpeechRecognitionRequest(
            Context,
            "en-US",
            new SimulatedUtteranceInput(string.Empty),
            new SpeechAudioInput(AudioFormat.SyntheticText, audio, "turn-1"));

        SpeechRecognitionResult first = await recognizer.RecognizeAsync(request, default);
        SpeechRecognitionResult replay = await recognizer.RecognizeAsync(request, default);

        Assert.Equal("can you hear me", first.RecognizedText);
        Assert.Equal(first.RecognizedText, replay.RecognizedText);
        Assert.Equal(0.93m, first.Confidence);
        Assert.True(first.IsFinal);
        Assert.Null(first.Failure);
    }

    [Fact]
    public async Task FakeSpeechSynthesisProducesFinalSyntheticAudioChunk()
    {
        var synthesizer = new MockSpeechSynthesizer(new MockSpeechOptions(), TimeProvider.System);

        SpeechSynthesisResult result = await synthesizer.SynthesizeAsync(
            new SpeechSynthesisRequest(
                Context, "Brief response", "en-US", new VoiceConfiguration("calm-a", SpeakingRate: 1.1m)),
            default);

        Assert.Null(result.Failure);
        SynthesizedAudioChunk chunk = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<SynthesizedAudioChunk>>(
            result.AudioChunks));
        Assert.Equal(1, chunk.Sequence);
        Assert.Equal(AudioFormat.SyntheticText, chunk.Format);
        Assert.Equal("Brief response", Encoding.UTF8.GetString(chunk.Audio.Span));
        Assert.True(chunk.IsFinal);
    }

    [Fact]
    public async Task FakeAudioTransportClearsPlaybackAndBoundsCapturedOutput()
    {
        await using var transport = new FakeRealtimeAudioTransport("fake-call", "fake-stream");
        for (int sequence = 1; sequence <= 257; sequence++)
        {
            await transport.SendAsync(new SynthesizedAudioChunk(
                sequence, AudioFormat.SyntheticText,
                Encoding.UTF8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture)), sequence == 257), default);
        }

        await transport.ClearPlaybackAsync(default);
        await transport.ClearPlaybackAsync(default);
        await transport.CompleteAsync("test_complete", default);

        Assert.Equal(256, transport.OutboundChunks.Count);
        Assert.Equal(2, transport.OutboundChunks[0].Sequence);
        Assert.Equal(257, transport.OutboundChunks[^1].Sequence);
        Assert.Equal(2, transport.ClearPlaybackCount);
        Assert.Equal("test_complete", transport.CompletionReason);
    }

    [Fact]
    public async Task DisabledProvidersReturnBoundedNonRetryableFailures()
    {
        var speech = new DisabledSpeechProvider();
        var languageModel = new DisabledAiConversationRuntime();
        SpeechRecognitionResult recognition = await speech.RecognizeAsync(
            new SpeechRecognitionRequest(Context, "en-US", new SimulatedUtteranceInput("hello")), default);
        SpeechSynthesisResult synthesis = await speech.SynthesizeAsync(
            new SpeechSynthesisRequest(Context, "hello", "en-US", new VoiceConfiguration("calm-a")), default);
        AiResponseResult generation = await languageModel.GenerateAsync(new AiResponseRequest(
            Context, Configuration(), [], "hello", [], new SafetyEscalationPolicy("test", [], [])), default);

        Assert.Equal("speech_recognition_disabled", recognition.Failure?.Code);
        Assert.False(recognition.Failure?.Retryable);
        Assert.Equal("speech_synthesis_disabled", synthesis.Failure?.Code);
        Assert.False(synthesis.Failure?.Retryable);
        Assert.Equal("language_model_disabled", generation.Failure?.Code);
        Assert.False(generation.Failure?.Retryable);
        Assert.DoesNotContain("credential", string.Join(' ',
            recognition.Failure?.SafeMessage, synthesis.Failure?.SafeMessage, generation.Failure?.SafeMessage),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("I need an appointment", "Thanks for the test message. This development assistant can continue a general voice conversation.")]
    [InlineData("My name is Test Caller", "Thanks for the test message. This development assistant can continue a general voice conversation.")]
    [InlineData("What are your hours?", "Thanks for the test message. This development assistant can continue a general voice conversation.")]
    [InlineData("I need a human", "Human transfer is not available in this development assistant.")]
    [InlineData("My tooth hurts", "I can't provide medical advice. If this may be an emergency, contact local emergency services.")]
    public async Task FakeLanguageModelMakesNoUnsupportedWorkflowOrStaffClaims(
        string callerText,
        string expectedResponse)
    {
        var languageModel = new MockAiConversationRuntime(new MockAiOptions(), TimeProvider.System);
        ConversationRuntimeConfiguration configuration = Configuration();
        AiResponseResult result = await languageModel.GenerateAsync(new AiResponseRequest(
            Context,
            configuration,
            [],
            callerText,
            [],
            new SafetyEscalationPolicy(configuration.SafetyPolicyVersion, [], [])), default);

        Assert.Equal(expectedResponse, result.AssistantText);
        Assert.DoesNotContain("staff", result.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recorded", result.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("office team", result.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("appointment", result.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("booked", result.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insurance", result.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("patient", result.AssistantText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SimulatedProviderTimeoutHonorsPipelineCancellation()
    {
        var recognizer = new MockSpeechRecognizer(new MockSpeechOptions(), TimeProvider.System);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recognizer.RecognizeAsync(
            new SpeechRecognitionRequest(
                Context, "en-US", new SimulatedUtteranceInput("", "timeout"),
                new SpeechAudioInput(AudioFormat.SyntheticText, Encoding.UTF8.GetBytes("hello"), "timeout-turn")),
            cancellation.Token));
    }

    [Fact]
    public async Task FakeAudioReceiveWaitHonorsCancellation()
    {
        await using var transport = new FakeRealtimeAudioTransport("fake-call", inputCapacity: 1);
        using var cancellation = new CancellationTokenSource();
        await using IAsyncEnumerator<RealtimeAudioFrame> enumerator =
            transport.ReceiveAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await enumerator.MoveNextAsync().AsTask());
    }

    [Fact]
    public void RealtimeOptionsRejectUnboundedTimeoutAndQueueValues()
    {
        Assert.Throws<InvalidOperationException>(() => (Options() with
        {
            RecognitionTimeout = TimeSpan.Zero,
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Options() with
        {
            AudioQueueCapacity = int.MaxValue,
        }).Validate());
    }

    private static RealtimeVoiceOptions Options() => new()
    {
        Conversation = Configuration(),
    };

    private static ConversationRuntimeConfiguration Configuration() => new()
    {
        Version = "voice-test-v1",
        Language = "en-US",
        VoiceId = "calm-a",
        Greeting = "Hello, this is the PurpleGlass development voice assistant.",
        OfficeName = "Development workspace",
        OfficeHours = "Not configured",
        OfficeLocation = "Not configured",
        SafetyPolicyVersion = "development-safety-v1",
        AiAdapterKey = "mock-ai",
        SpeechRecognitionAdapterKey = "mock-speech",
        SpeechSynthesisAdapterKey = "mock-speech",
    };

    private static RealtimeAudioFrame Frame(
        long sequence,
        string text,
        DateTimeOffset receivedAt,
        bool speechStarted = false,
        bool endOfUtterance = false) =>
        new(sequence, AudioFormat.SyntheticText, Encoding.UTF8.GetBytes(text), receivedAt,
            speechStarted, endOfUtterance);

    private static RealtimeAudioFrame Silence(long sequence, DateTimeOffset receivedAt) =>
        new(sequence, AudioFormat.SyntheticText, ReadOnlyMemory<byte>.Empty, receivedAt);

    private static RealtimeAudioFrame PcmFrame(long sequence, DateTimeOffset receivedAt, short amplitude)
    {
        const int samples = 160;
        var pcm = new byte[samples * sizeof(short)];
        for (int index = 0; index < samples; index++)
        {
            short sample = amplitude == 0 ? (short)0
                : (short)(index % 8 < 4 ? amplitude : -amplitude);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * sizeof(short)), sample);
        }
        return new RealtimeAudioFrame(
            sequence, AudioFormat.Pcm16(), pcm,
            receivedAt.AddMilliseconds((sequence - 1) * 20));
    }

    private static void Capture(
        TurnDetectionResult result,
        List<FinalizedVoiceUtterance> finalized,
        List<RejectedVoiceCandidate> rejected)
    {
        if (result.FinalizedUtterance is not null) finalized.Add(result.FinalizedUtterance);
        if (result.RejectedCandidate is not null) rejected.Add(result.RejectedCandidate);
    }
}
