namespace PurpleGlass.Integrations.Worker;

public sealed class OutboxPublisherOptions
{
    public const string SectionName = "OutboxPublisher";

    public int BatchSize { get; init; } = 20;

    public int MaximumAttempts { get; init; } = 10;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan FailureRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (BatchSize is < 1 or > 100) throw new InvalidOperationException("OutboxPublisher:BatchSize must be between 1 and 100.");
        if (MaximumAttempts < 1) throw new InvalidOperationException("OutboxPublisher:MaximumAttempts must be positive.");
        if (LeaseDuration <= TimeSpan.Zero) throw new InvalidOperationException("OutboxPublisher:LeaseDuration must be positive.");
        if (InitialRetryDelay <= TimeSpan.Zero) throw new InvalidOperationException("OutboxPublisher:InitialRetryDelay must be positive.");
        if (MaximumRetryDelay < InitialRetryDelay) throw new InvalidOperationException("OutboxPublisher:MaximumRetryDelay must not be shorter than InitialRetryDelay.");
        if (PollInterval <= TimeSpan.Zero) throw new InvalidOperationException("OutboxPublisher:PollInterval must be positive.");
        if (FailureRetryDelay <= TimeSpan.Zero) throw new InvalidOperationException("OutboxPublisher:FailureRetryDelay must be positive.");
    }
}
