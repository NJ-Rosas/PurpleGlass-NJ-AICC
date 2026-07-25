using PurpleGlass.Eventing;

namespace PurpleGlass.UnitTests;

public sealed class OutboxMessageTests
{
    [Fact]
    public void FailureUsesExponentialBackoffAndClearsLease()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OutboxMessage message = Create(now);
        Guid leaseId = Guid.NewGuid();
        message.Claim(leaseId, now.AddSeconds(30));

        message.MarkFailed(
            leaseId,
            "broker unavailable",
            now,
            maximumAttempts: 3,
            initialRetryDelay: TimeSpan.FromSeconds(2),
            maximumRetryDelay: TimeSpan.FromMinutes(1));

        Assert.Equal(OutboxMessage.PendingStatus, message.Status);
        Assert.Equal(now.AddSeconds(2), message.NextAttemptAtUtc);
        Assert.Equal(1, message.Attempts);
        Assert.Null(message.LeaseId);
        Assert.Null(message.LeaseExpiresAtUtc);
    }

    [Fact]
    public void FinalFailureMovesMessageToDeadLetter()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OutboxMessage message = Create(now);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Guid leaseId = Guid.NewGuid();
            message.Claim(leaseId, now.AddSeconds(30));
            message.MarkFailed(
                leaseId,
                $"failure-{attempt + 1}",
                now,
                maximumAttempts: 3,
                initialRetryDelay: TimeSpan.FromSeconds(1),
                maximumRetryDelay: TimeSpan.FromSeconds(10));
        }

        Assert.Equal(OutboxMessage.DeadLetterStatus, message.Status);
        Assert.Equal(3, message.Attempts);
        Assert.Null(message.NextAttemptAtUtc);
        Assert.Equal("failure-3", message.LastError);
    }

    [Fact]
    public void CompletionRequiresMatchingLease()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OutboxMessage message = Create(now);
        message.Claim(Guid.NewGuid(), now.AddSeconds(30));

        _ = Assert.Throws<InvalidOperationException>(() =>
            message.MarkPublished(Guid.NewGuid(), now));
    }

    private static OutboxMessage Create(DateTimeOffset now) => OutboxMessage.Create(
        Guid.NewGuid(),
        Guid.NewGuid(),
        $"pg/local/v1/tenants/{Guid.NewGuid():D}/events/test",
        "TestEvent",
        "{}",
        Guid.NewGuid(),
        now);
}
