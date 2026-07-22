namespace PurpleGlass.Adapters.AI.Mock;

public sealed record MockAiOptions
{
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;
    public bool FailGeneration { get; init; }
}
