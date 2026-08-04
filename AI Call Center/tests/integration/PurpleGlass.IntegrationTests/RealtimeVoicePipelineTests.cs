using System.Text;
using System.Data.Common;
using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PurpleGlass.Adapters.Audio.Fake;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Contracts;
using PurpleGlass.Modules.Conversation.Infrastructure;
using PurpleGlass.SharedKernel;

namespace PurpleGlass.IntegrationTests;

[Collection(DurablePathGroup.Name)]
public sealed class RealtimeVoicePipelineTests(DurablePathFixture fixture)
{
    private const string Greeting = "Hello from the PurpleGlass test assistant.";
    private const string FallbackResponse = "I'm sorry, I'm having trouble responding right now.";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] GreetingCallerSpeakers = ["Assistant", "Caller"];
    private static readonly string[] GreetingCallerAssistantSpeakers = ["Assistant", "Caller", "Assistant"];

    [Fact]
    public async Task NormalConversationFlowsFromAudioThroughProvidersAndPersistsTranscriptAndOutbox()
    {
        await using SessionHarness harness = await CreateHarnessAsync();
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Hello");
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        SpeechRecognitionRequest recognition = Assert.Single(harness.Recognizer.Requests);
        SpeechAudioInput input = Assert.IsType<SpeechAudioInput>(recognition.AudioInput);
        Assert.Equal("Hello", Encoding.UTF8.GetString(input.Audio.Span));
        AiResponseRequest generation = Assert.Single(harness.LanguageModel.Requests);
        Assert.Equal("Hello", generation.CurrentCallerTurn);
        Assert.Equal("Hello from the assistant.", harness.Synthesizer.Requests[1].Text);
        Assert.Contains("Hello from the assistant.", DecodeOutput(harness.Transport));
        string[] latencyStages = harness.Diagnostics.Latencies.Select(item => item.Stage).ToArray();
        Assert.Contains("stt_request_started", latencyStages);
        Assert.Contains("stt_completed", latencyStages);
        Assert.Contains("llm_request_started", latencyStages);
        Assert.Contains("llm_completed", latencyStages);
        Assert.Contains("tts_first_audio", latencyStages);
        Assert.Contains("first_twilio_media_sent", latencyStages);
        Assert.Contains("tts_completed", latencyStages);
        Assert.All(harness.Diagnostics.Latencies, diagnostic =>
        {
            Assert.True(double.IsFinite(diagnostic.DurationMs) && diagnostic.DurationMs >= 0);
            Assert.True(double.IsFinite(diagnostic.ElapsedFromEndpointMs)
                && diagnostic.ElapsedFromEndpointMs >= 0);
            Assert.InRange(diagnostic.Stage.Length, 1, 64);
            Assert.InRange(diagnostic.Adapter.Length, 1, 64);
            Assert.InRange(diagnostic.Result.Length, 1, 64);
        });

        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal("Completed", details.State);
        Assert.Equal(
            new[]
            {
                (Speaker: "Assistant", Text: Greeting),
                (Speaker: "Caller", Text: "Hello"),
                (Speaker: "Assistant", Text: "Hello from the assistant."),
            },
            details.Transcript.OrderBy(turn => turn.SequenceNumber)
                .Select(turn => (turn.Speaker, turn.Text)).ToArray());

        await using var eventing = fixture.CreateEventing();
        Assert.True(await eventing.OutboxMessages.AnyAsync(message =>
            message.CorrelationId == harness.CorrelationId
            && message.MessageType == nameof(UserSpeechRecognized)));
        Assert.True(await eventing.OutboxMessages.AnyAsync(message =>
            message.CorrelationId == harness.CorrelationId
            && message.MessageType == nameof(AIResponseGenerated)));
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Theory]
    [InlineData("en-US", "Please speak Spanish.", "es-US")]
    [InlineData("es-US", "Please speak English.", "en-US")]
    [InlineData("es-PR", "Háblame en inglés.", "en-US")]
    [InlineData("en-US", "Cambia a español de Puerto Rico.", "es-PR")]
    public async Task ExplicitLanguageRequestSwitchesGenerationAndSynthesisAndPersistsHistory(
        string startingLanguage,
        string callerRequest,
        string targetLanguage)
    {
        var languageModel = new ControlledAiRuntime((request, _) =>
            request.LanguageContext?.CurrentLanguageCode.StartsWith("es", StringComparison.Ordinal) == true
                ? "Claro, continuamos en español." : "Sure, I'll continue in English.");
        await using SessionHarness harness = await CreateHarnessAsync(
            languageModel: languageModel,
            startingLanguageCode: startingLanguage,
            startingLanguageReason: CallLanguageReasons.CallOverride);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync(callerRequest);
        Task listening = harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        Assert.True(ReferenceEquals(listening, await Task.WhenAny(listening, run)),
            $"Session ended early: {string.Join(',', harness.Diagnostics.Exceptions.Select(item => $"{item.Stage}:{item.SafeCode}:{item.RootExceptionType}"))}");
        await listening;
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        AiResponseRequest request = Assert.Single(harness.LanguageModel.Requests);
        Assert.Equal(targetLanguage, request.Configuration.Language);
        Assert.NotNull(request.LanguageContext);
        Assert.Equal(targetLanguage, request.LanguageContext.ActiveLanguageCode);
        Assert.Equal(startingLanguage, request.LanguageContext.PriorLanguageCode);
        Assert.Equal(targetLanguage, request.LanguageContext.CurrentLanguageCode);
        Assert.True(request.LanguageContext.SwitchAccepted);
        Assert.True(request.LanguageContext.AcknowledgementNeeded);
        Assert.False(request.LanguageContext.UnsupportedFallbackSelected);
        Assert.Equal(targetLanguage, harness.Synthesizer.Requests[1].Language);
        Assert.DoesNotContain("cannot", harness.Synthesizer.Requests[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsupported", harness.Synthesizer.Requests[1].Text, StringComparison.OrdinalIgnoreCase);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(targetLanguage, details.Language);
        ConversationLanguageChangeProjection change = Assert.Single(details.LanguageChanges!);
        Assert.Equal(startingLanguage, change.PreviousLanguageCode);
        Assert.Equal(targetLanguage, change.LanguageCode);
        Assert.Equal(CallLanguageReasons.CallerExplicitRequest, change.Reason);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task RepeatedMeaningfulDetectedTurnsSwitchAutomaticallyWithoutFlapping()
    {
        var recognizer = new ControlledSpeechRecognizer(resultResponse: (request, _) =>
        {
            string text = Encoding.UTF8.GetString(request.AudioInput!.Audio.Span);
            return new SpeechRecognitionResult(text, 0.96m, request.Language, null, null, true,
                DetectedLanguages: ["es"], DetectionConfidence: 0.96m);
        });
        await using SessionHarness harness = await CreateHarnessAsync(recognizer: recognizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Necesito hacer una cita con el dentista mañana.");
        await harness.Transport.QueueUtteranceAsync("También tengo dolor en una muela desde ayer.");
        Task listening = harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 3);
        Assert.True(ReferenceEquals(listening, await Task.WhenAny(listening, run)),
            $"Session ended early: {string.Join(',', harness.Diagnostics.Exceptions.Select(item => $"{item.Stage}:{item.SafeCode}:{item.RootExceptionType}"))}");
        await listening;
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Equal(["en-US", "es-US"],
            harness.LanguageModel.Requests.Select(request => request.Configuration.Language).ToArray());
        Assert.Equal(["en-US", "en-US", "es-US"],
            harness.Synthesizer.Requests.Select(request => request.Language).ToArray());
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal("es-US", details.Language);
        ConversationLanguageChangeProjection change = Assert.Single(details.LanguageChanges!);
        Assert.Equal(CallLanguageReasons.AutomaticDetection, change.Reason);
        Assert.Equal(0.96m, change.DetectionConfidence);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Theory]
    [InlineData("en-US", "Please speak English.")]
    [InlineData("es-US", "Por favor, habla español.")]
    public async Task AlreadyActiveLanguageRequestKeepsStateWithoutDuplicateEventOrInability(
        string activeLanguage,
        string callerRequest)
    {
        var languageModel = new ControlledAiRuntime((request, _) =>
            request.Configuration.Language.StartsWith("es", StringComparison.Ordinal)
                ? "Ya estamos hablando en español." : "We're already speaking English.");
        await using SessionHarness harness = await CreateHarnessAsync(
            languageModel: languageModel,
            startingLanguageCode: activeLanguage,
            startingLanguageReason: CallLanguageReasons.CallOverride);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync(callerRequest);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        AiResponseRequest request = Assert.Single(harness.LanguageModel.Requests);
        Assert.Equal(activeLanguage, request.LanguageContext!.ActiveLanguageCode);
        Assert.False(request.LanguageContext.SwitchAccepted);
        Assert.True(request.LanguageContext.AcknowledgementNeeded);
        Assert.False(request.LanguageContext.UnsupportedFallbackSelected);
        Assert.DoesNotContain("cannot", harness.Synthesizer.Requests[1].Text, StringComparison.OrdinalIgnoreCase);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(activeLanguage, details.Language);
        Assert.Empty(details.LanguageChanges!);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Theory]
    [InlineData("en-US", "Please speak French.", "I can currently continue in English or Spanish.")]
    [InlineData("es-US", "Por favor, habla francés.", "Actualmente puedo continuar en inglés o español.")]
    public async Task UnsupportedLanguageRequestRetainsStateAndUsesOnlyBoundedFallback(
        string activeLanguage,
        string callerRequest,
        string expectedResponse)
    {
        await using SessionHarness harness = await CreateHarnessAsync(
            startingLanguageCode: activeLanguage,
            startingLanguageReason: CallLanguageReasons.CallOverride);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync(callerRequest);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Empty(harness.LanguageModel.Requests);
        Assert.Equal(expectedResponse, harness.Synthesizer.Requests[1].Text);
        Assert.Equal(activeLanguage, harness.Synthesizer.Requests[1].Language);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(activeLanguage, details.Language);
        Assert.Empty(details.LanguageChanges!);
        VoiceLanguageDiagnostic diagnostic = Assert.Single(harness.Diagnostics.Languages);
        Assert.True(diagnostic.UnsupportedRequest);
        Assert.True(diagnostic.UnsupportedFallbackSelected);
        Assert.Equal("application_generated", diagnostic.AcknowledgementSource);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task SpanishStartingLanguageControlsGreetingSttAgentAndTtsForCompleteSession()
    {
        await using SessionHarness harness = await CreateHarnessAsync(
            startingLanguageCode: "es_pr", startingLanguageReason: CallLanguageReasons.LocationDefault);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        Assert.Equal("es-PR", harness.Synthesizer.Requests[0].Language);
        Assert.Equal(SupportedCallLanguages.Require("es-PR").Greeting, harness.Synthesizer.Requests[0].Text);
        await harness.Transport.QueueUtteranceAsync("Necesito confirmar el horario de la oficina.");
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Equal("es-PR", Assert.Single(harness.Recognizer.Requests).Language);
        AiResponseRequest generation = Assert.Single(harness.LanguageModel.Requests);
        Assert.Equal("es-PR", generation.Configuration.Language);
        Assert.Contains("Puerto Rico Spanish (es-PR)", generation.Behavior.Instructions, StringComparison.Ordinal);
        Assert.Equal("es-PR", harness.Synthesizer.Requests[1].Language);
        Assert.Equal(2, harness.Synthesizer.Requests.Count);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal("es-PR", details.Language);
        Assert.Empty(details.LanguageChanges!);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task ExplicitSwitchDuringPlaybackClearsAndCancelsOldLanguageBeforeNewResponse()
    {
        var languageModel = new ControlledAiRuntime((_, invocation) =>
            invocation == 1 ? "Old English response." : "Nueva respuesta en español.");
        var synthesizer = new ControlledSpeechSynthesizer(blockOnInvocation: 2);
        await using SessionHarness harness = await CreateHarnessAsync(
            languageModel: languageModel, synthesizer: synthesizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Tell me about your office hours.");
        await synthesizer.WaitForInvocationsAsync(2);
        await harness.Transport.QueueUtteranceAsync("Speak Spanish.");
        await synthesizer.WaitForInvocationsAsync(3);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.True(synthesizer.CancellationObserved);
        Assert.Equal(1, harness.Transport.ClearPlaybackCount);
        Assert.Equal("es-US", synthesizer.Requests[2].Language);
        Assert.DoesNotContain("Old English response.", DecodeOutput(harness.Transport));
        Assert.Contains("Nueva respuesta en español.", DecodeOutput(harness.Transport));
        Assert.Contains(harness.Diagnostics.Languages,
            diagnostic => diagnostic.SwitchAccepted
                && diagnostic.ActiveLanguage == "es-US"
                && diagnostic.Reason == CallLanguageReasons.CallerExplicitRequest);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task GreetingWaitsForExplicitMediaReadinessBeforeSendingAudio()
    {
        await using SessionHarness harness = await CreateHarnessAsync();
        harness.Transport.HoldMediaReady();

        Task run = harness.Start();
        await harness.Synthesizer.WaitForInvocationsAsync(1);

        Assert.Equal(1, harness.Transport.MediaReadyWaitCount);
        Assert.Empty(harness.Transport.OutboundChunks);

        harness.Transport.ReleaseMediaReady();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);
        Assert.NotEmpty(harness.Transport.OutboundChunks);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task DisconnectBeforeGreetingMediaReadinessCancelsWithoutSendingOrDuplicatingAudio()
    {
        await using SessionHarness harness = await CreateHarnessAsync();
        harness.Transport.HoldMediaReady();
        Task run = harness.Start();
        await harness.Synthesizer.WaitForInvocationsAsync(1);

        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Single(harness.Synthesizer.Requests);
        Assert.Empty(harness.Transport.OutboundChunks);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task DentalAppointmentTurnsPreservePriorCallerAndAssistantHistory()
    {
        var languageModel = new ControlledAiRuntime((_, invocation) =>
            invocation == 1 ? "First answer." : "Second answer.");
        await using SessionHarness harness = await CreateHarnessAsync(languageModel: languageModel);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("I need to make an appointment");
        await harness.Transport.QueueUtteranceAsync("My back tooth hurts when I drink cold water");
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 3);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Equal(2, languageModel.Requests.Count);
        AiResponseRequest second = languageModel.Requests[1];
        Assert.Equal("My back tooth hurts when I drink cold water", second.CurrentCallerTurn);
        Assert.Contains(second.ExistingTurns,
            turn => turn.Speaker == "Caller" && turn.Text == "I need to make an appointment");
        Assert.Contains(second.ExistingTurns,
            turn => turn.Speaker == "Assistant" && turn.Text == "First answer.");
        Assert.Contains("virtual receptionist", second.Behavior.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("appointment_booking", second.Behavior.UnsupportedActions);
        Assert.Empty(second.AvailableTools);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(2, details.Transcript.Count(turn => turn.Speaker == "Caller"));
        Assert.Equal(3, details.Transcript.Count(turn => turn.Speaker == "Assistant"));
        Assert.Equal(
            [Greeting, "I need to make an appointment", "First answer.",
                "My back tooth hurts when I drink cold water", "Second answer."],
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Text).ToArray());
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task ModelContextIsTenantScopedAndExcludesAnotherTenantHistory()
    {
        const string tenantASecret = "Tenant A private conversation marker";
        var tenantAModel = new ControlledAiRuntime();
        await using (SessionHarness tenantA = await CreateHarnessAsync(languageModel: tenantAModel))
        {
            Task tenantARun = tenantA.Start();
            _ = await tenantA.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);
            await tenantA.Transport.QueueUtteranceAsync(tenantASecret);
            _ = await tenantA.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
            tenantA.Transport.CompleteInput();
            await tenantARun.WaitAsync(TestTimeout);
            await AssertCompletedAndDisposedAsync(tenantA, "media_disconnected");
        }

        var tenantBModel = new ControlledAiRuntime();
        await using SessionHarness tenantB = await CreateHarnessAsync(languageModel: tenantBModel);
        Task tenantBRun = tenantB.Start();
        _ = await tenantB.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);
        await tenantB.Transport.QueueUtteranceAsync("Tenant B question");
        _ = await tenantB.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        tenantB.Transport.CompleteInput();
        await tenantBRun.WaitAsync(TestTimeout);

        AiResponseRequest request = Assert.Single(tenantBModel.Requests);
        Assert.Equal(tenantB.TenantId, request.Context.TenantId);
        Assert.Equal(tenantB.LocationId, request.Context.LocationId);
        Assert.DoesNotContain(request.ExistingTurns,
            turn => turn.Text.Contains(tenantASecret, StringComparison.Ordinal));
        Assert.DoesNotContain(tenantASecret, request.CurrentCallerTurn, StringComparison.Ordinal);
        Assert.DoesNotContain(tenantASecret, request.Behavior.Instructions, StringComparison.Ordinal);
        await AssertCompletedAndDisposedAsync(tenantB, "media_disconnected");
    }

    [Fact]
    public async Task CallerBargeInClearsPlaybackCancelsCurrentSynthesisAndProcessesNextTurn()
    {
        var languageModel = new ControlledAiRuntime((_, invocation) =>
            invocation == 1 ? "Interrupted answer." : "Answer after interruption.");
        var synthesizer = new ControlledSpeechSynthesizer(blockOnInvocation: 2);
        await using SessionHarness harness = await CreateHarnessAsync(
            languageModel: languageModel,
            synthesizer: synthesizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("First question");
        await harness.Recognizer.WaitForInvocationsAsync(1);
        await languageModel.WaitForInvocationsAsync(1);
        await synthesizer.WaitForInvocationsAsync(2);
        await harness.Transport.QueueUtteranceAsync("Second question");
        VoiceSessionStateChange interrupted = await harness.States.WaitForAsync(
            change => change.State == VoiceSessionState.Interrupted);
        await languageModel.WaitForInvocationsAsync(2);
        await synthesizer.WaitForInvocationsAsync(3);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Null(interrupted.SafeCode);
        Assert.True(synthesizer.CancellationObserved);
        Assert.Equal(1, harness.Transport.ClearPlaybackCount);
        string[] output = DecodeOutput(harness.Transport);
        Assert.DoesNotContain("Interrupted answer.", output);
        Assert.Contains("Answer after interruption.", output);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(2, details.Transcript.Count(turn => turn.Speaker == "Caller"));
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task CallerBargeInDuringRecognitionCancelsPendingTurnAndProcessesNewUtterance()
    {
        var recognizer = new ControlledSpeechRecognizer(blockOnInvocation: 1);
        await using SessionHarness harness = await CreateHarnessAsync(recognizer: recognizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("First question");
        await recognizer.WaitForInvocationsAsync(1);
        await harness.Transport.QueueUtteranceAsync("Second question");
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Interrupted);
        await recognizer.WaitForInvocationsAsync(2);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.True(recognizer.CancellationObserved);
        Assert.Equal(2, recognizer.Requests.Count);
        Assert.Equal(1, harness.Transport.ClearPlaybackCount);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        LiveTranscriptTurn caller = Assert.Single(details.Transcript, turn => turn.Speaker == "Caller");
        Assert.Equal("Second question", caller.Text);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task NoiseCandidateDoesNotClearPlaybackButConfirmedShortSpeechDoes()
    {
        var recognizer = new ControlledSpeechRecognizer(response: (_, invocation) =>
            invocation == 1 ? "First question" : "Help me");
        var synthesizer = new ControlledSpeechSynthesizer(blockOnInvocation: 2);
        await using SessionHarness harness = await CreateHarnessAsync(
            recognizer: recognizer,
            synthesizer: synthesizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("First question");
        await synthesizer.WaitForInvocationsAsync(2);
        long sequence = 100;
        sequence = await QueueNoiseFramesAsync(harness.Transport, sequence, 15, amplitude: 3_000);
        await Task.Delay(100);

        Assert.False(synthesizer.CancellationObserved);
        Assert.Equal(0, harness.Transport.ClearPlaybackCount);

        sequence = await QueuePcmFramesAsync(harness.Transport, sequence, 6, amplitude: 8_000);
        _ = await QueuePcmFramesAsync(harness.Transport, sequence, 25, amplitude: 0);
        await synthesizer.WaitForInvocationsAsync(3);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.True(synthesizer.CancellationObserved);
        Assert.Equal(1, harness.Transport.ClearPlaybackCount);
        Assert.Equal(2, recognizer.Requests.Count);
        Assert.Equal(2, harness.LanguageModel.Requests.Count);
        Assert.Contains("Hello from the assistant.", DecodeOutput(harness.Transport));
        Assert.DoesNotContain(harness.States.Changes,
            change => change.State == VoiceSessionState.Failed);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task SpeechAfterAllMediaSentButBeforeMarkAcknowledgementStillClearsPlayback()
    {
        var recognizer = new ControlledSpeechRecognizer(response: (_, invocation) =>
            invocation == 1 ? "First question" : "Interrupting question");
        await using SessionHarness harness = await CreateHarnessAsync(recognizer: recognizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        harness.Transport.AutoAcknowledgePlayback = false;
        await harness.Transport.QueueUtteranceAsync("First question");
        await harness.Synthesizer.WaitForInvocationsAsync(2);
        using (var timeout = new CancellationTokenSource(TestTimeout))
        {
            while (harness.Transport.OutboundChunks.Count < 2)
                await Task.Delay(10, timeout.Token);
        }

        harness.Transport.AutoAcknowledgePlayback = true;
        await harness.Transport.QueueUtteranceAsync("Interrupting question");
        using (var timeout = new CancellationTokenSource(TestTimeout))
        {
            while (harness.Transport.ClearPlaybackCount == 0)
                await Task.Delay(10, timeout.Token);
        }
        await harness.Synthesizer.WaitForInvocationsAsync(3);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Equal(1, harness.Transport.ClearPlaybackCount);
        Assert.Equal(2, recognizer.Requests.Count);
        Assert.Equal(2, harness.LanguageModel.Requests.Count);
        Assert.DoesNotContain(harness.States.Changes,
            change => change.State == VoiceSessionState.Failed);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task CallerBargeInDuringCallerTurnCommitDoesNotPoisonTheNextTurn()
    {
        var interceptor = new BlockingCallerTurnCommandInterceptor();
        await using SessionHarness harness = await CreateHarnessAsync(commandInterceptor: interceptor);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Interrupted caller turn");
        await interceptor.WaitUntilBlockedAsync();
        await harness.Transport.QueueUtteranceAsync("Second caller turn");
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Interrupted);
        interceptor.Release();
        await harness.Recognizer.WaitForInvocationsAsync(2);
        Task responseStarted = harness.LanguageModel.WaitForInvocationsAsync(1);
        Task next = await Task.WhenAny(responseStarted, run).WaitAsync(TestTimeout);
        Assert.Same(responseStarted, next);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.DoesNotContain(harness.States.Changes,
            change => change.State == VoiceSessionState.Failed);
        Assert.Equal("Completed", details.State);
        Assert.Equal(["Interrupted caller turn", "Second caller turn"],
            details.Transcript.Where(turn => turn.Speaker == "Caller")
                .OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Text).ToArray());
        Assert.False(interceptor.CancellationObserved);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task ProviderCompletionDuringCallerTurnCommitUsesFreshCleanupContext()
    {
        var interceptor = new BlockingCallerTurnCommandInterceptor();
        await using SessionHarness harness = await CreateHarnessAsync(commandInterceptor: interceptor);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Caller turn during provider completion");
        await interceptor.WaitUntilBlockedAsync();
        await harness.CompleteFromProviderAsync();
        await interceptor.WaitUntilCanceledAsync();
        await run.WaitAsync(TestTimeout);

        Assert.DoesNotContain(harness.States.Changes,
            change => change.State == VoiceSessionState.Failed);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal("Completed", details.State);
        Assert.Single(details.Transcript);
        Assert.Equal("Assistant", details.Transcript[0].Speaker);
        var call = await harness.Calls.GetAsync(harness.TenantId, harness.CallId, default);
        Assert.Equal("Completed", call.State);
        await AssertCompletedAndDisposedAsync(harness, "provider_completed");
    }

    [Fact]
    public async Task HangupDuringSpeechRecognitionCancelsProviderAndCleansUpSession()
    {
        var recognizer = new ControlledSpeechRecognizer(blockOnInvocation: 1);
        await using SessionHarness harness = await CreateHarnessAsync(recognizer: recognizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Hang up in STT");
        await recognizer.WaitForInvocationsAsync(1);
        await harness.RequestHangupAsync();
        await run.WaitAsync(TestTimeout);

        Assert.True(recognizer.CancellationObserved);
        Assert.Empty(harness.LanguageModel.Requests);
        Assert.Single(harness.Synthesizer.Requests);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Single(details.Transcript);
        await AssertCompletedAndDisposedAsync(harness, "hangup");
    }

    [Fact]
    public async Task HangupDuringLanguageModelGenerationCancelsProviderAndCleansUpSession()
    {
        var languageModel = new ControlledAiRuntime(blockOnInvocation: 1);
        await using SessionHarness harness = await CreateHarnessAsync(languageModel: languageModel);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Hang up in LLM");
        await languageModel.WaitForInvocationsAsync(1);
        await harness.RequestHangupAsync();
        await run.WaitAsync(TestTimeout);

        Assert.True(languageModel.CancellationObserved);
        Assert.Single(harness.Synthesizer.Requests);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(GreetingCallerSpeakers,
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Speaker).ToArray());
        await AssertCompletedAndDisposedAsync(harness, "hangup");
    }

    [Fact]
    public async Task HangupDuringSpeechSynthesisCancelsProviderPlaybackAndCleansUpSession()
    {
        var synthesizer = new ControlledSpeechSynthesizer(blockOnInvocation: 2);
        await using SessionHarness harness = await CreateHarnessAsync(synthesizer: synthesizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Hang up in TTS");
        await synthesizer.WaitForInvocationsAsync(2);
        await harness.RequestHangupAsync();
        await run.WaitAsync(TestTimeout);

        Assert.True(synthesizer.CancellationObserved);
        Assert.Single(harness.Transport.OutboundChunks);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(GreetingCallerAssistantSpeakers,
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Speaker).ToArray());
        await AssertCompletedAndDisposedAsync(harness, "hangup");
    }

    [Fact]
    public async Task ProviderCompletionDuringSecondResponseEndsMultiTurnCallWithoutVoiceFailure()
    {
        var languageModel = new ControlledAiRuntime((_, invocation) =>
            invocation == 1 ? "First answer." : "Second answer.");
        var synthesizer = new ControlledSpeechSynthesizer(blockOnInvocation: 3);
        await using SessionHarness harness = await CreateHarnessAsync(
            languageModel: languageModel,
            synthesizer: synthesizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("First question");
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        await harness.Transport.QueueUtteranceAsync("Second question");
        await synthesizer.WaitForInvocationsAsync(3);
        await harness.CompleteFromProviderAsync();
        await run.WaitAsync(TestTimeout);

        Assert.True(synthesizer.CancellationObserved);
        Assert.DoesNotContain(harness.States.Changes,
            change => change.State == VoiceSessionState.Failed);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal("Completed", details.State);
        Assert.Equal([Greeting, "First question", "First answer.", "Second question", "Second answer."],
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Text).ToArray());
        var call = await harness.Calls.GetAsync(harness.TenantId, harness.CallId, default);
        Assert.Equal("Completed", call.State);
        await AssertCompletedAndDisposedAsync(harness, "provider_completed");
    }

    [Fact]
    public async Task SpeechRecognitionFailurePublishesSafeFailureAndLeavesNoCallerTurn()
    {
        var recognizer = new ControlledSpeechRecognizer(resultResponse: (request, _) =>
            new SpeechRecognitionResult(string.Empty, null, request.Language, null, null, false,
                new RuntimeFailure("speech_recognition_response_invalid",
                    "Speech recognition returned an invalid response.", false),
                ResponseDiagnostic: new SpeechRecognitionResponseDiagnostic(
                    "speech_recognition", "response_parsing", "invalid_response_schema",
                    "success", "json", "missing_text", false, false, "absent")));
        await using SessionHarness harness = await CreateHarnessAsync(recognizer: recognizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Unrecognized audio");
        VoiceSessionStateChange failed = await harness.States.WaitForAsync(
            change => change.State == VoiceSessionState.Failed);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Equal("speech_recognition_response_invalid", failed.SafeCode);
        Assert.Empty(harness.LanguageModel.Requests);
        Assert.Equal(FallbackResponse, harness.Synthesizer.Requests[1].Text);
        VoiceSpeechRecognitionDiagnostic diagnostic = Assert.Single(harness.Diagnostics.SpeechRecognitions);
        Assert.Equal("invalid_response_schema", diagnostic.ResultCategory);
        Assert.Equal("missing_text", diagnostic.ResponseShapeCategory);
        Assert.Equal("end_turn", diagnostic.RecoveryDecision);
        Assert.False(diagnostic.TranscriptPresent);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Single(details.Transcript);
        Assert.Equal("Assistant", details.Transcript[0].Speaker);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task TransientSpeechRecognitionFailureEndsOnlyTurnWithoutRetryAtOneAttemptDefault()
    {
        var recognizer = new ControlledSpeechRecognizer(resultResponse: (request, _) =>
            new SpeechRecognitionResult(string.Empty, null, request.Language, null, null, false,
                new RuntimeFailure("speech_recognition_unavailable",
                    "Speech recognition is temporarily unavailable.", true),
                ResponseDiagnostic: new SpeechRecognitionResponseDiagnostic(
                    "speech_recognition", "response_headers_received", "provider_transient",
                    "server_error", "json", "provider_error_status", false, false, "absent")));
        await using SessionHarness harness = await CreateHarnessAsync(recognizer: recognizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Transient provider failure");
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Failed);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Single(recognizer.Requests);
        Assert.Empty(harness.LanguageModel.Requests);
        VoiceSpeechRecognitionDiagnostic diagnostic = Assert.Single(harness.Diagnostics.SpeechRecognitions);
        Assert.Equal("provider_transient", diagnostic.ResultCategory);
        Assert.False(diagnostic.RetryAttempted);
        Assert.Equal("end_turn", diagnostic.RecoveryDecision);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Single(details.Transcript);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task FormerCallOneEmptyTranscriptionDiscardsOnlyThatTurnAndSessionContinues()
    {
        var recognizer = new ControlledSpeechRecognizer(resultResponse: (request, invocation) =>
            invocation == 1
                ? new SpeechRecognitionResult(string.Empty, null, request.Language, null, null, true,
                    ResponseDiagnostic: new SpeechRecognitionResponseDiagnostic(
                        "speech_recognition", "response_completed", "empty_result", "success",
                        "event_stream", "valid_empty_transcript", false, false, "absent"))
                : new SpeechRecognitionResult(
                    Encoding.UTF8.GetString(request.AudioInput!.Audio.Span), 0.99m,
                    request.Language, null, null, true));
        await using SessionHarness harness = await CreateHarnessAsync(
            recognizer: recognizer,
            startingLanguageCode: "en-US",
            startingLanguageReason: CallLanguageReasons.CallOverride);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Audio with no usable transcription");
        await harness.Transport.QueueUtteranceAsync("Second turn succeeds");
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.DoesNotContain(harness.States.Changes, change => change.State == VoiceSessionState.Failed);
        Assert.Equal(2, harness.Recognizer.Requests.Count);
        Assert.Single(harness.LanguageModel.Requests);
        Assert.Equal("Second turn succeeds", harness.LanguageModel.Requests[0].CurrentCallerTurn);
        VoiceSpeechRecognitionDiagnostic empty = Assert.Single(
            harness.Diagnostics.SpeechRecognitions, item => item.ResultCategory == "empty_result");
        Assert.Equal("discard_turn", empty.RecoveryDecision);
        Assert.False(empty.TranscriptPresent);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal([Greeting, "Second turn succeeds", "Hello from the assistant."],
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Text).ToArray());
        Assert.Empty(details.LanguageChanges!);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Theory]
    [InlineData(null, "absent")]
    [InlineData("fr", "unsupported")]
    public async Task MissingOrUnsupportedDetectedLanguageDoesNotRejectValidTranscript(
        string? detectedLanguage,
        string expectedSupportCategory)
    {
        var recognizer = new ControlledSpeechRecognizer(resultResponse: (request, _) =>
            new SpeechRecognitionResult("Valid caller turn", 0.95m, request.Language, null, null, true,
                DetectedLanguages: detectedLanguage is null ? [] : [detectedLanguage],
                ResponseDiagnostic: new SpeechRecognitionResponseDiagnostic(
                    "speech_recognition", "response_completed", "success", "success",
                    "event_stream", "valid", true, detectedLanguage is not null,
                    detectedLanguage is null ? "absent" : "present")));
        await using SessionHarness harness = await CreateHarnessAsync(recognizer: recognizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Valid caller turn");
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Single(harness.LanguageModel.Requests);
        Assert.DoesNotContain(harness.States.Changes, change => change.State == VoiceSessionState.Failed);
        VoiceSpeechRecognitionDiagnostic diagnostic = Assert.Single(harness.Diagnostics.SpeechRecognitions);
        Assert.Equal(expectedSupportCategory, diagnostic.LanguageSupportCategory);
        Assert.Equal("continue", diagnostic.RecoveryDecision);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(GreetingCallerAssistantSpeakers,
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Speaker).ToArray());
        Assert.Empty(details.LanguageChanges!);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task LateSpeechRecognitionCompletionAfterHangupIsDiscardedBeforePersistence()
    {
        var recognizer = new ControlledSpeechRecognizer(ignoreCancellationOnInvocation: 1);
        await using SessionHarness harness = await CreateHarnessAsync(recognizer: recognizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Late provider response");
        await recognizer.WaitForInvocationsAsync(1);
        await harness.RequestHangupAsync();
        recognizer.ReleaseIgnoredCancellation();
        await run.WaitAsync(TestTimeout);

        Assert.Empty(harness.LanguageModel.Requests);
        Assert.Single(harness.Synthesizer.Requests);
        Assert.DoesNotContain(harness.States.Changes, change => change.State == VoiceSessionState.Failed);
        VoiceSpeechRecognitionDiagnostic diagnostic = Assert.Single(harness.Diagnostics.SpeechRecognitions);
        Assert.True(diagnostic.CancellationRequested);
        Assert.True(diagnostic.SessionClosing);
        Assert.Equal("session_closing", diagnostic.RecoveryDecision);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Single(details.Transcript);
        await AssertCompletedAndDisposedAsync(harness, "hangup");
    }

    [Fact]
    public async Task LanguageModelFailurePersistsFallbackAndAllowsLaterTurns()
    {
        var languageModel = new ControlledAiRuntime(
            failureOnInvocation: 1,
            failure: new RuntimeFailure("ai_generation_failed", "The assistant is temporarily unavailable.", false));
        await using SessionHarness harness = await CreateHarnessAsync(languageModel: languageModel);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Trigger model failure");
        VoiceSessionStateChange failed = await harness.States.WaitForAsync(
            change => change.State == VoiceSessionState.Failed);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        await harness.Transport.QueueUtteranceAsync("Continue after model failure");
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 3);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Equal("ai_generation_failed", failed.SafeCode);
        Assert.Equal(FallbackResponse, harness.Synthesizer.Requests[1].Text);
        Assert.Contains(FallbackResponse, DecodeOutput(harness.Transport));
        Assert.Equal(2, languageModel.Requests.Count);
        Assert.Equal("Hello from the assistant.", harness.Synthesizer.Requests[2].Text);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(["Assistant", "Caller", "Assistant", "Caller", "Assistant"],
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Speaker).ToArray());
        Assert.Equal(FallbackResponse,
            details.Transcript.Single(turn => turn.SequenceNumber == 3).Text);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task SpeechSynthesisFailurePublishesSafeFailureWithoutSendingInvalidAudio()
    {
        var synthesizer = new ControlledSpeechSynthesizer(
            failureOnInvocation: 2,
            failure: new RuntimeFailure("speech_synthesis_failed", "Speech synthesis is unavailable.", false));
        await using SessionHarness harness = await CreateHarnessAsync(synthesizer: synthesizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Trigger synthesis failure");
        VoiceSessionStateChange failed = await harness.States.WaitForAsync(
            change => change.State == VoiceSessionState.Failed);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Equal("speech_synthesis_failed", failed.SafeCode);
        Assert.Equal(2, synthesizer.Requests.Count);
        Assert.Single(harness.Transport.OutboundChunks);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(GreetingCallerAssistantSpeakers,
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Speaker).ToArray());
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task WrongTenantCannotCreateOrReadVoiceConversation()
    {
        await using SessionHarness harness = await CreateHarnessAsync();
        Guid wrongTenant = Guid.NewGuid();
        ConversationApplicationException createFailure = await Assert.ThrowsAsync<ConversationApplicationException>(() =>
            harness.Conversations.CreateAsync(new CreateConversation(
                wrongTenant, harness.LocationId, harness.CallId, Guid.NewGuid(),
                "realtime-integration-v1", "en-US"), default));
        Assert.Equal("call_not_found", createFailure.Code);

        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);

        ConversationApplicationException readFailure = await Assert.ThrowsAsync<ConversationApplicationException>(() =>
            harness.Conversations.GetAsync(wrongTenant, details.ConversationId, default));
        Assert.Equal("conversation_not_found", readFailure.Code);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task DuplicateMediaFramesProduceExactlyOneCallerTurn()
    {
        await using SessionHarness harness = await CreateHarnessAsync();
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);
        var duplicate = new RealtimeAudioFrame(
            41,
            AudioFormat.SyntheticText,
            Encoding.UTF8.GetBytes("Only once"),
            DateTimeOffset.UtcNow,
            SpeechStarted: true,
            EndOfUtterance: true);

        await harness.Transport.QueueFrameAsync(duplicate);
        await harness.Transport.QueueFrameAsync(duplicate);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Single(harness.Recognizer.Requests);
        Assert.Single(harness.LanguageModel.Requests);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        LiveTranscriptTurn caller = Assert.Single(details.Transcript, turn => turn.Speaker == "Caller");
        Assert.Equal("Only once", caller.Text);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task TenSecondsSilenceAndNoiseConsumeNoTurnsAndShortSpeechStillWorksAfterward()
    {
        var recognizer = new ControlledSpeechRecognizer(response: (_, _) => "Yes");
        await using SessionHarness harness = await CreateHarnessAsync(recognizer: recognizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);
        long sequence = 1;

        sequence = await QueuePcmFramesAsync(harness.Transport, sequence, 500, amplitude: 0);
        sequence = await QueuePcmFramesAsync(harness.Transport, sequence, 100, amplitude: 200);
        await Task.Delay(100);

        Assert.Empty(harness.Recognizer.Requests);
        Assert.Empty(harness.LanguageModel.Requests);
        Assert.Single(harness.Synthesizer.Requests);
        Assert.Equal(VoiceSessionState.Listening, harness.Session.State);
        Assert.DoesNotContain(harness.States.Changes,
            change => change.SafeCode == "maximum_turns");

        sequence = await QueuePcmFramesAsync(harness.Transport, sequence, 6, amplitude: 8_000);
        _ = await QueuePcmFramesAsync(harness.Transport, sequence, 25, amplitude: 0);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);

        Assert.Single(harness.Recognizer.Requests);
        Assert.Single(harness.LanguageModel.Requests);
        Assert.Equal(2, harness.Synthesizer.Requests.Count);

        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal([Greeting, "Yes", "Hello from the assistant."],
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Text).ToArray());
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task OneQualifiedPcmUtteranceSubmitsSttOnceAndLaterSilenceStaysSilent()
    {
        var recognizer = new ControlledSpeechRecognizer(response: (_, _) => "One clear sentence");
        await using SessionHarness harness = await CreateHarnessAsync(recognizer: recognizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);
        long sequence = 1;

        sequence = await QueuePcmFramesAsync(harness.Transport, sequence, 20, amplitude: 0);
        sequence = await QueuePcmFramesAsync(harness.Transport, sequence, 8, amplitude: 8_000);
        sequence = await QueuePcmFramesAsync(harness.Transport, sequence, 25, amplitude: 0);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        _ = await QueuePcmFramesAsync(harness.Transport, sequence, 100, amplitude: 0);
        await Task.Delay(100);

        Assert.Single(harness.Recognizer.Requests);
        Assert.Single(harness.LanguageModel.Requests);
        Assert.Equal(2, harness.Synthesizer.Requests.Count);
        Assert.Equal(VoiceSessionState.Listening, harness.Session.State);

        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal([Greeting, "One clear sentence", "Hello from the assistant."],
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Text).ToArray());
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task StopRequestedBeforeRuntimeStartCancelsImmediatelyAndCleansUpTransport()
    {
        await using SessionHarness harness = await CreateHarnessAsync();

        harness.Session.RequestStop("hangup");
        await harness.Start().WaitAsync(TestTimeout);

        Assert.Empty(harness.Recognizer.Requests);
        Assert.Empty(harness.LanguageModel.Requests);
        Assert.Empty(harness.Synthesizer.Requests);
        await AssertCompletedAndDisposedAsync(harness, "hangup");
    }

    [Fact]
    public async Task MediaDisconnectDuringLanguageModelGenerationCancelsActiveTurn()
    {
        var languageModel = new ControlledAiRuntime(blockOnInvocation: 1);
        await using SessionHarness harness = await CreateHarnessAsync(languageModel: languageModel);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Disconnect in LLM");
        await languageModel.WaitForInvocationsAsync(1);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.True(languageModel.CancellationObserved);
        Assert.Single(harness.Synthesizer.Requests);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(GreetingCallerSpeakers,
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Speaker).ToArray());
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    private async Task<SessionHarness> CreateHarnessAsync(
        ControlledSpeechRecognizer? recognizer = null,
        ControlledAiRuntime? languageModel = null,
        ControlledSpeechSynthesizer? synthesizer = null,
        DbCommandInterceptor? commandInterceptor = null,
        string startingLanguageCode = "en-US",
        string startingLanguageReason = CallLanguageReasons.Fallback)
    {
        Guid tenantId = Guid.NewGuid();
        Guid locationId = Guid.NewGuid();
        Guid correlationId = Guid.NewGuid();
        string providerCallId = $"FA-voice-{Guid.NewGuid():N}";
        CallManagementDbContext calls = fixture.CreateCalls();
        ConversationDbContext conversations = fixture.CreateConversations();
        var callService = new CallManagementService(calls, TimeProvider.System);
        try
        {
            _ = await callService.RegisterInboundAsync(new RegisterInboundCall(
                tenantId,
                locationId,
                providerCallId,
                "+15550001001",
                "+15550001002",
                correlationId,
                Provider: "Fake",
                StartingLanguageCode: startingLanguageCode,
                StartingLanguageReason: startingLanguageReason), default);
            VoiceCallContext call = await callService.ConnectVoiceMediaAsync("Fake", providerCallId, default);
            var conversationService = new ConversationService(conversations, callService, TimeProvider.System);
            var transport = new FakeRealtimeAudioTransport(providerCallId, $"FM-{Guid.NewGuid():N}");
            var states = new RecordingVoiceSessionStateSink();
            recognizer ??= new ControlledSpeechRecognizer();
            languageModel ??= new ControlledAiRuntime();
            synthesizer ??= new ControlledSpeechSynthesizer();
            var persistence = new FixtureRealtimeConversationPersistence(fixture, commandInterceptor);
            var diagnostics = new RecordingVoiceSessionDiagnostics();
            var session = new RealtimeVoiceSession(
                persistence,
                recognizer,
                languageModel,
                synthesizer,
                Options(),
                states,
                TimeProvider.System,
                diagnostics);
            var identity = new VoiceSessionIdentity(
                call.TenantId,
                call.LocationId,
                call.CallId,
                call.Direction,
                call.Provider,
                call.ProviderCallId,
                transport.ProviderMediaStreamId,
                call.CorrelationId,
                call.StartingLanguageCode,
                call.StartingLanguageReason);
            return new SessionHarness(
                calls,
                conversations,
                callService,
                conversationService,
                session,
                identity,
                transport,
                states,
                recognizer,
                languageModel,
                synthesizer,
                diagnostics);
        }
        catch
        {
            await conversations.DisposeAsync();
            await calls.DisposeAsync();
            throw;
        }
    }

    private async Task<ConversationDetails> ReadDetailsAsync(Guid tenantId, Guid callId)
    {
        await using CallManagementDbContext calls = fixture.CreateCalls();
        await using ConversationDbContext conversations = fixture.CreateConversations();
        var callService = new CallManagementService(calls, TimeProvider.System);
        var conversationService = new ConversationService(conversations, callService, TimeProvider.System);
        return await conversationService.GetDetailsForCallAsync(tenantId, callId, default)
            ?? throw new Xunit.Sdk.XunitException("The durable voice conversation was not found.");
    }

    private static RealtimeVoiceOptions Options() => new()
    {
        Conversation = new ConversationRuntimeConfiguration
        {
            Version = "realtime-integration-v1",
            Language = "en-US",
            VoiceId = "test-voice",
            Greeting = Greeting,
            OfficeName = "PurpleGlass Test Office",
            OfficeHours = "Not configured",
            OfficeLocation = "Not configured",
            SafetyPolicyVersion = "safety-test-v1",
            AiAdapterKey = "controlled-ai",
            SpeechRecognitionAdapterKey = "controlled-speech",
            SpeechSynthesisAdapterKey = "controlled-speech",
            MaximumTurns = 6,
            InactivityTimeout = TimeSpan.FromSeconds(30),
            MaximumDuration = TimeSpan.FromSeconds(30),
            MaximumHistoryTurns = 12,
            MaximumOutputTokens = 64,
            MaximumResponseCharacters = 200,
            SystemPrompt = "You are a deterministic PurpleGlass integration-test assistant.",
        },
        RecognitionTimeout = TimeSpan.FromSeconds(10),
        LanguageModelTimeout = TimeSpan.FromSeconds(10),
        SynthesisTimeout = TimeSpan.FromSeconds(10),
        CleanupTimeout = TimeSpan.FromSeconds(2),
        EndOfUtteranceSilence = TimeSpan.FromMilliseconds(100),
        MaximumUtteranceDuration = TimeSpan.FromSeconds(10),
        AudioQueueCapacity = 16,
        UtteranceQueueCapacity = 4,
        MaximumAudioBytesPerUtterance = 16 * 1024,
        MaximumProviderAttempts = 1,
    };

    private static string[] DecodeOutput(FakeRealtimeAudioTransport transport) =>
        transport.OutboundChunks.Select(chunk => Encoding.UTF8.GetString(chunk.Audio.ToArray())).ToArray();

    private static async Task<long> QueuePcmFramesAsync(
        FakeRealtimeAudioTransport transport,
        long firstSequence,
        int count,
        short amplitude)
    {
        for (int frameIndex = 0; frameIndex < count; frameIndex++)
        {
            var pcm = new byte[160 * sizeof(short)];
            for (int sampleIndex = 0; sampleIndex < 160; sampleIndex++)
            {
                short sample = amplitude == 0 ? (short)0 : (short)Math.Round(
                    amplitude * Math.Sin(2 * Math.PI * 1_000 * sampleIndex / 8_000),
                    MidpointRounding.AwayFromZero);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sampleIndex * sizeof(short)), sample);
            }
            long sequence = firstSequence + frameIndex;
            await transport.QueueFrameAsync(new RealtimeAudioFrame(
                sequence,
                AudioFormat.Pcm16(),
                pcm,
                DateTimeOffset.UtcNow.AddMilliseconds(sequence * 20)));
        }
        return firstSequence + count;
    }

    private static async Task<long> QueueNoiseFramesAsync(
        FakeRealtimeAudioTransport transport,
        long firstSequence,
        int count,
        short amplitude)
    {
        for (int frameIndex = 0; frameIndex < count; frameIndex++)
        {
            var pcm = new byte[160 * sizeof(short)];
            for (int sampleIndex = 0; sampleIndex < 160; sampleIndex++)
            {
                int magnitude = amplitude - ((sampleIndex * 37) % Math.Max(1, amplitude / 3));
                short sample = (short)(sampleIndex % 2 == 0 ? magnitude : -magnitude);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sampleIndex * sizeof(short)), sample);
            }
            long sequence = firstSequence + frameIndex;
            await transport.QueueFrameAsync(new RealtimeAudioFrame(
                sequence,
                AudioFormat.Pcm16(),
                pcm,
                DateTimeOffset.UtcNow.AddMilliseconds(sequence * 20)));
        }
        return firstSequence + count;
    }

    private static async Task AssertCompletedAndDisposedAsync(SessionHarness harness, string reason)
    {
        Assert.Equal(VoiceSessionState.Ended, harness.Session.State);
        Assert.Equal(reason, harness.Transport.CompletionReason);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            harness.Transport.QueueUtteranceAsync("after-disposal").AsTask());
    }

    private sealed class SessionHarness : IAsyncDisposable
    {
        private readonly CallManagementDbContext calls;
        private readonly ConversationDbContext conversations;
        private readonly CancellationTokenSource lifetime = new();
        private Task? run;

        public SessionHarness(
            CallManagementDbContext calls,
            ConversationDbContext conversations,
            CallManagementService callsService,
            ConversationService conversationService,
            RealtimeVoiceSession session,
            VoiceSessionIdentity identity,
            FakeRealtimeAudioTransport transport,
            RecordingVoiceSessionStateSink states,
            ControlledSpeechRecognizer recognizer,
            ControlledAiRuntime languageModel,
            ControlledSpeechSynthesizer synthesizer,
            RecordingVoiceSessionDiagnostics diagnostics)
        {
            this.calls = calls;
            this.conversations = conversations;
            Calls = callsService;
            Conversations = conversationService;
            Session = session;
            Identity = identity;
            Transport = transport;
            States = states;
            Recognizer = recognizer;
            LanguageModel = languageModel;
            Synthesizer = synthesizer;
            Diagnostics = diagnostics;
        }

        public CallManagementService Calls { get; }
        public ConversationService Conversations { get; }
        public RealtimeVoiceSession Session { get; }
        public VoiceSessionIdentity Identity { get; }
        public FakeRealtimeAudioTransport Transport { get; }
        public RecordingVoiceSessionStateSink States { get; }
        public ControlledSpeechRecognizer Recognizer { get; }
        public ControlledAiRuntime LanguageModel { get; }
        public ControlledSpeechSynthesizer Synthesizer { get; }
        public RecordingVoiceSessionDiagnostics Diagnostics { get; }
        public Guid TenantId => Identity.TenantId;
        public Guid LocationId => Identity.LocationId;
        public Guid CallId => Identity.CallId;
        public Guid CorrelationId => Identity.CorrelationId;

        public Task Start() => run ??= Session.RunAsync(Identity, Transport, lifetime.Token);

        public async Task RequestHangupAsync()
        {
            _ = await Calls.RequestHangupAsync(
                new RequestCallHangup(TenantId, LocationId, CallId), default);
            Session.RequestStop("hangup");
        }

        public async Task CompleteFromProviderAsync()
        {
            _ = await Calls.ApplyProviderStatusAsync(new ApplyProviderCallStatus(
                Identity.Provider, Identity.ProviderCallId, "completed",
                $"completion-{Guid.NewGuid():N}"), default);
            Session.RequestStop("provider_completed");
        }

        public async ValueTask DisposeAsync()
        {
            if (run is { IsCompleted: false })
            {
                Session.RequestStop("test_cleanup");
                lifetime.Cancel();
                try { await run.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception) { }
            }
            if (run is null) await Transport.DisposeAsync();
            lifetime.Dispose();
            await conversations.DisposeAsync();
            await calls.DisposeAsync();
        }
    }

    private sealed class RecordingVoiceSessionStateSink : IVoiceSessionStateSink
    {
        private readonly object synchronization = new();
        private readonly List<VoiceSessionStateChange> changes = [];
        private TaskCompletionSource published = NewSignal();

        public IReadOnlyList<VoiceSessionStateChange> Changes
        {
            get { lock (synchronization) return changes.ToArray(); }
        }

        public ValueTask PublishAsync(VoiceSessionStateChange change, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource signal;
            lock (synchronization)
            {
                changes.Add(change);
                signal = published;
                published = NewSignal();
            }
            signal.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async Task<VoiceSessionStateChange> WaitForAsync(Func<VoiceSessionStateChange, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            while (true)
            {
                Task signal;
                lock (synchronization)
                {
                    VoiceSessionStateChange? match = changes.LastOrDefault(predicate);
                    if (match is not null) return match;
                    signal = published.Task;
                }

                try { await signal.WaitAsync(timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException("The expected realtime voice state was not published.");
                }
            }
        }

        public async Task<VoiceSessionStateChange> WaitForOccurrencesAsync(
            VoiceSessionState state,
            int count)
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            while (true)
            {
                Task signal;
                lock (synchronization)
                {
                    VoiceSessionStateChange[] matches = changes.Where(change => change.State == state).ToArray();
                    if (matches.Length >= count) return matches[^1];
                    signal = published.Task;
                }

                try { await signal.WaitAsync(timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException("The expected realtime voice state occurrence was not published.");
                }
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingCallerTurnCommandInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int turnInsertCommands;

        public Task WaitUntilBlockedAsync() => blocked.Task.WaitAsync(TestTimeout);
        public Task WaitUntilCanceledAsync() => canceled.Task.WaitAsync(TestTimeout);
        public bool CancellationObserved => canceled.Task.IsCompleted;
        public void Release() => release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await BlockCallerTurnAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await BlockCallerTurnAsync(command, cancellationToken);
            return result;
        }

        private async Task BlockCallerTurnAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (!command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal)
                || !command.CommandText.Contains("conversation_turns", StringComparison.Ordinal)
                || Interlocked.Increment(ref turnInsertCommands) != 2)
                return;
            blocked.TrySetResult();
            try
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class FixtureRealtimeConversationPersistence(
        DurablePathFixture fixture,
        DbCommandInterceptor? commandInterceptor) : IRealtimeConversationPersistence
    {
        public Task<ConversationStatusProjection> CreateAsync(CreateConversation command, CancellationToken cancellationToken) =>
            ExecuteAsync(service => service.CreateAsync(command, cancellationToken));
        public Task<ConversationStatusProjection> ActivateAsync(ChangeConversationState command, CancellationToken cancellationToken) =>
            ExecuteAsync(service => service.ActivateAsync(command, cancellationToken));
        public Task<ConversationStatusProjection> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            ExecuteAsync(service => service.GetAsync(tenantId, conversationId, cancellationToken));
        public Task<IReadOnlyList<LiveTranscriptTurn>> GetTranscriptAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            ExecuteAsync(service => service.GetTranscriptAsync(tenantId, conversationId, cancellationToken));
        public Task<LiveTranscriptTurn> AddCallerTurnAsync(AddConversationTurn command, CancellationToken cancellationToken) =>
            ExecuteAsync(service => service.AddCallerTurnAsync(command, cancellationToken));
        public Task<LiveTranscriptTurn> AddAssistantTurnAsync(AddConversationTurn command, CancellationToken cancellationToken) =>
            ExecuteAsync(service => service.AddAssistantTurnAsync(command, cancellationToken));
        public Task<ConversationStatusProjection> ChangeLanguageAsync(ChangeConversationLanguage command, CancellationToken cancellationToken) =>
            ExecuteAsync(service => service.ChangeLanguageAsync(command, cancellationToken));
        public Task<CompletedConversationSummary> CompleteAsync(CompleteConversation command, CancellationToken cancellationToken) =>
            ExecuteAsync(service => service.CompleteAsync(command, cancellationToken));
        public Task<ConversationStatusProjection> FailAsync(ChangeConversationState command, CancellationToken cancellationToken) =>
            ExecuteAsync(service => service.FailAsync(command, cancellationToken));

        private async Task<T> ExecuteAsync<T>(Func<ConversationService, Task<T>> operation)
        {
            await using CallManagementDbContext calls = fixture.CreateCalls();
            await using ConversationDbContext conversations = commandInterceptor is null
                ? fixture.CreateConversations()
                : new ConversationDbContext(new DbContextOptionsBuilder<ConversationDbContext>()
                    .UseNpgsql(fixture.ConnectionString)
                    .AddInterceptors(commandInterceptor)
                    .Options);
            var callService = new CallManagementService(calls, TimeProvider.System);
            var conversationService = new ConversationService(conversations, callService, TimeProvider.System);
            return await operation(conversationService);
        }
    }

    private sealed class RecordingVoiceSessionDiagnostics : IVoiceSessionDiagnostics
    {
        private readonly object synchronization = new();
        private readonly List<VoiceLatencyDiagnostic> latencies = [];
        private readonly List<VoiceSessionExceptionDiagnostic> exceptions = [];
        private readonly List<VoiceLanguageDiagnostic> languages = [];
        private readonly List<VoiceSpeechRecognitionDiagnostic> speechRecognitions = [];

        public IReadOnlyList<VoiceLatencyDiagnostic> Latencies
        {
            get { lock (synchronization) return latencies.ToArray(); }
        }

        public IReadOnlyList<VoiceSessionExceptionDiagnostic> Exceptions
        {
            get { lock (synchronization) return exceptions.ToArray(); }
        }

        public IReadOnlyList<VoiceLanguageDiagnostic> Languages
        {
            get { lock (synchronization) return languages.ToArray(); }
        }

        public IReadOnlyList<VoiceSpeechRecognitionDiagnostic> SpeechRecognitions
        {
            get { lock (synchronization) return speechRecognitions.ToArray(); }
        }

        public void RecordException(VoiceSessionExceptionDiagnostic diagnostic)
        {
            lock (synchronization) exceptions.Add(diagnostic);
        }

        public void RecordLatency(VoiceLatencyDiagnostic diagnostic)
        {
            lock (synchronization) latencies.Add(diagnostic);
        }

        public void RecordLanguage(VoiceLanguageDiagnostic diagnostic)
        {
            lock (synchronization) languages.Add(diagnostic);
        }

        public void RecordSpeechRecognition(VoiceSpeechRecognitionDiagnostic diagnostic)
        {
            lock (synchronization) speechRecognitions.Add(diagnostic);
        }
    }

    private sealed class ControlledSpeechRecognizer : ISpeechRecognizer
    {
        private readonly InvocationLog<SpeechRecognitionRequest> invocations = new();
        private readonly int? blockOnInvocation;
        private readonly int? failureOnInvocation;
        private readonly RuntimeFailure failure;
        private readonly Func<SpeechRecognitionRequest, int, string>? response;
        private readonly Func<SpeechRecognitionRequest, int, SpeechRecognitionResult>? resultResponse;
        private readonly int? ignoreCancellationOnInvocation;
        private readonly TaskCompletionSource ignoredCancellationRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int cancellationObserved;

        public ControlledSpeechRecognizer(
            int? blockOnInvocation = null,
            int? failureOnInvocation = null,
            RuntimeFailure? failure = null,
            Func<SpeechRecognitionRequest, int, string>? response = null,
            Func<SpeechRecognitionRequest, int, SpeechRecognitionResult>? resultResponse = null,
            int? ignoreCancellationOnInvocation = null)
        {
            this.blockOnInvocation = blockOnInvocation;
            this.failureOnInvocation = failureOnInvocation;
            this.failure = failure ?? new RuntimeFailure(
                "speech_recognition_failed", "Speech recognition is unavailable.", false);
            this.response = response;
            this.resultResponse = resultResponse;
            this.ignoreCancellationOnInvocation = ignoreCancellationOnInvocation;
        }

        public string AdapterKey => "controlled-speech";
        public IReadOnlyList<SpeechRecognitionRequest> Requests => invocations.Snapshot;
        public bool CancellationObserved => Volatile.Read(ref cancellationObserved) == 1;
        public Task WaitForInvocationsAsync(int count) => invocations.WaitForCountAsync(count);
        public void ReleaseIgnoredCancellation() => ignoredCancellationRelease.TrySetResult();

        public async Task<SpeechRecognitionResult> RecognizeAsync(
            SpeechRecognitionRequest request,
            CancellationToken cancellationToken)
        {
            int invocation = invocations.Add(request);
            if (ignoreCancellationOnInvocation == invocation)
                await ignoredCancellationRelease.Task;
            if (blockOnInvocation == invocation)
                await BlockUntilCanceledAsync(cancellationToken);
            if (failureOnInvocation == invocation)
                return new SpeechRecognitionResult(
                    string.Empty, null, request.Language, null, null, false, failure);
            if (resultResponse is not null)
                return resultResponse(request, invocation);
            string text = response?.Invoke(request, invocation) ?? (request.AudioInput is { } input
                ? Encoding.UTF8.GetString(input.Audio.Span)
                : request.Input.Text);
            return new SpeechRecognitionResult(text, 0.99m, request.Language, null, null, true);
        }

        private async Task BlockUntilCanceledAsync(CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref cancellationObserved, 1);
                throw;
            }
        }
    }

    private sealed class ControlledAiRuntime : IAiConversationRuntime
    {
        private readonly InvocationLog<AiResponseRequest> invocations = new();
        private readonly Func<AiResponseRequest, int, string> response;
        private readonly int? blockOnInvocation;
        private readonly int? failureOnInvocation;
        private readonly RuntimeFailure failure;
        private int cancellationObserved;

        public ControlledAiRuntime(
            Func<AiResponseRequest, int, string>? response = null,
            int? blockOnInvocation = null,
            int? failureOnInvocation = null,
            RuntimeFailure? failure = null)
        {
            this.response = response ?? ((_, _) => "Hello from the assistant.");
            this.blockOnInvocation = blockOnInvocation;
            this.failureOnInvocation = failureOnInvocation;
            this.failure = failure ?? new RuntimeFailure(
                "ai_generation_failed", "The assistant is temporarily unavailable.", false);
        }

        public string AdapterKey => "controlled-ai";
        public IReadOnlyList<AiResponseRequest> Requests => invocations.Snapshot;
        public bool CancellationObserved => Volatile.Read(ref cancellationObserved) == 1;
        public Task WaitForInvocationsAsync(int count) => invocations.WaitForCountAsync(count);

        public async Task<AiResponseResult> GenerateAsync(
            AiResponseRequest request,
            CancellationToken cancellationToken)
        {
            int invocation = invocations.Add(request);
            if (blockOnInvocation == invocation)
                await BlockUntilCanceledAsync(cancellationToken);
            if (failureOnInvocation == invocation)
                return new AiResponseResult(
                    string.Empty, null, false, null, false,
                    new AiUsageMetadata(0, 0), request.Configuration.Version, failure);
            return new AiResponseResult(
                response(request, invocation), "integration-test", false, null, false,
                new AiUsageMetadata(1, 1), request.Configuration.Version);
        }

        private async Task BlockUntilCanceledAsync(CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref cancellationObserved, 1);
                throw;
            }
        }
    }

    private sealed class ControlledSpeechSynthesizer : ISpeechSynthesizer
    {
        private readonly InvocationLog<SpeechSynthesisRequest> invocations = new();
        private readonly int? blockOnInvocation;
        private readonly int? failureOnInvocation;
        private readonly RuntimeFailure failure;
        private int cancellationObserved;

        public ControlledSpeechSynthesizer(
            int? blockOnInvocation = null,
            int? failureOnInvocation = null,
            RuntimeFailure? failure = null)
        {
            this.blockOnInvocation = blockOnInvocation;
            this.failureOnInvocation = failureOnInvocation;
            this.failure = failure ?? new RuntimeFailure(
                "speech_synthesis_failed", "Speech synthesis is unavailable.", false);
        }

        public string AdapterKey => "controlled-speech";
        public IReadOnlyList<SpeechSynthesisRequest> Requests => invocations.Snapshot;
        public bool CancellationObserved => Volatile.Read(ref cancellationObserved) == 1;
        public Task WaitForInvocationsAsync(int count) => invocations.WaitForCountAsync(count);

        public async Task<SpeechSynthesisResult> SynthesizeAsync(
            SpeechSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            int invocation = invocations.Add(request);
            if (blockOnInvocation == invocation)
                await BlockUntilCanceledAsync(cancellationToken);
            if (failureOnInvocation == invocation)
                return new SpeechSynthesisResult(
                    string.Empty, "application/octet-stream", null, request.Voice.VoiceId,
                    new Dictionary<string, string>(), failure);
            byte[] audio = Encoding.UTF8.GetBytes(request.Text);
            return new SpeechSynthesisResult(
                $"fake://speech/{invocation}",
                AudioFormat.SyntheticText.Encoding,
                TimeSpan.FromMilliseconds(20),
                request.Voice.VoiceId,
                new Dictionary<string, string> { ["adapter"] = AdapterKey },
                AudioChunks:
                [
                    new SynthesizedAudioChunk(
                        1, AudioFormat.SyntheticText, audio, IsFinal: true),
                ]);
        }

        private async Task BlockUntilCanceledAsync(CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref cancellationObserved, 1);
                throw;
            }
        }
    }

    private sealed class InvocationLog<T>
    {
        private readonly object synchronization = new();
        private readonly List<T> requests = [];
        private TaskCompletionSource invoked = NewSignal();

        public IReadOnlyList<T> Snapshot
        {
            get { lock (synchronization) return requests.ToArray(); }
        }

        public int Add(T request)
        {
            int count;
            TaskCompletionSource signal;
            lock (synchronization)
            {
                requests.Add(request);
                count = requests.Count;
                signal = invoked;
                invoked = NewSignal();
            }
            signal.TrySetResult();
            return count;
        }

        public async Task WaitForCountAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            while (true)
            {
                Task signal;
                lock (synchronization)
                {
                    if (requests.Count >= expected) return;
                    signal = invoked.Task;
                }

                try { await signal.WaitAsync(timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException($"Provider invocation {expected} was not observed.");
                }
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
