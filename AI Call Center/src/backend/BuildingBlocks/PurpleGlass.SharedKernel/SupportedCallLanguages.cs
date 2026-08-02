using System.Collections.Frozen;

namespace PurpleGlass.SharedKernel;

public sealed record SupportedCallLanguage(
    string Code,
    string DisplayName,
    string RecognitionCode,
    string AgentLanguageName,
    string Greeting,
    string FallbackCode);

public static class SupportedCallLanguages
{
    public const string SystemFallbackCode = "en-US";

    private static readonly SupportedCallLanguage[] definitions =
    [
        new("en-US", "English", "en", "English",
            "Thank you for calling our dental office. How can I help you today?", "en-US"),
        new("es-US", "Spanish", "es", "Spanish",
            "Gracias por llamar a nuestra oficina dental. ¿Cómo puedo ayudarle hoy?", "en-US"),
        new("es-PR", "Spanish (Puerto Rico)", "es", "Puerto Rico Spanish",
            "Gracias por llamar a nuestra oficina dental. ¿Cómo puedo ayudarle hoy?", "en-US"),
    ];

    private static readonly FrozenDictionary<string, SupportedCallLanguage> byCode = definitions
        .ToFrozenDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "en-US",
        ["en-us"] = "en-US",
        ["english"] = "en-US",
        ["inglés"] = "en-US",
        ["ingles"] = "en-US",
        ["es"] = "es-US",
        ["es-us"] = "es-US",
        ["spanish"] = "es-US",
        ["español"] = "es-US",
        ["espanol"] = "es-US",
        ["es-pr"] = "es-PR",
        ["puerto rico spanish"] = "es-PR",
        ["español de puerto rico"] = "es-PR",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SupportedCallLanguage> All => definitions;

    public static bool TryNormalize(string? value, out SupportedCallLanguage language)
    {
        language = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string candidate = value.Trim().Replace('_', '-');
        if (byCode.TryGetValue(candidate, out language!)) return true;
        return aliases.TryGetValue(candidate, out string? code) && byCode.TryGetValue(code, out language!);
    }

    public static SupportedCallLanguage Require(string value, string? parameterName = null) =>
        TryNormalize(value, out SupportedCallLanguage language)
            ? language
            : throw new ArgumentException("The call language is not supported.", parameterName ?? nameof(value));

    public static SupportedCallLanguage Fallback => byCode[SystemFallbackCode];

    public static SupportedCallLanguage? FromDetectedCode(string? detectedCode, string? preferredCode = null)
    {
        if (string.IsNullOrWhiteSpace(detectedCode)) return null;
        string baseCode = detectedCode.Trim().Split(['-', '_'], 2)[0].ToLowerInvariant();
        if (TryNormalize(preferredCode, out SupportedCallLanguage preferred)
            && preferred.RecognitionCode.Equals(baseCode, StringComparison.OrdinalIgnoreCase))
            return preferred;
        return definitions.FirstOrDefault(item => item.RecognitionCode.Equals(baseCode, StringComparison.OrdinalIgnoreCase));
    }
}
