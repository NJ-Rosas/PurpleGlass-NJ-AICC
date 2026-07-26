namespace PurpleGlass.Integrations.Worker;

public sealed class TelephonyRuntimeOptions
{
    public const string SectionName = "Telephony";
    public string Provider { get; init; } = "None";
    public string PublicBaseUrl { get; init; } = string.Empty;
    public int PollMilliseconds { get; init; } = 500;
}
