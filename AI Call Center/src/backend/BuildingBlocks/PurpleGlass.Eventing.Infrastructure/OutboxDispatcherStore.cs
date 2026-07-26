using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PurpleGlass.Eventing;

namespace PurpleGlass.Eventing.Infrastructure;

public sealed class OutboxDispatcherStore(EventingDbContext dbContext)
{
    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        Guid leaseId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (leaseId == Guid.Empty) throw new ArgumentException("A lease identifier is required.", nameof(leaseId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 100);

        await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        List<OutboxMessage> messages = await dbContext.OutboxMessages
            .FromSqlInterpolated($"""
                SELECT *
                FROM eventing.outbox_messages
                WHERE (
                    "Status" = {OutboxMessage.PendingStatus}
                    AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= {now})
                ) OR (
                    "Status" = {OutboxMessage.ProcessingStatus}
                    AND "LeaseExpiresAtUtc" <= {now}
                )
                ORDER BY "OccurredAtUtc"
                FOR UPDATE SKIP LOCKED
                LIMIT {batchSize}
                """)
            .ToListAsync(cancellationToken);

        DateTimeOffset leaseExpiresAtUtc = now.Add(leaseDuration);
        foreach (OutboxMessage message in messages)
        {
            message.Claim(leaseId, leaseExpiresAtUtc);
        }

        _ = await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messages;
    }

    public async Task MarkPublishedAsync(
        OutboxMessage message,
        Guid leaseId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken)
    {
        message.MarkPublished(leaseId, publishedAtUtc);
        _ = await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        OutboxMessage message,
        Guid leaseId,
        string error,
        DateTimeOffset failedAtUtc,
        int maximumAttempts,
        TimeSpan initialRetryDelay,
        TimeSpan maximumRetryDelay,
        CancellationToken cancellationToken)
    {
        message.MarkFailed(
            leaseId,
            error,
            failedAtUtc,
            maximumAttempts,
            initialRetryDelay,
            maximumRetryDelay);
        _ = await dbContext.SaveChangesAsync(cancellationToken);
    }
}
