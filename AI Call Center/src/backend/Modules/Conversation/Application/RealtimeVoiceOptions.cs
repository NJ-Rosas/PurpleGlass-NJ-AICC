namespace PurpleGlass.Modules.Conversation.Application;

public sealed record RealtimeVoiceOptions
{
    public const string SectionName = "Voice";

    public bool Enabled { get; init; } = true;
    public required ConversationRuntimeConfiguration Conversation { get; init; }
    public CallLanguagePolicyOptions LanguagePolicy { get; init; } = new();
    public TimeSpan RecognitionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan LanguageModelTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan SynthesisTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan EndOfUtteranceSilence { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaximumUtteranceDuration { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan MinimumSpeechDuration { get; init; } = TimeSpan.FromMilliseconds(120);
    public TimeSpan MaximumSpeechCandidateDuration { get; init; } = TimeSpan.FromMilliseconds(300);
    public int SpeechEnergyThreshold { get; init; } = 500;
    public double SpeechContinuationThresholdRatio { get; init; } = 0.70;
    public double SpeechStartSnrMultiplier { get; init; } = 3.0;
    public double MinimumSpeechLikeFrameRatio { get; init; } = 0.50;
    public int AudioQueueCapacity { get; init; } = 100;
    public int UtteranceQueueCapacity { get; init; } = 4;
    public int MaximumAudioBytesPerUtterance { get; init; } = 640_000;
    public int MaximumProviderAttempts { get; init; } = 1;

    public RealtimeVoiceOptions Validate()
    {
        ArgumentNullException.ThrowIfNull(Conversation);
        ArgumentNullException.ThrowIfNull(LanguagePolicy);
        Conversation.Validate();
        LanguagePolicy.Validate();
        ValidateTimeout(RecognitionTimeout, nameof(RecognitionTimeout), TimeSpan.FromMinutes(2));
        ValidateTimeout(LanguageModelTimeout, nameof(LanguageModelTimeout), TimeSpan.FromMinutes(2));
        ValidateTimeout(SynthesisTimeout, nameof(SynthesisTimeout), TimeSpan.FromMinutes(2));
        ValidateTimeout(CleanupTimeout, nameof(CleanupTimeout), TimeSpan.FromSeconds(30));
        ValidateTimeout(EndOfUtteranceSilence, nameof(EndOfUtteranceSilence), TimeSpan.FromSeconds(5));
        ValidateTimeout(MaximumUtteranceDuration, nameof(MaximumUtteranceDuration), TimeSpan.FromMinutes(2));
        ValidateTimeout(MinimumSpeechDuration, nameof(MinimumSpeechDuration), TimeSpan.FromSeconds(2));
        ValidateTimeout(MaximumSpeechCandidateDuration, nameof(MaximumSpeechCandidateDuration), TimeSpan.FromSeconds(2));
        if (MinimumSpeechDuration >= MaximumUtteranceDuration)
            throw new InvalidOperationException("MinimumSpeechDuration must be shorter than MaximumUtteranceDuration.");
        if (MaximumSpeechCandidateDuration < MinimumSpeechDuration
            || MaximumSpeechCandidateDuration >= MaximumUtteranceDuration)
            throw new InvalidOperationException("MaximumSpeechCandidateDuration must bound speech qualification.");
        if (SpeechEnergyThreshold is < 0 or > short.MaxValue)
            throw new InvalidOperationException("SpeechEnergyThreshold is outside the PCM16 range.");
        if (SpeechContinuationThresholdRatio is < 0.25 or > 1)
            throw new InvalidOperationException("SpeechContinuationThresholdRatio must be between 0.25 and 1.");
        if (SpeechStartSnrMultiplier is < 1.25 or > 10)
            throw new InvalidOperationException("SpeechStartSnrMultiplier must be between 1.25 and 10.");
        if (MinimumSpeechLikeFrameRatio is < 0.25 or > 1)
            throw new InvalidOperationException("MinimumSpeechLikeFrameRatio must be between 0.25 and 1.");
        if (AudioQueueCapacity is < 4 or > 2_000)
            throw new InvalidOperationException("AudioQueueCapacity must be between 4 and 2000.");
        if (UtteranceQueueCapacity is < 1 or > 20)
            throw new InvalidOperationException("UtteranceQueueCapacity must be between 1 and 20.");
        if (MaximumAudioBytesPerUtterance is < 1_024 or > 10_000_000)
            throw new InvalidOperationException("MaximumAudioBytesPerUtterance must be between 1024 and 10000000.");
        if (MaximumProviderAttempts is < 1 or > 2)
            throw new InvalidOperationException("MaximumProviderAttempts must be one or two.");
        return this;
    }

    private static void ValidateTimeout(TimeSpan value, string name, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum)
            throw new InvalidOperationException($"{name} must be positive and no greater than {maximum}.");
    }
}
