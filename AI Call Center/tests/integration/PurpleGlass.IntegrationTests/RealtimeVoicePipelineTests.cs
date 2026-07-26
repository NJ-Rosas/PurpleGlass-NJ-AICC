using System.Text;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PurpleGlass.Adapters.Audio.Fake;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Contracts;
using PurpleGlass.Modules.Conversation.Infrastructure;

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

    [Fact]
    public async Task TwoTurnsPreservePriorCallerAndAssistantHistory()
    {
        var languageModel = new ControlledAiRuntime((_, invocation) =>
            invocation == 1 ? "First answer." : "Second answer.");
        await using SessionHarness harness = await CreateHarnessAsync(languageModel: languageModel);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("First question");
        await harness.Transport.QueueUtteranceAsync("Second question");
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 3);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Equal(2, languageModel.Requests.Count);
        AiResponseRequest second = languageModel.Requests[1];
        Assert.Equal("Second question", second.CurrentCallerTurn);
        Assert.Contains(second.ExistingTurns,
            turn => turn.Speaker == "Caller" && turn.Text == "First question");
        Assert.Contains(second.ExistingTurns,
            turn => turn.Speaker == "Assistant" && turn.Text == "First answer.");
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(2, details.Transcript.Count(turn => turn.Speaker == "Caller"));
        Assert.Equal(3, details.Transcript.Count(turn => turn.Speaker == "Assistant"));
        Assert.Equal(
            [Greeting, "First question", "First answer.", "Second question", "Second answer."],
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Text).ToArray());
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
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
        var recognizer = new ControlledSpeechRecognizer(
            failureOnInvocation: 1,
            failure: new RuntimeFailure("speech_recognition_failed", "Speech recognition is unavailable.", false));
        await using SessionHarness harness = await CreateHarnessAsync(recognizer: recognizer);
        Task run = harness.Start();
        _ = await harness.States.WaitForAsync(change => change.State == VoiceSessionState.Listening);

        await harness.Transport.QueueUtteranceAsync("Unrecognized audio");
        VoiceSessionStateChange failed = await harness.States.WaitForAsync(
            change => change.State == VoiceSessionState.Failed);
        _ = await harness.States.WaitForOccurrencesAsync(VoiceSessionState.Listening, 2);
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Equal("speech_recognition_failed", failed.SafeCode);
        Assert.Empty(harness.LanguageModel.Requests);
        Assert.Equal(FallbackResponse, harness.Synthesizer.Requests[1].Text);
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Single(details.Transcript);
        Assert.Equal("Assistant", details.Transcript[0].Speaker);
        await AssertCompletedAndDisposedAsync(harness, "media_disconnected");
    }

    [Fact]
    public async Task LanguageModelFailureProducesSafeResponseAndFailureState()
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
        harness.Transport.CompleteInput();
        await run.WaitAsync(TestTimeout);

        Assert.Equal("ai_generation_failed", failed.SafeCode);
        Assert.Equal(FallbackResponse, harness.Synthesizer.Requests[1].Text);
        Assert.Contains(FallbackResponse, DecodeOutput(harness.Transport));
        ConversationDetails details = await ReadDetailsAsync(harness.TenantId, harness.CallId);
        Assert.Equal(GreetingCallerSpeakers,
            details.Transcript.OrderBy(turn => turn.SequenceNumber).Select(turn => turn.Speaker).ToArray());
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
        DbCommandInterceptor? commandInterceptor = null)
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
                Provider: "Fake"), default);
            VoiceCallContext call = await callService.ConnectVoiceMediaAsync("Fake", providerCallId, default);
            var conversationService = new ConversationService(conversations, callService, TimeProvider.System);
            var transport = new FakeRealtimeAudioTransport(providerCallId, $"FM-{Guid.NewGuid():N}");
            var states = new RecordingVoiceSessionStateSink();
            recognizer ??= new ControlledSpeechRecognizer();
            languageModel ??= new ControlledAiRuntime();
            synthesizer ??= new ControlledSpeechSynthesizer();
            var persistence = new FixtureRealtimeConversationPersistence(fixture, commandInterceptor);
            var session = new RealtimeVoiceSession(
                persistence,
                recognizer,
                languageModel,
                synthesizer,
                Options(),
                states,
                TimeProvider.System,
                new RecordingVoiceSessionDiagnostics());
            var identity = new VoiceSessionIdentity(
                call.TenantId,
                call.LocationId,
                call.CallId,
                call.Direction,
                call.Provider,
                call.ProviderCallId,
                transport.ProviderMediaStreamId,
                call.CorrelationId);
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
                synthesizer);
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
            ControlledSpeechSynthesizer synthesizer)
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
        public void RecordException(VoiceSessionExceptionDiagnostic diagnostic) { }
    }

    private sealed class ControlledSpeechRecognizer : ISpeechRecognizer
    {
        private readonly InvocationLog<SpeechRecognitionRequest> invocations = new();
        private readonly int? blockOnInvocation;
        private readonly int? failureOnInvocation;
        private readonly RuntimeFailure failure;
        private int cancellationObserved;

        public ControlledSpeechRecognizer(
            int? blockOnInvocation = null,
            int? failureOnInvocation = null,
            RuntimeFailure? failure = null)
        {
            this.blockOnInvocation = blockOnInvocation;
            this.failureOnInvocation = failureOnInvocation;
            this.failure = failure ?? new RuntimeFailure(
                "speech_recognition_failed", "Speech recognition is unavailable.", false);
        }

        public string AdapterKey => "controlled-speech";
        public IReadOnlyList<SpeechRecognitionRequest> Requests => invocations.Snapshot;
        public bool CancellationObserved => Volatile.Read(ref cancellationObserved) == 1;
        public Task WaitForInvocationsAsync(int count) => invocations.WaitForCountAsync(count);

        public async Task<SpeechRecognitionResult> RecognizeAsync(
            SpeechRecognitionRequest request,
            CancellationToken cancellationToken)
        {
            int invocation = invocations.Add(request);
            if (blockOnInvocation == invocation)
                await BlockUntilCanceledAsync(cancellationToken);
            if (failureOnInvocation == invocation)
                return new SpeechRecognitionResult(
                    string.Empty, null, request.Language, null, null, false, failure);
            string text = request.AudioInput is { } input
                ? Encoding.UTF8.GetString(input.Audio.Span)
                : request.Input.Text;
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
