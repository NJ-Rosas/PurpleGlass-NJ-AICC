namespace PurpleGlass.Adapters.Speech.OpenAI;

public sealed record OpenAiSpeechOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public Uri BaseUri { get; init; } = new("https://api.openai.com/v1/", UriKind.Absolute);
    public string TranscriptionModel { get; init; } = string.Empty;
    public string SynthesisModel { get; init; } = string.Empty;
    public int MaximumInputAudioBytes { get; init; } = 20 * 1024 * 1024;
    public int MaximumTranscriptionResponseBytes { get; init; } = 256 * 1024;
    public int MaximumSpeechResponseBytes { get; init; } = 16 * 1024 * 1024;
    public int MaximumTranscriptCharacters { get; init; } = 8_000;
    public int MaximumSynthesisCharacters { get; init; } = 4_096;
    public int AudioChunkBytes { get; init; } = 4_800;

    public OpenAiSpeechOptions Validate()
    {
        ValidateSecret(ApiKey);
        ValidateIdentifier(TranscriptionModel, nameof(TranscriptionModel), 100);
        ValidateIdentifier(SynthesisModel, nameof(SynthesisModel), 100);
        ValidateBaseUri(BaseUri);
        ValidateSize(MaximumInputAudioBytes, nameof(MaximumInputAudioBytes), 1024, 25 * 1024 * 1024);
        ValidateSize(MaximumTranscriptionResponseBytes, nameof(MaximumTranscriptionResponseBytes), 1024, 1024 * 1024);
        ValidateSize(MaximumSpeechResponseBytes, nameof(MaximumSpeechResponseBytes), 16 * 1024, 64 * 1024 * 1024);
        ValidateSize(MaximumTranscriptCharacters, nameof(MaximumTranscriptCharacters), 100, 8_000);
        ValidateSize(MaximumSynthesisCharacters, nameof(MaximumSynthesisCharacters), 100, 4_096);
        ValidateSize(AudioChunkBytes, nameof(AudioChunkBytes), 480, 64 * 1024);
        if (AudioChunkBytes % 2 != 0)
        {
            throw new InvalidOperationException("AudioChunkBytes must preserve 16-bit PCM sample alignment.");
        }

        return this;
    }

    internal Uri TranscriptionsEndpoint => new($"{BaseUri.AbsoluteUri.TrimEnd('/')}/audio/transcriptions", UriKind.Absolute);
    internal Uri SpeechEndpoint => new($"{BaseUri.AbsoluteUri.TrimEnd('/')}/audio/speech", UriKind.Absolute);

    private static void ValidateSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidOperationException("A bounded OpenAI API key is required when an OpenAI speech adapter is enabled.");
        }
    }

    private static void ValidateIdentifier(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidOperationException($"{name} must be a bounded provider identifier.");
        }
    }

    private static void ValidateBaseUri(Uri? value)
    {
        if (value is null || !value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(value.UserInfo) || !string.IsNullOrEmpty(value.Query)
            || !string.IsNullOrEmpty(value.Fragment))
        {
            throw new InvalidOperationException("The OpenAI base URI must be an absolute HTTPS URI without credentials, query, or fragment components.");
        }
    }

    private static void ValidateSize(int value, string name, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}.");
        }
    }
}
