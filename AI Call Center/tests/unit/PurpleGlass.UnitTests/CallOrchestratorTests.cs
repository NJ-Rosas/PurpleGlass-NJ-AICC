using Microsoft.Extensions.Logging.Abstractions;
using PurpleGlass.Adapters.AI.Mock;
using PurpleGlass.Adapters.Speech.Mock;
using PurpleGlass.CallOrchestrator.Worker;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Domain;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Domain;
using ConversationAggregate = PurpleGlass.Modules.Conversation.Domain.Conversation;

namespace PurpleGlass.UnitTests;

public sealed class CallOrchestratorTests
{
    [Fact]
    public async Task OrchestratorExecutesLifecycleAndPersistsBothSpeakers()
    {
        Harness harness = CreateHarness();

        SimulatedCallResult result = await harness.Service.RunAsync(Request(
            new SimulatedCallerInput(Guid.NewGuid(), "What are your office hours?"),
            new SimulatedCallerInput(Guid.NewGuid(), "No thanks")), default);

        Assert.Equal("Completed", result.CallState);
        Assert.Equal("Completed", result.ConversationState);
        Assert.Contains("Call:InConversation", result.StateTransitions);
        Assert.Contains(result.Transcript, turn => turn.Speaker == "Caller");
        Assert.Contains(result.Transcript, turn => turn.Speaker == "Assistant");
        Assert.NotNull(result.Summary);
    }

    [Fact]
    public async Task DuplicateCallerInputIsHandledIdempotently()
    {
        Harness harness = CreateHarness();
        Guid inputId = Guid.NewGuid();

        SimulatedCallResult result = await harness.Service.RunAsync(Request(
            new SimulatedCallerInput(inputId, "What are your office hours?"),
            new SimulatedCallerInput(inputId, "What are your office hours?")), default);

        Assert.Single(result.Transcript, turn => turn.TurnId == inputId);
        Assert.Equal(3, result.Transcript.Count);
    }

    [Fact]
    public async Task MaximumTurnLimitCompletesSafely()
    {
        Harness harness = CreateHarness(maximumTurns: 1);

        SimulatedCallResult result = await harness.Service.RunAsync(Request(
            new SimulatedCallerInput(Guid.NewGuid(), "What are your office hours?"),
            new SimulatedCallerInput(Guid.NewGuid(), "Where are you located?")), default);

        Assert.Equal("maximum_turns", result.Outcome);
        Assert.Equal("Completed", result.CallState);
        Assert.Single(result.Transcript, turn => turn.Speaker == "Caller");
    }

    [Fact]
    public async Task AdapterTimeoutUsesBoundedFailureFallback()
    {
        Harness harness = CreateHarness(
            recognitionTimeout: TimeSpan.FromMilliseconds(10),
            speech: new MockSpeechOptions { RecognitionDelay = TimeSpan.FromSeconds(1) });

        SimulatedCallResult result = await harness.Service.RunAsync(Request(
            new SimulatedCallerInput(Guid.NewGuid(), "What are your office hours?")), default);

        Assert.Equal("recognize_timeout", result.FailureCode);
        Assert.Equal("Failed", result.CallState);
        Assert.Equal("Failed", result.ConversationState);
    }

    [Fact]
    public async Task CancellationStopsProcessingAndCleansUpActiveState()
    {
        Harness harness = CreateHarness(speech: new MockSpeechOptions { RecognitionDelay = TimeSpan.FromSeconds(10) });
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Service.RunAsync(Request(
            new SimulatedCallerInput(Guid.NewGuid(), "What are your office hours?")), source.Token));

        Assert.Equal(CallState.Failed, Assert.Single(harness.Calls.Calls).State);
        Assert.Equal(ConversationState.Failed, Assert.Single(harness.Conversations.Conversations).State);
    }

    [Fact]
    public async Task AdapterFailureDoesNotLeaveActiveState()
    {
        Harness harness = CreateHarness(ai: new MockAiOptions { FailGeneration = true });

        SimulatedCallResult result = await harness.Service.RunAsync(Request(
            new SimulatedCallerInput(Guid.NewGuid(), "What are your office hours?")), default);

        Assert.Equal("ai_generation_failed", result.FailureCode);
        Assert.Equal("Failed", result.CallState);
        Assert.Equal("Failed", result.ConversationState);
    }

    private static Harness CreateHarness(
        int maximumTurns = 6,
        TimeSpan? recognitionTimeout = null,
        MockSpeechOptions? speech = null,
        MockAiOptions? ai = null)
    {
        var callStore = new MemoryCallStore();
        var conversationStore = new MemoryConversationStore();
        var callService = new CallManagementService(callStore, TimeProvider.System);
        var conversationService = new ConversationService(conversationStore, callService, TimeProvider.System);
        var options = new CallOrchestratorOptions
        {
            Conversation = AiRuntimeTests.Configuration(maximumTurns),
            MockAi = ai ?? new(),
            MockSpeech = speech ?? new(),
            RecognitionTimeout = recognitionTimeout ?? TimeSpan.FromSeconds(1),
            AiTimeout = TimeSpan.FromSeconds(1),
            SynthesisTimeout = TimeSpan.FromSeconds(1),
            CleanupTimeout = TimeSpan.FromSeconds(1),
            MaximumAdapterAttempts = 1,
        }.Validate();
        var recognizer = new MockSpeechRecognizer(options.MockSpeech, TimeProvider.System);
        var runtime = new MockAiConversationRuntime(options.MockAi, TimeProvider.System);
        var synthesizer = new MockSpeechSynthesizer(options.MockSpeech, TimeProvider.System);
        var service = new CallOrchestrationService(
            callService, conversationService, recognizer, runtime, synthesizer, options,
            TimeProvider.System, NullLogger<CallOrchestrationService>.Instance);
        return new(service, callStore, conversationStore);
    }

    private static SimulatedCallRequest Request(params SimulatedCallerInput[] inputs) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        SimulatedCallDirection.Inbound,
        $"provider-{Guid.NewGuid():N}",
        "+15550100001",
        "+15550100002",
        inputs,
        Guid.NewGuid(),
        Guid.NewGuid(),
        "trace-unit");

    private sealed record Harness(
        CallOrchestrationService Service,
        MemoryCallStore Calls,
        MemoryConversationStore Conversations);

    private sealed class MemoryCallStore : ICallStore
    {
        private readonly Dictionary<(Guid TenantId, string Key), Guid> outbound = [];
        public List<CallSession> Calls { get; } = [];
        public List<OutboxMessage> Outbox { get; } = [];
        public Task<CallSession?> GetAsync(Guid tenantId, Guid callId, bool tracking, CancellationToken cancellationToken) => Task.FromResult(Calls.SingleOrDefault(call => call.TenantId.Value == tenantId && call.Id.Value == callId));
        public Task<CallSession?> GetByProviderCallIdAsync(Guid tenantId, string providerCallId, bool tracking, CancellationToken cancellationToken) => Task.FromResult(Calls.SingleOrDefault(call => call.TenantId.Value == tenantId && call.ProviderCallId == providerCallId));
        public Task<CallSession?> GetByOutboundKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken) => Task.FromResult(outbound.TryGetValue((tenantId, idempotencyKey), out Guid id) ? Calls.Single(call => call.Id.Value == id) : null);
        public Task<IReadOnlyList<CallSession>> GetRecentAsync(Guid tenantId, Guid? locationId, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CallSession>>(Calls);
        public void Add(CallSession callSession) => Calls.Add(callSession);
        public void AddOutboundRequest(Guid tenantId, string idempotencyKey, Guid callId, DateTimeOffset createdAtUtc) => outbound[(tenantId, idempotencyKey)] = callId;
        public void AddOutbox(OutboxMessage message) => Outbox.Add(message);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MemoryConversationStore : IConversationStore
    {
        public List<ConversationAggregate> Conversations { get; } = [];
        public List<OutboxMessage> Outbox { get; } = [];
        public Task<ConversationAggregate?> GetAsync(Guid tenantId, Guid conversationId, bool tracking, CancellationToken cancellationToken) => Task.FromResult(Conversations.SingleOrDefault(conversation => conversation.TenantId.Value == tenantId && conversation.Id.Value == conversationId));
        public Task<ConversationAggregate?> GetForCallAsync(Guid tenantId, Guid callId, bool tracking, CancellationToken cancellationToken) => Task.FromResult(Conversations.SingleOrDefault(conversation => conversation.TenantId.Value == tenantId && conversation.CallSession.Value == callId));
        public void Add(ConversationAggregate conversation) => Conversations.Add(conversation);
        public void AddOutbox(OutboxMessage message) => Outbox.Add(message);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
