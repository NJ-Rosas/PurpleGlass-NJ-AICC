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
        Assert.Equal(now, message.LastAttemptAtUtc);
        Assert.Equal(now, message.DeadLetteredAtUtc);
    }

    [Fact]
    public void RequeuePreservesFailureEvidenceAndRecordsOperatorRecovery()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OutboxMessage message = Create(now);
        Guid lease = Guid.NewGuid();
        message.Claim(lease, now.AddMinutes(1));
        message.MarkFailed(lease, "timeout", now, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Guid correlation = Guid.NewGuid();

        message.Requeue("operator-1", correlation, now.AddMinutes(2));

        Assert.Equal(OutboxMessage.PendingStatus, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.Equal("timeout", message.LastError);
        Assert.Equal(now, message.DeadLetteredAtUtc);
        Assert.Equal(1, message.RecoveryCount);
        Assert.Equal("operator-1", message.LastRecoveredBy);
        Assert.Equal(correlation, message.LastRecoveryCorrelationId);
        _ = Assert.Throws<InvalidOperationException>(() =>
            message.Requeue("operator-2", Guid.NewGuid(), now.AddMinutes(3)));
    }

    [Fact]
    public void NonTerminalMessageCannotBeRequeued()
    {
        OutboxMessage message = Create(DateTimeOffset.UtcNow);
        _ = Assert.Throws<InvalidOperationException>(() =>
            message.Requeue("operator-1", Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void LeasedAndPublishedMessagesCannotBeRequeued()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OutboxMessage leased = Create(now);
        leased.Claim(Guid.NewGuid(), now.AddMinutes(1));
        _ = Assert.Throws<InvalidOperationException>(() => leased.Requeue("operator", Guid.NewGuid(), now));

        OutboxMessage published = Create(now);
        Guid lease = Guid.NewGuid();
        published.Claim(lease, now.AddMinutes(1));
        published.MarkPublished(lease, now);
        _ = Assert.Throws<InvalidOperationException>(() => published.Requeue("operator", Guid.NewGuid(), now));
    }

    [Fact]
    public void RecoveredMessageCanDeadLetterAgainWithoutLosingRecoveryHistory()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OutboxMessage message = Create(now);
        Guid firstLease = Guid.NewGuid();
        message.Claim(firstLease, now.AddMinutes(1));
        message.MarkFailed(firstLease, "timeout", now, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        message.Requeue("operator", Guid.NewGuid(), now.AddMinutes(1));
        Guid secondLease = Guid.NewGuid();
        message.Claim(secondLease, now.AddMinutes(2));
        message.MarkFailed(secondLease, "network", now.AddMinutes(2), 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        Assert.Equal(OutboxMessage.DeadLetterStatus, message.Status);
        Assert.Equal(2, message.Attempts);
        Assert.Equal(1, message.RecoveryCount);
        Assert.Equal("operator", message.LastRecoveredBy);
        Assert.Equal("network", message.LastError);
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
