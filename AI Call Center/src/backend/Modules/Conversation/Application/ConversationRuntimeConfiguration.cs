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
        return this;
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
    }
}

public sealed record VoiceConfiguration(string VoiceId, string Style = "neutral");

public sealed record SafetyEscalationPolicy(
    string Version,
    IReadOnlyList<string> EscalationKeywords,
    IReadOnlyList<string> UrgentKeywords);
