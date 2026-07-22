using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PurpleGlass.Adapters.AI.Mock;
using PurpleGlass.Adapters.Speech.Mock;
using PurpleGlass.CallOrchestrator.Worker;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Infrastructure;

namespace PurpleGlass.IntegrationTests;

[Collection(SimulatedCallFlowGroup.Name)]
public sealed class SimulatedCallFlowTests(SimulatedCallFlowFixture fixture)
{
    [Fact]
    public async Task CompleteInboundFlowPersistsTranscriptSummaryOutboxAndReplayIdentity()
    {
        await using Harness harness = CreateHarness();
        Guid input = Guid.NewGuid();
        SimulatedCallRequest request = Request(SimulatedCallDirection.Inbound,
            new SimulatedCallerInput(input, "What are your office hours?"),
            new SimulatedCallerInput(Guid.NewGuid(), "No thanks"));

        SimulatedCallResult result = await harness.Service.RunAsync(request, default);
        var replay = await harness.CallsService.RegisterInboundAsync(new RegisterInboundCall(
            request.TenantId, request.LocationId, request.StartKey, request.FromNumber, request.ToNumber,
            request.CorrelationId, request.CausationId, request.TraceId), default);

        Assert.Equal("Completed", result.CallState);
        Assert.Equal("Completed", result.ConversationState);
        Assert.Equal(result.CallId, replay.CallId);
        Assert.Contains(result.Transcript, turn => turn.Speaker == "Caller");
        Assert.Contains(result.Transcript, turn => turn.Speaker == "Assistant");
        Assert.NotNull(result.Summary);
        Assert.Contains("office-hours", result.Summary.CallerIntent, StringComparison.Ordinal);
        Assert.Single((await harness.Calls.Calls.AsNoTracking().ToListAsync()), call => call.Id.Value == result.CallId);
        await using var eventing = fixture.CreateEventing();
        Assert.True(await eventing.OutboxMessages.CountAsync(message => message.CorrelationId == request.CorrelationId) >= 10);
        Assert.True(await eventing.OutboxMessages.AnyAsync(message => message.CausationId == input && message.TraceId == request.TraceId));
    }

    [Fact]
    public async Task CompleteOutboundFlowPersistsAndReusesIdempotencyKey()
    {
        await using Harness harness = CreateHarness();
        SimulatedCallRequest request = Request(SimulatedCallDirection.Outbound,
            new SimulatedCallerInput(Guid.NewGuid(), "My name is Morgan Example"),
            new SimulatedCallerInput(Guid.NewGuid(), "No thanks"));

        SimulatedCallResult result = await harness.Service.RunAsync(request, default);
        var replay = await harness.CallsService.RequestOutboundAsync(new RequestOutboundCall(
            request.TenantId, request.LocationId, request.StartKey, request.FromNumber, request.ToNumber,
            request.CorrelationId, request.CausationId, request.TraceId), default);

        Assert.Equal("Completed", result.CallState);
        Assert.Contains("Call:Ringing", result.StateTransitions);
        Assert.Equal(result.CallId, replay.CallId);
        Assert.Single((await harness.Calls.Calls.AsNoTracking().ToListAsync()), call => call.Id.Value == result.CallId);
    }

    [Theory]
    [InlineData("I need a human representative", "escalated")]
    [InlineData("There is uncontrolled bleeding", "urgent_escalation")]
    public async Task EscalationAndUrgentPathsCompleteSafely(string callerText, string expectedOutcome)
    {
        await using Harness harness = CreateHarness();

        SimulatedCallResult result = await harness.Service.RunAsync(Request(
            SimulatedCallDirection.Inbound, new SimulatedCallerInput(Guid.NewGuid(), callerText)), default);

        Assert.True(result.Escalated);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal("Completed", result.CallState);
        Assert.Equal("Completed", result.ConversationState);
        Assert.True(result.Summary?.Escalated);
    }

    [Fact]
    public async Task SpeechRecognitionFailureFailsBothDurableAggregates()
    {
        await using Harness harness = CreateHarness(speech: new MockSpeechOptions { FailRecognition = true });

        SimulatedCallResult result = await harness.Service.RunAsync(Request(
            SimulatedCallDirection.Inbound, new SimulatedCallerInput(Guid.NewGuid(), "Hello")), default);

        Assert.Equal("speech_recognition_failed", result.FailureCode);
        Assert.Equal("Failed", result.CallState);
        Assert.Equal("Failed", result.ConversationState);
    }

    [Fact]
    public async Task AiGenerationFailureFailsBothDurableAggregates()
    {
        await using Harness harness = CreateHarness(ai: new MockAiOptions { FailGeneration = true });

        SimulatedCallResult result = await harness.Service.RunAsync(Request(
            SimulatedCallDirection.Inbound, new SimulatedCallerInput(Guid.NewGuid(), "Hello")), default);

        Assert.Equal("ai_generation_failed", result.FailureCode);
        Assert.Equal("Failed", result.CallState);
        Assert.Equal("Failed", result.ConversationState);
    }

    [Fact]
    public async Task SpeechSynthesisFailureFailsBothDurableAggregates()
    {
        await using Harness harness = CreateHarness(speech: new MockSpeechOptions { FailSynthesis = true });

        SimulatedCallResult result = await harness.Service.RunAsync(Request(
            SimulatedCallDirection.Inbound, new SimulatedCallerInput(Guid.NewGuid(), "Hello")), default);

        Assert.Equal("speech_synthesis_failed", result.FailureCode);
        Assert.Equal("Failed", result.CallState);
        Assert.Equal("Failed", result.ConversationState);
    }

    [Fact]
    public async Task CancellationDuringConversationPersistsFailedFinalStates()
    {
        await using Harness harness = CreateHarness(speech: new MockSpeechOptions { RecognitionDelay = TimeSpan.FromSeconds(10) });
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        SimulatedCallRequest request = Request(
            SimulatedCallDirection.Inbound, new SimulatedCallerInput(Guid.NewGuid(), "Hello"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Service.RunAsync(request, source.Token));

        Assert.Equal("Failed", (await harness.CallsService.GetByProviderCallIdAsync(request.TenantId, request.StartKey, default)).State);
        Assert.Equal("Failed", (await harness.ConversationsService.GetForCallAsync(
            request.TenantId,
            (await harness.CallsService.GetByProviderCallIdAsync(request.TenantId, request.StartKey, default)).CallId,
            default)).State);
    }

    [Fact]
    public async Task AlternateAiImplementationIsSubstitutableWithoutOrchestratorChanges()
    {
        await using Harness harness = CreateHarness(runtime: new AlternateAiRuntime());

        SimulatedCallResult result = await harness.Service.RunAsync(Request(
            SimulatedCallDirection.Inbound, new SimulatedCallerInput(Guid.NewGuid(), "Hello")), default);

        Assert.Equal("Completed", result.CallState);
        Assert.Contains(result.Transcript, turn => turn.Text == "Alternate adapter response.");
    }

    private Harness CreateHarness(
        MockSpeechOptions? speech = null,
        MockAiOptions? ai = null,
        IAiConversationRuntime? runtime = null)
    {
        CallManagementDbContext callsDb = fixture.CreateCalls();
        ConversationDbContext conversationsDb = fixture.CreateConversations();
        var callService = new CallManagementService(callsDb, TimeProvider.System);
        var conversationService = new ConversationService(conversationsDb, callService, TimeProvider.System);
        MockSpeechOptions speechOptions = speech ?? new();
        var options = new CallOrchestratorOptions
        {
            Conversation = Configuration(),
            MockAi = ai ?? new(),
            MockSpeech = speechOptions,
            RecognitionTimeout = TimeSpan.FromSeconds(1),
            AiTimeout = TimeSpan.FromSeconds(1),
            SynthesisTimeout = TimeSpan.FromSeconds(1),
            CleanupTimeout = TimeSpan.FromSeconds(2),
            MaximumAdapterAttempts = 1,
        }.Validate();
        var service = new CallOrchestrationService(
            callService,
            conversationService,
            new MockSpeechRecognizer(speechOptions, TimeProvider.System),
            runtime ?? new MockAiConversationRuntime(options.MockAi, TimeProvider.System),
            new MockSpeechSynthesizer(speechOptions, TimeProvider.System),
            options,
            TimeProvider.System,
            NullLogger<CallOrchestrationService>.Instance);
        return new(service, callsDb, conversationsDb, callService, conversationService);
    }

    private static SimulatedCallRequest Request(
        SimulatedCallDirection direction,
        params SimulatedCallerInput[] inputs) => new(
            Guid.NewGuid(), Guid.NewGuid(), direction, $"task4-{Guid.NewGuid():N}",
            "+15550100001", "+15550100002", inputs, Guid.NewGuid(), Guid.NewGuid(), $"trace-{Guid.NewGuid():N}");

    private static ConversationRuntimeConfiguration Configuration() => new()
    {
        Version = "integration-v1",
        Language = "en-US",
        VoiceId = "calm-a",
        Greeting = "Thank you for calling Integration Dental.",
        OfficeName = "Integration Dental",
        OfficeHours = "Monday through Friday, 8 AM to 5 PM",
        OfficeLocation = "100 Prototype Avenue",
        SafetyPolicyVersion = "safety-v1",
        MaximumTurns = 6,
        InactivityTimeout = TimeSpan.FromSeconds(30),
        AiAdapterKey = "mock-ai",
        SpeechRecognitionAdapterKey = "mock-speech",
        SpeechSynthesisAdapterKey = "mock-speech",
        EscalationKeywords = ["human", "representative"],
        UrgentKeywords = ["uncontrolled bleeding"],
    };

    private sealed record Harness(
        CallOrchestrationService Service,
        CallManagementDbContext Calls,
        ConversationDbContext Conversations,
        CallManagementService CallsService,
        ConversationService ConversationsService) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Conversations.DisposeAsync();
            await Calls.DisposeAsync();
        }
    }

    private sealed class AlternateAiRuntime : IAiConversationRuntime
    {
        public string AdapterKey => "mock-ai";
        public Task<AiResponseResult> GenerateAsync(AiResponseRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AiResponseResult(
                "Alternate adapter response.", "alternate", false, null, true,
                new AiUsageMetadata(1, 1), request.Configuration.Version));
    }
}

public sealed class SimulatedCallFlowFixture : DurablePathFixture;

[CollectionDefinition(Name)]
public sealed class SimulatedCallFlowGroup : ICollectionFixture<SimulatedCallFlowFixture>
{
    public const string Name = "simulated-call-flow";
}
