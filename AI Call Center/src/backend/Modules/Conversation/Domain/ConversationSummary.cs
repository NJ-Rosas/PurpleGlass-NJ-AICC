namespace PurpleGlass.Modules.Conversation.Domain;

public sealed record ConversationSummary
{
    public ConversationSummary(
        string text,
        string? callerIntent,
        string outcome,
        bool followUpRequired,
        bool escalated,
        DateTimeOffset generatedAtUtc,
        string configurationVersion)
    {
        Text = RequireValue(text, nameof(text), 4_000);
        CallerIntent = NormalizeOptional(callerIntent, nameof(callerIntent), 200);
        Outcome = RequireValue(outcome, nameof(outcome), 200);
        FollowUpRequired = followUpRequired;
        Escalated = escalated;
        GeneratedAtUtc = generatedAtUtc;
        ConfigurationVersion = RequireValue(configurationVersion, nameof(configurationVersion), 100);
    }

    public string Text { get; }

    public string? CallerIntent { get; }

    public string Outcome { get; }

    public bool FollowUpRequired { get; }

    public bool Escalated { get; }

    public DateTimeOffset GeneratedAtUtc { get; }

    public string ConfigurationVersion { get; }

    private static string RequireValue(string value, string parameterName, int maximumLength)
    {
        string normalized = value.Trim();
        if (normalized.Length is 0 || normalized.Length > maximumLength)
        {
            throw new ArgumentException($"Value must contain between 1 and {maximumLength} characters.", parameterName);
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, string parameterName, int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException($"Value cannot exceed {maximumLength} characters.", parameterName);
        }

        return normalized;
    }
}
