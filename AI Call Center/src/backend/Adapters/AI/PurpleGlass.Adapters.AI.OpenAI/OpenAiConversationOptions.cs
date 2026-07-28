namespace PurpleGlass.Adapters.AI.OpenAI;

public sealed record OpenAiConversationOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public Uri BaseUri { get; init; } = new("https://api.openai.com/v1/", UriKind.Absolute);
    public string Model { get; init; } = string.Empty;

    public OpenAiConversationOptions Validate()
    {
        ValidateSecret(ApiKey);
        ValidateIdentifier(Model, nameof(Model), 100);
        ValidateBaseUri(BaseUri);
        return this;
    }

    private static void ValidateSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidOperationException("A bounded OpenAI API key is required when the OpenAI conversation adapter is enabled.");
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
}
