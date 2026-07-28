using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.AI.Mock;

public sealed class MockAiConversationRuntime(MockAiOptions options, TimeProvider timeProvider) : IAiConversationRuntime
{
    private const string MedicalSafetyResponse =
        "I can't provide medical advice. If this may be an emergency, contact local emergency services.";
    private const string GenericTestResponse =
        "Thanks for the test message. This development assistant can continue a general voice conversation.";

    public string AdapterKey => "deterministic";

    public async Task<AiResponseResult> GenerateAsync(AiResponseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (options.Delay > TimeSpan.Zero) await Task.Delay(options.Delay, timeProvider, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.FailGeneration)
            return Result(request, string.Empty, null, false, null, false,
                new RuntimeFailure("ai_generation_failed", "The assistant could not generate a response.", true));

        string caller = request.CurrentCallerTurn.Trim();
        string normalized = caller.ToLowerInvariant();
        if (ContainsAny(normalized, request.SafetyPolicy.UrgentKeywords))
            return Result(request, MedicalSafetyResponse, "urgent-safety", true, "urgent_call", true);
        if (LooksMedical(normalized))
            return Result(request, MedicalSafetyResponse, "medical-question", true, "clinical_question", true);
        if (ContainsAny(normalized, request.SafetyPolicy.EscalationKeywords)
            || normalized.Contains("human", StringComparison.Ordinal))
            return Result(request, "Human transfer is not available in this development assistant.", "human-unavailable", false, null, false);
        if (normalized is "no" or "no thanks" || normalized.Contains("goodbye", StringComparison.Ordinal))
            return Result(request, "Thank you for testing PurpleGlass. Goodbye.", "conversation-end", false, null, true);

        return Result(request, GenericTestResponse, "general-test", false, null, false);
    }

    private static bool ContainsAny(string input, IEnumerable<string> candidates) =>
        candidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate)
            && input.Contains(candidate.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool LooksMedical(string input) =>
        input.Contains("diagnose", StringComparison.Ordinal)
        || input.Contains("what medicine", StringComparison.Ordinal)
        || input.Contains("tooth hurts", StringComparison.Ordinal)
        || input.Contains("infection", StringComparison.Ordinal);

    private static AiResponseResult Result(
        AiResponseRequest request,
        string text,
        string? intent,
        bool escalation,
        string? reason,
        bool end,
        RuntimeFailure? failure = null) =>
        new(text, intent, escalation, reason, end,
            new AiUsageMetadata(request.CurrentCallerTurn.Length, text.Length),
            request.Configuration.Version, failure);
}
