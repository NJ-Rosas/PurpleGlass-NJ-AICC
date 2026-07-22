using PurpleGlass.Adapters.AI.Mock;
using PurpleGlass.Adapters.Speech.Mock;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.CallOrchestrator.Worker;

public sealed record CallOrchestratorOptions
{
    public const string SectionName = "CallOrchestrator";

    public required ConversationRuntimeConfiguration Conversation { get; init; }
    public MockAiOptions MockAi { get; init; } = new();
    public MockSpeechOptions MockSpeech { get; init; } = new();
    public TimeSpan RecognitionTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan AiTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SynthesisTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public int MaximumAdapterAttempts { get; init; } = 2;

    public CallOrchestratorOptions Validate()
    {
        Conversation.Validate();
        ValidateTimeout(RecognitionTimeout, nameof(RecognitionTimeout));
        ValidateTimeout(AiTimeout, nameof(AiTimeout));
        ValidateTimeout(SynthesisTimeout, nameof(SynthesisTimeout));
        ValidateTimeout(CleanupTimeout, nameof(CleanupTimeout));
        if (MaximumAdapterAttempts is < 1 or > 3)
            throw new InvalidOperationException("MaximumAdapterAttempts must be between 1 and 3.");
        if (!string.Equals(Conversation.AiAdapterKey, "mock-ai", StringComparison.Ordinal))
            throw new InvalidOperationException("Task 4 supports the mock-ai adapter key only.");
        if (!string.Equals(Conversation.SpeechRecognitionAdapterKey, "mock-speech", StringComparison.Ordinal)
            || !string.Equals(Conversation.SpeechSynthesisAdapterKey, "mock-speech", StringComparison.Ordinal))
            throw new InvalidOperationException("Task 4 supports the mock-speech adapter key only.");
        return this;
    }

    private static void ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException($"{name} must be between zero and two minutes.");
    }
}
