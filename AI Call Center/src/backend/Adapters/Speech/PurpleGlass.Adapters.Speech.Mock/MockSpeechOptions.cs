namespace PurpleGlass.Adapters.Speech.Mock;

public sealed record MockSpeechOptions
{
    public decimal RecognitionConfidence { get; init; } = 0.98m;
    public TimeSpan RecognitionDelay { get; init; } = TimeSpan.Zero;
    public TimeSpan SynthesisDelay { get; init; } = TimeSpan.Zero;
    public bool FailRecognition { get; init; }
    public bool FailSynthesis { get; init; }
    public IReadOnlySet<string> SupportedVoices { get; init; } =
        new HashSet<string>(["calm-a", "bright-b"], StringComparer.OrdinalIgnoreCase);
}
