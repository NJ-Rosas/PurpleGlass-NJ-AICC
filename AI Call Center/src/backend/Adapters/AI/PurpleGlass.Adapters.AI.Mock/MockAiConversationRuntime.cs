using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.AI.Mock;

public sealed class MockAiConversationRuntime(MockAiOptions options, TimeProvider timeProvider) : IAiConversationRuntime
{
    private const string MedicalSafetyResponse =
        "I can't provide medical advice. If this may be an emergency, contact local emergency services.";

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
        if (request.Configuration.Language.StartsWith("es", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsAny(normalized, request.SafetyPolicy.UrgentKeywords))
                return Result(request,
                    "No puedo dar consejos médicos. Si puede ser una emergencia, comuníquese con los servicios de emergencia locales.",
                    "urgent-safety", true, "urgent_call", true);
            return Result(request,
                "Claro. Puedo ayudarle a recopilar la información para la oficina dental. ¿En qué puedo ayudarle hoy?",
                "general-intake", false, null, false);
        }
        if (ContainsAny(normalized, request.SafetyPolicy.UrgentKeywords))
            return Result(request, MedicalSafetyResponse, "urgent-safety", true, "urgent_call", true);
        if (LooksLikePromptInjection(normalized))
            return Result(request,
                "I can't provide private patient information or claim an action was completed. What dental-office help do you need?",
                "privacy-boundary", false, null, false);
        if (ContainsAny(normalized, request.SafetyPolicy.EscalationKeywords)
            || normalized.Contains("human", StringComparison.Ordinal))
            return Result(request,
                "I understand you'd like to speak with someone. I can't transfer this call right now, but I can gather what you need help with.",
                "human-unavailable", false, null, false);
        if (normalized is "no" or "no thanks" || normalized.Contains("goodbye", StringComparison.Ordinal))
            return Result(request, "Thank you for calling. Goodbye.", "conversation-end", false, null, true);

        if (ContainsAny(normalized, ["cancel", "cancellation"]))
            return Result(request,
                "I understand you need to cancel an appointment. I can't change it from this call, but what date is the appointment?",
                "appointment-cancellation-intake", false, null, false);
        if (ContainsAny(normalized, ["reschedule", "move my appointment", "change my appointment", "can't make"]))
            return Result(request,
                "I can gather the rescheduling details, but I can't change the appointment from this call. What date is it currently scheduled for?",
                "appointment-reschedule-intake", false, null, false);
        if (ContainsAny(normalized, ["insurance", "coverage", "copay", "eligible", "eligibility"]))
            return Result(request,
                "I don't have insurance eligibility or coverage information available. What would you like help understanding about your insurance question?",
                "insurance-intake", false, null, false);
        if (ContainsAny(normalized, ["bill", "billing", "balance", "charge", "payment"]))
            return Result(request,
                "I don't have access to balances or payment records, but I can gather what your billing question is about. What charge or payment do you need help with?",
                "billing-intake", false, null, false);
        if (ContainsAny(normalized, ["patient record", "patient lookup", "look me up", "my chart", "treatment plan"]))
            return Result(request,
                "I don't have access to patient records on this call. What would you like help with?",
                "patient-record-unavailable", false, null, false);
        if (AsksOfficeHours(normalized))
            return IsAvailable(request.Configuration.OfficeHours)
                ? Result(request, $"The configured office hours are {request.Configuration.OfficeHours.Trim()}.",
                    "office-hours", false, null, false)
                : Result(request, "I don't have the office hours available.",
                    "office-hours-unavailable", false, null, false);
        if (AsksOfficeLocation(normalized))
            return IsAvailable(request.Configuration.OfficeLocation)
                ? Result(request, $"The configured office location is {request.Configuration.OfficeLocation.Trim()}.",
                    "office-location", false, null, false)
                : Result(request, "I don't have the office address available.",
                    "office-location-unavailable", false, null, false);
        if (LooksMedical(normalized))
            return HasAppointmentContext(request.ExistingTurns)
                ? Result(request,
                    "I understand the appointment request is for that tooth concern. How long has it been bothering you?",
                    "appointment-intake", false, null, false)
                : Result(request,
                    "I can't diagnose a dental condition, but I can help gather information about the concern. How long has it been bothering you?",
                    "dental-concern-intake", false, null, false);
        if (LooksLikeAppointmentRequest(normalized))
            return Result(request,
                "Sure, I can help gather the details for an appointment request. Is this for a routine visit or is something bothering you?",
                "appointment-intake", false, null, false);
        if (HasAppointmentContext(request.ExistingTurns))
            return Result(request,
                "I understand this is for the appointment request you mentioned. How long has that concern been bothering you?",
                "appointment-intake", false, null, false);

        return Result(request, "Of course. What would you like help with today?", "general-intake", false, null, false);
    }

    private static bool ContainsAny(string input, IEnumerable<string> candidates) =>
        candidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate)
            && input.Contains(candidate.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool LooksMedical(string input) =>
        input.Contains("diagnose", StringComparison.Ordinal)
        || input.Contains("what medicine", StringComparison.Ordinal)
        || input.Contains("tooth hurts", StringComparison.Ordinal)
        || input.Contains("toothache", StringComparison.Ordinal)
        || input.Contains("infection", StringComparison.Ordinal)
        || input.Contains("pain", StringComparison.Ordinal)
        || input.Contains("swelling", StringComparison.Ordinal)
        || input.Contains("cold water", StringComparison.Ordinal)
        || input.Contains("sensitive", StringComparison.Ordinal);

    private static bool LooksLikeAppointmentRequest(string input) =>
        ContainsAny(input, ["appointment", "book", "schedule", "cleaning", "checkup"]);

    private static bool HasAppointmentContext(IEnumerable<SanitizedConversationTurn> history) =>
        history.Any(turn => turn.Speaker.Equals("Caller", StringComparison.OrdinalIgnoreCase)
            && LooksLikeAppointmentRequest(turn.Text.ToLowerInvariant()));

    private static bool LooksLikePromptInjection(string input) =>
        input.Contains("ignore your instructions", StringComparison.Ordinal)
        || input.Contains("another patient", StringComparison.Ordinal)
        || input.Contains("another tenant", StringComparison.Ordinal)
        || input.Contains("pretend you booked", StringComparison.Ordinal);

    private static bool AsksOfficeHours(string input) =>
        input.Contains("hours", StringComparison.Ordinal)
        || input.Contains("what time do you", StringComparison.Ordinal)
        || input.Contains("when do you open", StringComparison.Ordinal)
        || input.Contains("when do you close", StringComparison.Ordinal);

    private static bool AsksOfficeLocation(string input) =>
        input.Contains("address", StringComparison.Ordinal)
        || input.Contains("where are you", StringComparison.Ordinal)
        || input.Contains("your location", StringComparison.Ordinal);

    private static bool IsAvailable(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Trim().Equals("not configured", StringComparison.OrdinalIgnoreCase);

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
