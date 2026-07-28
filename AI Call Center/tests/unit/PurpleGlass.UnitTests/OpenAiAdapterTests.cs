using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PurpleGlass.Adapters.AI.OpenAI;
using PurpleGlass.Adapters.Speech.OpenAI;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.UnitTests;

public sealed class OpenAiAdapterTests
{
    private static readonly RuntimeInvocationContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test-trace");

    [Fact]
    public async Task ConversationMapsBoundedOrderedHistoryAndUsageThroughResponsesGateway()
    {
        OpenAiResponsesRequest? captured = null;
        var gateway = new StubResponsesGateway((request, _) =>
        {
            captured = request;
            return Task.FromResult(new OpenAiResponsesResult(
                "  A concise answer.  ", "resp_test", "gpt-4o-mini", 12, 4));
        });
        var runtime = new OpenAiConversationRuntime(gateway, ConversationOptions());
        ConversationRuntimeConfiguration configuration = Configuration() with
        {
            MaximumHistoryTurns = 3,
            MaximumOutputTokens = 96,
        };
        var request = new AiResponseRequest(
            Context,
            configuration,
            [
                new SanitizedConversationTurn("Caller", "discarded old turn"),
                new SanitizedConversationTurn("Assistant", "discarded old answer"),
                new SanitizedConversationTurn("Caller", "recent question"),
                new SanitizedConversationTurn("Assistant", "recent answer"),
                new SanitizedConversationTurn("System", "must be ignored"),
                new SanitizedConversationTurn("Caller", "current question"),
            ],
            " current question ",
            [],
            new SafetyEscalationPolicy("test", [], []));

        AiResponseResult result = await runtime.GenerateAsync(request, default);

        Assert.Null(result.Failure);
        Assert.Equal("A concise answer.", result.AssistantText);
        Assert.Equal(new AiUsageMetadata(12, 4, "tokens"), result.Usage);
        Assert.Equal("openai", result.Provider);
        Assert.Equal("gpt-4o-mini", result.Model);
        Assert.Equal("resp_test", result.ProviderRequestId);
        Assert.Equal(96, captured?.MaximumOutputTokens);
        Assert.Equal("gpt-4o-mini", captured?.Model);
        Assert.Contains("Development workspace", captured?.Instructions, StringComparison.Ordinal);
        Assert.Collection(Assert.IsAssignableFrom<IReadOnlyList<OpenAiConversationMessage>>(captured?.Messages),
            turn => Assert.Equal(new OpenAiConversationMessage("user", "recent question"), turn),
            turn => Assert.Equal(new OpenAiConversationMessage("assistant", "recent answer"), turn),
            turn => Assert.Equal(new OpenAiConversationMessage("user", "current question"), turn));
        Assert.Equal(1, gateway.InvocationCount);
    }

    [Fact]
    public async Task ConversationConstrainsCallerFacingOutput()
    {
        var runtime = new OpenAiConversationRuntime(
            StubResponsesGateway.Returning("First sentence. Second sentence that is too long."),
            ConversationOptions());
        AiResponseRequest request = ConversationRequest("question", Configuration() with
        {
            MaximumResponseCharacters = 20,
        });

        AiResponseResult result = await runtime.GenerateAsync(request, default);

        Assert.Null(result.Failure);
        Assert.Equal("First sentence.", result.AssistantText);
        Assert.InRange(result.AssistantText.Length, 1, 20);
    }

    [Fact]
    public async Task ConversationRejectsEmptyResponses()
    {
        var runtime = new OpenAiConversationRuntime(
            StubResponsesGateway.Returning("   "), ConversationOptions());

        AiResponseResult result = await runtime.GenerateAsync(ConversationRequest("question"), default);

        Assert.Equal("ai_response_invalid", result.Failure?.Code);
        Assert.False(result.Failure?.Retryable);
    }

    [Theory]
    [InlineData(401, "ai_authentication_failed", false)]
    [InlineData(408, "ai_timeout", true)]
    [InlineData(429, "ai_rate_limited", true)]
    [InlineData(500, "ai_provider_unavailable", true)]
    [InlineData(400, "ai_request_rejected", false)]
    [InlineData(0, "ai_network_failed", true)]
    public async Task ConversationMapsProviderFailuresToSafeBoundedFailures(
        int statusCode,
        string expectedCode,
        bool retryable)
    {
        var gateway = new StubResponsesGateway((_, _) =>
            Task.FromException<OpenAiResponsesResult>(new OpenAiResponsesException(
                statusCode, new InvalidOperationException("secret provider body"))));
        var runtime = new OpenAiConversationRuntime(gateway, ConversationOptions());

        AiResponseResult result = await runtime.GenerateAsync(ConversationRequest("question"), default);

        Assert.Equal(expectedCode, result.Failure?.Code);
        Assert.Equal(retryable, result.Failure?.Retryable);
        Assert.DoesNotContain("secret", result.Failure?.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(result.Failure?.SafeMessage.Length ?? 0, 1, 120);
    }

    [Fact]
    public async Task ConversationMapsNetworkFailureWithoutLeakingException()
    {
        var gateway = new StubResponsesGateway((_, _) =>
            Task.FromException<OpenAiResponsesResult>(new HttpRequestException("secret network detail")));
        var runtime = new OpenAiConversationRuntime(gateway, ConversationOptions());

        AiResponseResult result = await runtime.GenerateAsync(ConversationRequest("question"), default);

        Assert.Equal("ai_network_failed", result.Failure?.Code);
        Assert.DoesNotContain("secret", result.Failure?.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConversationPropagatesCallerCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new StubResponsesGateway(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new OpenAiResponsesResult(string.Empty, null, null, 0, 0);
        });
        var runtime = new OpenAiConversationRuntime(gateway, ConversationOptions());
        using var cancellation = new CancellationTokenSource();
        Task<AiResponseResult> operation = runtime.GenerateAsync(
            ConversationRequest("question"), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public void ConversationOpenAiSelectionRequiresCredential()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            (ConversationOptions() with { ApiKey = string.Empty }).Validate());

        Assert.Contains("API key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecognitionMapsPcmToWaveMultipartAndTranscriptResult()
    {
        MultipartSnapshot multipart = default!;
        var handler = new RecordingHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://unit.openai.test/v1/audio/transcriptions", request.RequestUri?.AbsoluteUri);
            AssertBearerAuthentication(request.Headers, "unit-test-key");
            multipart = await SnapshotMultipartAsync(request, cancellationToken);
            return JsonResponse(HttpStatusCode.OK, """{"text":"  hello caller  "}""");
        });
        using var httpClient = new HttpClient(handler);
        var recognizer = new OpenAiSpeechRecognizer(httpClient, SpeechOptions());
        byte[] pcm = [0x01, 0x02, 0x03, 0x04];

        SpeechRecognitionResult result = await recognizer.RecognizeAsync(
            RecognitionRequest(pcm, "en-US"), default);

        Assert.Null(result.Failure);
        Assert.Equal("hello caller", result.RecognizedText);
        Assert.Equal("en-US", result.Language);
        Assert.True(result.IsFinal);
        Assert.Equal("gpt-4o-mini-transcribe", multipart.Fields["model"]);
        Assert.Equal("en", multipart.Fields["language"]);
        Assert.Equal("json", multipart.Fields["response_format"]);
        Assert.Equal("utterance.wav", multipart.FileName);
        Assert.Equal("audio/wav", multipart.FileContentType);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(multipart.FileBytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(multipart.FileBytes, 8, 4));
        Assert.Equal(8_000, BinaryPrimitives.ReadInt32LittleEndian(multipart.FileBytes.AsSpan(24, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(multipart.FileBytes.AsSpan(22, 2)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(multipart.FileBytes.AsSpan(34, 2)));
        Assert.Equal(pcm.Length, BinaryPrimitives.ReadInt32LittleEndian(multipart.FileBytes.AsSpan(40, 4)));
        Assert.Equal(pcm, multipart.FileBytes[44..]);
    }

    [Fact]
    public async Task RecognitionRejectsOversizedResponse()
    {
        const int maximumBytes = 1024;
        using var httpClient = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[maximumBytes + 1]),
            })));
        var recognizer = new OpenAiSpeechRecognizer(httpClient, SpeechOptions() with
        {
            MaximumTranscriptionResponseBytes = maximumBytes,
        });

        SpeechRecognitionResult result = await recognizer.RecognizeAsync(
            RecognitionRequest([0x01, 0x02]), default);

        Assert.Equal("speech_recognition_response_too_large", result.Failure?.Code);
        Assert.False(result.Failure?.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "speech_recognition_authentication_failed", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "speech_recognition_rate_limited", true)]
    public async Task RecognitionMapsHttpFailuresWithoutLeakingProviderBody(
        HttpStatusCode statusCode,
        string expectedCode,
        bool retryable)
    {
        using var httpClient = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(statusCode, "secret transcript and provider trace"))));
        var recognizer = new OpenAiSpeechRecognizer(httpClient, SpeechOptions());

        SpeechRecognitionResult result = await recognizer.RecognizeAsync(
            RecognitionRequest([0x01, 0x02]), default);

        Assert.Equal(expectedCode, result.Failure?.Code);
        Assert.Equal(retryable, result.Failure?.Retryable);
        Assert.DoesNotContain("secret", result.Failure?.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecognitionMapsProviderTimeoutToRetryableFailure()
    {
        using var httpClient = new HttpClient(new RecordingHttpMessageHandler(
            (_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("provider timeout"))));
        var recognizer = new OpenAiSpeechRecognizer(httpClient, SpeechOptions());

        SpeechRecognitionResult result = await recognizer.RecognizeAsync(
            RecognitionRequest([0x01, 0x02]), default);

        Assert.Equal("speech_recognition_timeout", result.Failure?.Code);
        Assert.True(result.Failure?.Retryable);
    }

    [Fact]
    public async Task SynthesisMapsRequestAndChunksPcmResult()
    {
        JsonElement requestBody = default;
        byte[] pcm = Enumerable.Range(0, 960).Select(value => (byte)value).ToArray();
        var handler = new RecordingHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://unit.openai.test/v1/audio/speech", request.RequestUri?.AbsoluteUri);
            AssertBearerAuthentication(request.Headers, "unit-test-key");
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "audio/pcm");
            requestBody = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken))
                .RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pcm),
            };
        });
        using var httpClient = new HttpClient(handler);
        var synthesizer = new OpenAiSpeechSynthesizer(httpClient, SpeechOptions() with
        {
            AudioChunkBytes = 480,
        });

        SpeechSynthesisResult result = await synthesizer.SynthesizeAsync(new SpeechSynthesisRequest(
            Context,
            "  Helpful response.  ",
            "en-US",
            new VoiceConfiguration("alloy", "calm", 1.25m)), default);

        Assert.Null(result.Failure);
        Assert.Equal("audio/pcm", result.ContentType);
        Assert.Equal("alloy", result.VoiceId);
        Assert.Equal(TimeSpan.FromMilliseconds(20), result.Duration);
        Assert.StartsWith("openai://speech/", result.AudioReference, StringComparison.Ordinal);
        Assert.Equal("pcm16le", result.Metadata["encoding"]);
        Assert.Equal("24000", result.Metadata["sample-rate-hz"]);
        Assert.Equal("en-US", result.Metadata["language"]);
        Assert.Equal("gpt-4o-mini-tts", requestBody.GetProperty("model").GetString());
        Assert.Equal("Helpful response.", requestBody.GetProperty("input").GetString());
        Assert.Equal("alloy", requestBody.GetProperty("voice").GetString());
        Assert.Equal("pcm", requestBody.GetProperty("response_format").GetString());
        Assert.Equal("audio", requestBody.GetProperty("stream_format").GetString());
        Assert.Equal(1.25m, requestBody.GetProperty("speed").GetDecimal());
        Assert.Contains("configured en-US language", requestBody.GetProperty("instructions").GetString(),
            StringComparison.Ordinal);
        Assert.Contains("calm delivery", requestBody.GetProperty("instructions").GetString(),
            StringComparison.Ordinal);
        IReadOnlyList<SynthesizedAudioChunk> chunks = Assert.IsAssignableFrom<
            IReadOnlyList<SynthesizedAudioChunk>>(result.AudioChunks);
        Assert.Collection(chunks,
            first =>
            {
                Assert.Equal(1, first.Sequence);
                Assert.Equal(AudioFormat.Pcm16(24_000), first.Format);
                Assert.Equal(pcm[..480], first.Audio.ToArray());
                Assert.False(first.IsFinal);
            },
            second =>
            {
                Assert.Equal(2, second.Sequence);
                Assert.Equal(AudioFormat.Pcm16(24_000), second.Format);
                Assert.Equal(pcm[480..], second.Audio.ToArray());
                Assert.True(second.IsFinal);
            });
    }

    [Fact]
    public async Task SynthesisRejectsOversizedResponse()
    {
        const int maximumBytes = 16 * 1024;
        using var httpClient = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[maximumBytes + 2]),
            })));
        var synthesizer = new OpenAiSpeechSynthesizer(httpClient, SpeechOptions() with
        {
            MaximumSpeechResponseBytes = maximumBytes,
        });

        SpeechSynthesisResult result = await synthesizer.SynthesizeAsync(SynthesisRequest(), default);

        Assert.Equal("speech_synthesis_response_too_large", result.Failure?.Code);
        Assert.False(result.Failure?.Retryable);
    }

    [Fact]
    public async Task SynthesisRejectsSuccessfulResponseWithNoPcmBytes()
    {
        using var httpClient = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            })));
        var synthesizer = new OpenAiSpeechSynthesizer(httpClient, SpeechOptions());

        SpeechSynthesisResult result = await synthesizer.SynthesizeAsync(SynthesisRequest(), default);

        Assert.Equal("speech_synthesis_response_invalid", result.Failure?.Code);
        Assert.Null(result.AudioChunks);
    }

    [Theory]
    [InlineData("RIFF")]
    [InlineData("ID3\0")]
    [InlineData("OggS")]
    [InlineData("fLaC")]
    public async Task SynthesisRejectsContainerDataWhenRawPcmWasRequested(string signature)
    {
        byte[] responseBody = new byte[32];
        Encoding.ASCII.GetBytes(signature).CopyTo(responseBody, 0);
        using var httpClient = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBody),
            })));
        var synthesizer = new OpenAiSpeechSynthesizer(httpClient, SpeechOptions());

        SpeechSynthesisResult result = await synthesizer.SynthesizeAsync(SynthesisRequest(), default);

        Assert.Equal("speech_synthesis_response_invalid", result.Failure?.Code);
        Assert.Null(result.AudioChunks);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "speech_synthesis_rate_limited", true)]
    [InlineData(HttpStatusCode.BadRequest, "speech_synthesis_rejected", false)]
    public async Task SynthesisMapsHttpFailuresWithoutLeakingProviderBody(
        HttpStatusCode statusCode,
        string expectedCode,
        bool retryable)
    {
        using var httpClient = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(statusCode, "secret caller content and provider trace"))));
        var synthesizer = new OpenAiSpeechSynthesizer(httpClient, SpeechOptions());

        SpeechSynthesisResult result = await synthesizer.SynthesizeAsync(SynthesisRequest(), default);

        Assert.Equal(expectedCode, result.Failure?.Code);
        Assert.Equal(retryable, result.Failure?.Retryable);
        Assert.DoesNotContain("secret", result.Failure?.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SynthesisPropagatesCallerCancellation()
    {
        var handler = new RecordingHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var synthesizer = new OpenAiSpeechSynthesizer(httpClient, SpeechOptions());
        using var cancellation = new CancellationTokenSource();
        Task<SpeechSynthesisResult> operation = synthesizer.SynthesizeAsync(
            SynthesisRequest(), cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private static OpenAiConversationOptions ConversationOptions() => new()
    {
        ApiKey = "unit-test-key",
        BaseUri = new Uri("https://unit.openai.test/v1/"),
        Model = "gpt-4o-mini",
    };

    private static OpenAiSpeechOptions SpeechOptions() => new()
    {
        ApiKey = "unit-test-key",
        BaseUri = new Uri("https://unit.openai.test/v1/"),
        TranscriptionModel = "gpt-4o-mini-transcribe",
        SynthesisModel = "gpt-4o-mini-tts",
    };

    private static ConversationRuntimeConfiguration Configuration() => new()
    {
        Version = "openai-test-v1",
        Language = "en-US",
        VoiceId = "alloy",
        Greeting = "Hello from the development assistant.",
        OfficeName = "Development workspace",
        OfficeHours = "Not configured",
        OfficeLocation = "Not configured",
        SafetyPolicyVersion = "test-safety-v1",
        AiAdapterKey = "openai",
        SpeechRecognitionAdapterKey = "openai",
        SpeechSynthesisAdapterKey = "openai",
        SystemPrompt = "Answer briefly without claiming unavailable capabilities.",
    };

    private static AiResponseRequest ConversationRequest(
        string callerText,
        ConversationRuntimeConfiguration? configuration = null) => new(
        Context,
        configuration ?? Configuration(),
        [],
        callerText,
        [],
        new SafetyEscalationPolicy("test-safety-v1", [], []));

    private static SpeechRecognitionRequest RecognitionRequest(byte[] pcm, string language = "en-US") => new(
        Context,
        language,
        new SimulatedUtteranceInput(string.Empty),
        new SpeechAudioInput(AudioFormat.Pcm16(), pcm, "unit-turn-1"));

    private static SpeechSynthesisRequest SynthesisRequest() => new(
        Context,
        "Helpful response.",
        "en-US",
        new VoiceConfiguration("alloy", "calm", 1.0m));

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static void AssertBearerAuthentication(HttpRequestHeaders headers, string expectedToken)
    {
        Assert.Equal("Bearer", headers.Authorization?.Scheme);
        Assert.Equal(expectedToken, headers.Authorization?.Parameter);
    }

    private static async Task<MultipartSnapshot> SnapshotMultipartAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        MultipartFormDataContent form = Assert.IsType<MultipartFormDataContent>(request.Content);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[]? fileBytes = null;
        string? fileName = null;
        string? fileContentType = null;
        foreach (HttpContent part in form)
        {
            string name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? string.Empty;
            if (name == "file")
            {
                fileBytes = await part.ReadAsByteArrayAsync(cancellationToken);
                fileName = part.Headers.ContentDisposition?.FileName?.Trim('"');
                fileContentType = part.Headers.ContentType?.MediaType;
            }
            else
            {
                fields[name] = await part.ReadAsStringAsync(cancellationToken);
            }
        }

        return new MultipartSnapshot(
            fields,
            Assert.IsType<byte[]>(fileBytes),
            Assert.IsType<string>(fileName),
            Assert.IsType<string>(fileContentType));
    }

    private sealed record MultipartSnapshot(
        IReadOnlyDictionary<string, string> Fields,
        byte[] FileBytes,
        string FileName,
        string FileContentType);

    private sealed class StubResponsesGateway(
        Func<OpenAiResponsesRequest, CancellationToken, Task<OpenAiResponsesResult>> response)
        : IOpenAiResponsesGateway
    {
        public int InvocationCount { get; private set; }

        public Task<OpenAiResponsesResult> CreateAsync(
            OpenAiResponsesRequest request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return response(request, cancellationToken);
        }

        public static StubResponsesGateway Returning(string text) => new((request, _) =>
            Task.FromResult(new OpenAiResponsesResult(text, "resp_test", request.Model, 1, 1)));
    }

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int InvocationCount { get; private set; }

        public TaskCompletionSource RequestStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            RequestStarted.TrySetResult();
            return responder(request, cancellationToken);
        }
    }
}
