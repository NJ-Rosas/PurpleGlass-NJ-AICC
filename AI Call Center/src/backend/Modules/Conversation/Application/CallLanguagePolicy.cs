using System.Globalization;
using System.Text;
using PurpleGlass.SharedKernel;

namespace PurpleGlass.Modules.Conversation.Application;

public static class CallLanguageReasons
{
    public const string LocationDefault = "location_default";
    public const string CallOverride = "call_override";
    public const string CallerExplicitRequest = "caller_explicit_request";
    public const string AutomaticDetection = "automatic_detection";
    public const string Fallback = "fallback";

    public static string Normalize(string? value) => value switch
    {
        LocationDefault => LocationDefault,
        CallOverride => CallOverride,
        CallerExplicitRequest => CallerExplicitRequest,
        AutomaticDetection => AutomaticDetection,
        _ => Fallback,
    };
}

public sealed record CallLanguagePolicyOptions(
    bool AutomaticSwitchingEnabled = true,
    int EvidenceThreshold = 2,
    int ReversalEvidenceThreshold = 3,
    int CooldownMeaningfulTurns = 2,
    int MinimumWords = 4,
    int MinimumCharacters = 16,
    decimal MinimumConfidence = 0.75m)
{
    public CallLanguagePolicyOptions Validate()
    {
        if (EvidenceThreshold is < 2 or > 5)
            throw new InvalidOperationException("Language evidence threshold must be between two and five turns.");
        if (ReversalEvidenceThreshold < EvidenceThreshold || ReversalEvidenceThreshold > 8)
            throw new InvalidOperationException("Language reversal evidence must be at least the initial threshold and no greater than eight turns.");
        if (CooldownMeaningfulTurns is < 0 or > 10)
            throw new InvalidOperationException("Language cooldown must be between zero and ten meaningful turns.");
        if (MinimumWords is < 2 or > 20 || MinimumCharacters is < 8 or > 200)
            throw new InvalidOperationException("Meaningful language evidence bounds are invalid.");
        if (MinimumConfidence is < 0 or > 1)
            throw new InvalidOperationException("Language confidence must be between zero and one.");
        return this;
    }
}

public sealed record CallLanguageDecision(
    bool IsLanguageRequest,
    bool Accepted,
    bool Unsupported,
    string ActiveLanguageCode,
    string? RequestedLanguageCode,
    string Result,
    string Reason,
    decimal? Confidence,
    int AlternateEvidenceCount,
    long Version);

public sealed class CallLanguageState
{
    private readonly CallLanguagePolicyOptions policy;
    private string? evidenceLanguage;
    private int evidenceCount;
    private int meaningfulTurnsSinceChange;

    public CallLanguageState(
        string startingLanguageCode,
        string reason,
        DateTimeOffset changedAtUtc,
        CallLanguagePolicyOptions? policy = null)
    {
        SupportedCallLanguage language = SupportedCallLanguages.Require(startingLanguageCode, nameof(startingLanguageCode));
        StartingLanguageCode = language.Code;
        ActiveLanguageCode = language.Code;
        Reason = CallLanguageReasons.Normalize(reason);
        ChangedAtUtc = changedAtUtc;
        this.policy = (policy ?? new CallLanguagePolicyOptions()).Validate();
    }

    public string StartingLanguageCode { get; }
    public string ActiveLanguageCode { get; private set; }
    public string Reason { get; private set; }
    public DateTimeOffset ChangedAtUtc { get; private set; }
    public decimal? DetectionConfidence { get; private set; }
    public long Version { get; private set; }

    public void Restore(string activeLanguageCode, string reason, DateTimeOffset? changedAtUtc, long version)
    {
        ActiveLanguageCode = SupportedCallLanguages.Require(activeLanguageCode).Code;
        Reason = CallLanguageReasons.Normalize(reason);
        ChangedAtUtc = changedAtUtc ?? ChangedAtUtc;
        Version = Math.Max(0, version);
        meaningfulTurnsSinceChange = policy.CooldownMeaningfulTurns + 1;
        ResetEvidence();
    }

    public CallLanguageDecision Evaluate(
        string transcript,
        SpeechRecognitionResult recognition,
        DateTimeOffset now)
    {
        ExplicitLanguageRequest explicitRequest = ExplicitLanguageRequestDetector.Detect(transcript);
        if (explicitRequest.IsRequest)
        {
            ResetEvidence();
            if (explicitRequest.LanguageCode is null)
                return Decision(true, false, true, null, "unsupported", CallLanguageReasons.CallerExplicitRequest, null);
            if (ActiveLanguageCode.Equals(explicitRequest.LanguageCode, StringComparison.OrdinalIgnoreCase))
                return Decision(true, false, false, explicitRequest.LanguageCode, "already_active", CallLanguageReasons.CallerExplicitRequest, null);
            Apply(explicitRequest.LanguageCode, CallLanguageReasons.CallerExplicitRequest, now, null);
            return Decision(true, true, false, explicitRequest.LanguageCode, "switched", Reason, null);
        }

        if (!policy.AutomaticSwitchingEnabled)
            return Decision(false, false, false, null, "automatic_disabled", Reason, recognition.DetectionConfidence);
        if (!IsMeaningful(transcript))
            return RejectAutomatic("not_meaningful", recognition.DetectionConfidence);
        meaningfulTurnsSinceChange++;
        if (recognition.DetectedLanguageCodes.Count != 1)
            return RejectAutomatic(recognition.DetectedLanguageCodes.Count > 1 ? "mixed_language" : "no_detection", recognition.DetectionConfidence);
        if (recognition.DetectionConfidence.HasValue && recognition.DetectionConfidence.Value < policy.MinimumConfidence)
            return RejectAutomatic("low_confidence", recognition.DetectionConfidence);

        SupportedCallLanguage? detected = SupportedCallLanguages.FromDetectedCode(
            recognition.DetectedLanguageCodes[0], ActiveLanguageCode);
        if (detected is null) return RejectAutomatic("unsupported_detection", recognition.DetectionConfidence);
        if (SameLanguageFamily(detected.Code, ActiveLanguageCode))
        {
            ResetEvidence();
            return Decision(false, false, false, detected.Code, "active_language_confirmed", Reason,
                recognition.DetectionConfidence);
        }
        if (IsContextFragment(transcript)) return RejectAutomatic("context_fragment", recognition.DetectionConfidence);
        if (!string.Equals(evidenceLanguage, detected.Code, StringComparison.OrdinalIgnoreCase))
        {
            evidenceLanguage = detected.Code;
            evidenceCount = 0;
        }
        evidenceCount++;
        int threshold = Version > 0 || Reason == CallLanguageReasons.CallerExplicitRequest
            ? policy.ReversalEvidenceThreshold : policy.EvidenceThreshold;
        if (Version > 0 && meaningfulTurnsSinceChange <= policy.CooldownMeaningfulTurns)
            return Decision(false, false, false, detected.Code, "cooldown", Reason,
                recognition.DetectionConfidence);
        if (evidenceCount < threshold)
            return Decision(false, false, false, detected.Code, "evidence_accumulating", Reason,
                recognition.DetectionConfidence);

        decimal? confidence = recognition.DetectionConfidence;
        Apply(detected.Code, CallLanguageReasons.AutomaticDetection, now, confidence);
        return Decision(false, true, false, detected.Code, "switched", Reason, confidence);
    }

    private void Apply(string code, string reason, DateTimeOffset now, decimal? confidence)
    {
        ActiveLanguageCode = SupportedCallLanguages.Require(code).Code;
        Reason = reason;
        ChangedAtUtc = now;
        DetectionConfidence = confidence;
        Version++;
        meaningfulTurnsSinceChange = 0;
        ResetEvidence();
    }

    private CallLanguageDecision RejectAutomatic(string result, decimal? confidence)
    {
        if (result is not "cooldown" and not "evidence_accumulating") ResetEvidence();
        return Decision(false, false, false, evidenceLanguage, result, Reason, confidence);
    }

    private CallLanguageDecision Decision(bool request, bool accepted, bool unsupported,
        string? requested, string result, string reason, decimal? confidence) =>
        new(request, accepted, unsupported, ActiveLanguageCode, requested, result, reason,
            confidence, evidenceCount, Version);

    private void ResetEvidence()
    {
        evidenceLanguage = null;
        evidenceCount = 0;
    }

    private bool IsMeaningful(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return text.Trim().Length >= policy.MinimumCharacters && words.Length >= policy.MinimumWords;
    }

    private static bool IsContextFragment(string text)
    {
        string normalized = ExplicitLanguageRequestDetector.Normalize(text);
        return normalized.StartsWith("my name is ", StringComparison.Ordinal)
            || normalized.StartsWith("mi nombre es ", StringComparison.Ordinal)
            || normalized.StartsWith("the address is ", StringComparison.Ordinal)
            || normalized.StartsWith("la direccion es ", StringComparison.Ordinal);
    }

    private static bool SameLanguageFamily(string left, string right) =>
        left.Split('-')[0].Equals(right.Split('-')[0], StringComparison.OrdinalIgnoreCase);
}

public sealed record ExplicitLanguageRequest(bool IsRequest, string? LanguageCode);

public static class ExplicitLanguageRequestDetector
{
    private static readonly string[] requestPhrases =
    [
        "speak ", "continue in ", "change to ", "switch to ", "can we continue in ",
        "por favor habla ", "por favor hable ", "puedes hablar ", "puede hablar ",
        "hablame en ", "hableme en ", "continua en ", "continuar en ",
        "cambia a ", "cambie a ",
    ];

    private static readonly HashSet<string> unsupportedNames = new(StringComparer.Ordinal)
    {
        "french", "frances", "german", "aleman", "italian", "italiano", "portuguese", "portugues",
        "mandarin", "chinese", "chino", "japanese", "japones", "korean", "coreano",
    };

    public static ExplicitLanguageRequest Detect(string transcript)
    {
        string normalized = Normalize(transcript);
        if (!requestPhrases.Any(normalized.Contains)) return new(false, null);
        foreach (SupportedCallLanguage language in SupportedCallLanguages.All.OrderByDescending(item => item.Code == "es-PR"))
        {
            if (ContainsLanguage(normalized, language)) return new(true, language.Code);
        }
        return unsupportedNames.Any(normalized.Contains)
            ? new(true, null)
            : new(false, null);
    }

    internal static string Normalize(string text)
    {
        string decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark
                && (char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || character == '-'))
                builder.Append(character);
        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool ContainsLanguage(string text, SupportedCallLanguage language) => language.Code switch
    {
        "en-US" => text.Contains("english", StringComparison.Ordinal) || text.Contains("ingles", StringComparison.Ordinal),
        "es-US" => text.Contains("spanish", StringComparison.Ordinal) || text.Contains("espanol", StringComparison.Ordinal),
        "es-PR" => text.Contains("puerto rico spanish", StringComparison.Ordinal)
            || text.Contains("espanol de puerto rico", StringComparison.Ordinal),
        _ => false,
    };
}
