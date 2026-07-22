using PurpleGlass.Adapters.AI.Mock;
using PurpleGlass.Adapters.Speech.Mock;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.UnitTests;

public sealed class AiRuntimeTests
{
    private static readonly RuntimeInvocationContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "trace-test");

    [Fact]
    public async Task SpeechRecognitionReturnsDeterministicTextAndConfidence()
    {
        var adapter = new MockSpeechRecognizer(new MockSpeechOptions { RecognitionConfidence = 0.91m }, TimeProvider.System);

        SpeechRecognitionResult result = await adapter.RecognizeAsync(
            new SpeechRecognitionRequest(Context, "en-US", new SimulatedUtteranceInput("  hello office  ")), default);

        Assert.Equal("hello office", result.RecognizedText);
        Assert.Equal(0.91m, result.Confidence);
        Assert.True(result.IsFinal);
    }

    [Fact]
    public async Task SpeechRecognitionHonorsCancellation()
    {
        var adapter = new MockSpeechRecognizer(
            new MockSpeechOptions { RecognitionDelay = TimeSpan.FromSeconds(10) }, TimeProvider.System);
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.RecognizeAsync(
            new SpeechRecognitionRequest(Context, "en-US", new SimulatedUtteranceInput("hello")), source.Token));
    }

    [Theory]
    [InlineData("What are your hours?", "office-hours", "Monday through Friday")]
    [InlineData("Where is your location?", "office-location", "100 Prototype Avenue")]
    [InlineData("My name is Alex Example", "caller-name", "reason for your call")]
    public async Task AiHandlesApprovedOfficeScenarios(string callerText, string intent, string expectedText)
    {
        AiResponseResult result = await CreateAi().GenerateAsync(Request(callerText), default);

        Assert.Equal(intent, result.Intent);
        Assert.Contains(expectedText, result.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task AiRequestsHumanEscalation()
    {
        AiResponseResult result = await CreateAi().GenerateAsync(Request("I need a human representative"), default);

        Assert.True(result.EscalationRequested);
        Assert.Equal("human_requested", result.EscalationReason);
        Assert.True(result.ShouldEndConversation);
    }

    [Fact]
    public async Task AiUsesUrgentSafetyPathWithoutDiagnosis()
    {
        AiResponseResult result = await CreateAi().GenerateAsync(Request("There is uncontrolled bleeding"), default);

        Assert.True(result.EscalationRequested);
        Assert.Equal("urgent-safety", result.Intent);
        Assert.Contains("cannot diagnose", result.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("you have", result.AssistantText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AiRefusesMedicalDiagnosis()
    {
        AiResponseResult result = await CreateAi().GenerateAsync(Request("My tooth hurts, diagnose it"), default);

        Assert.Equal("medical-question", result.Intent);
        Assert.Contains("cannot diagnose", result.AssistantText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpeechSynthesisReturnsOpaqueResultAndSupportsReplaceableVoice()
    {
        var adapter = new MockSpeechSynthesizer(new MockSpeechOptions(), TimeProvider.System);

        SpeechSynthesisResult calm = await adapter.SynthesizeAsync(
            new SpeechSynthesisRequest(Context, "Hello", "en-US", new VoiceConfiguration("calm-a")), default);
        SpeechSynthesisResult bright = await adapter.SynthesizeAsync(
            new SpeechSynthesisRequest(Context, "Hello", "en-US", new VoiceConfiguration("bright-b")), default);

        Assert.StartsWith("memory://mock-speech/", calm.AudioReference, StringComparison.Ordinal);
        Assert.Equal("audio/x-purpleglass-simulator", calm.ContentType);
        Assert.Equal("bright-b", bright.VoiceId);
        Assert.NotEqual(calm.AudioReference, bright.AudioReference);
    }

    private static MockAiConversationRuntime CreateAi() => new(new MockAiOptions(), TimeProvider.System);

    private static AiResponseRequest Request(string callerText) => new(
        Context,
        Configuration(),
        [],
        callerText,
        [],
        new SafetyEscalationPolicy("safety-v1", ["human", "representative"], ["uncontrolled bleeding"]));

    internal static ConversationRuntimeConfiguration Configuration(int maximumTurns = 6, string voice = "calm-a") =>
        new ConversationRuntimeConfiguration
        {
            Version = "test-v1",
            Language = "en-US",
            VoiceId = voice,
            Greeting = "Thank you for calling Test Dental.",
            OfficeName = "Test Dental",
            OfficeHours = "Monday through Friday, 8 AM to 5 PM",
            OfficeLocation = "100 Prototype Avenue",
            SafetyPolicyVersion = "safety-v1",
            MaximumTurns = maximumTurns,
            InactivityTimeout = TimeSpan.FromSeconds(30),
            AiAdapterKey = "mock-ai",
            SpeechRecognitionAdapterKey = "mock-speech",
            SpeechSynthesisAdapterKey = "mock-speech",
            EscalationKeywords = ["human", "representative"],
            UrgentKeywords = ["uncontrolled bleeding"],
        };
}
