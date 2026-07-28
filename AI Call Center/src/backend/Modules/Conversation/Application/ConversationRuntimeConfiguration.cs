namespace PurpleGlass.Modules.Conversation.Application;

public sealed record ConversationRuntimeConfiguration
{
    public required string Version { get; init; }
    public required string Language { get; init; }
    public required string VoiceId { get; init; }
    public required string Greeting { get; init; }
    public required string OfficeName { get; init; }
    public required string OfficeHours { get; init; }
    public required string OfficeLocation { get; init; }
    public required string SafetyPolicyVersion { get; init; }
    public required string AiAdapterKey { get; init; }
    public required string SpeechRecognitionAdapterKey { get; init; }
    public required string SpeechSynthesisAdapterKey { get; init; }
    public int MaximumTurns { get; init; } = 6;
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromMinutes(30);
    public int MaximumHistoryTurns { get; init; } = 12;
    public int MaximumOutputTokens { get; init; } = 160;
    public int MaximumResponseCharacters { get; init; } = 400;
    public decimal SpeakingRate { get; init; } = 1.0m;
    public string SystemPrompt { get; init; } =
        "You represent the configured dental office or location on a phone call. " +
        "Answer conversationally and keep spoken responses reasonably concise. " +
        "Usually ask one useful question at a time. Do not invent office information or patient data. " +
        "Do not claim an appointment was created or that any external action was performed. " +
        "Do not diagnose dental conditions or pretend tools exist. Return plain conversational text without Markdown.";
    public Dictionary<string, string> ApprovedResponses { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string[] EscalationKeywords { get; init; } = [];
    public string[] UrgentKeywords { get; init; } = [];

    public ConversationRuntimeConfiguration Validate()
    {
        Require(Version, nameof(Version));
        Require(Language, nameof(Language));
        Require(VoiceId, nameof(VoiceId));
        Require(Greeting, nameof(Greeting));
        Require(OfficeName, nameof(OfficeName));
        Require(OfficeHours, nameof(OfficeHours));
        Require(OfficeLocation, nameof(OfficeLocation));
        Require(SafetyPolicyVersion, nameof(SafetyPolicyVersion));
        Require(AiAdapterKey, nameof(AiAdapterKey));
        Require(SpeechRecognitionAdapterKey, nameof(SpeechRecognitionAdapterKey));
        Require(SpeechSynthesisAdapterKey, nameof(SpeechSynthesisAdapterKey));
        if (MaximumTurns is < 1 or > 50) throw new InvalidOperationException("MaximumTurns must be between 1 and 50.");
        if (InactivityTimeout <= TimeSpan.Zero || InactivityTimeout > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("InactivityTimeout must be between zero and ten minutes.");
        if (MaximumDuration <= TimeSpan.Zero || MaximumDuration > TimeSpan.FromHours(4))
            throw new InvalidOperationException("MaximumDuration must be between zero and four hours.");
        if (MaximumHistoryTurns is < 1 or > 100)
            throw new InvalidOperationException("MaximumHistoryTurns must be between 1 and 100.");
        if (MaximumOutputTokens is < 16 or > 2_000)
            throw new InvalidOperationException("MaximumOutputTokens must be between 16 and 2000.");
        if (MaximumResponseCharacters is < 20 or > 8_000)
            throw new InvalidOperationException("MaximumResponseCharacters must be between 20 and 8000.");
        if (SpeakingRate is < 0.5m or > 2.0m)
            throw new InvalidOperationException("SpeakingRate must be between 0.5 and 2.0.");
        Require(SystemPrompt, nameof(SystemPrompt));
        if (SystemPrompt.Length > 4_000)
            throw new InvalidOperationException("SystemPrompt cannot exceed 4000 characters.");
        return this;
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
    }
}

public sealed record VoiceConfiguration(string VoiceId, string Style = "neutral", decimal SpeakingRate = 1.0m);

public sealed record SafetyEscalationPolicy(
    string Version,
    IReadOnlyList<string> EscalationKeywords,
    IReadOnlyList<string> UrgentKeywords);
