using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.AI.Mock;

public sealed class MockAiConversationRuntime(MockAiOptions options, TimeProvider timeProvider) : IAiConversationRuntime
{
    private const string MedicalSafetyResponse =
        "I cannot diagnose dental or medical conditions. If this may be an emergency, call emergency services; otherwise, I can ask the office staff to follow up.";

    public string AdapterKey => "mock-ai";

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
        if (ContainsAny(normalized, request.SafetyPolicy.EscalationKeywords) || normalized.Contains("human", StringComparison.Ordinal))
            return Result(request, "I’ll mark this for a staff member to assist you.", "human-request", true, "human_requested", true);
        if (normalized.Contains("hours", StringComparison.Ordinal) || normalized.Contains("open", StringComparison.Ordinal))
            return Result(request, $"{request.Configuration.OfficeName} is open {request.Configuration.OfficeHours}.", "office-hours", false, null, false);
        if (normalized.Contains("where", StringComparison.Ordinal) || normalized.Contains("location", StringComparison.Ordinal) || normalized.Contains("address", StringComparison.Ordinal))
            return Result(request, $"The office is located at {request.Configuration.OfficeLocation}.", "office-location", false, null, false);
        if (LooksMedical(normalized))
            return Result(request, MedicalSafetyResponse, "medical-question", true, "clinical_question", true);
        if (normalized.Contains("appointment", StringComparison.Ordinal) || normalized.Contains("calling about", StringComparison.Ordinal))
            return Result(request, "Thank you. I recorded your reason for calling. Is there anything else the office should know?", "reason-for-call", false, null, false);
        if (normalized.StartsWith("my name is ", StringComparison.Ordinal) || normalized.StartsWith("i am ", StringComparison.Ordinal))
            return Result(request, "Thank you. What is the reason for your call today?", "caller-name", false, null, false);
        if (normalized is "no" or "no thanks" || normalized.Contains("goodbye", StringComparison.Ordinal))
            return Result(request, "Thank you for calling. The office team will review your message. Goodbye.", "conversation-end", false, null, true);

        bool askedForName = request.ExistingTurns.Any(turn => turn.Text.Contains("name", StringComparison.OrdinalIgnoreCase));
        return askedForName
            ? Result(request, "Please briefly tell me the reason for your call.", "reason-collection", false, null, false)
            : Result(request, "May I have your name, please?", "name-collection", false, null, false);
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
