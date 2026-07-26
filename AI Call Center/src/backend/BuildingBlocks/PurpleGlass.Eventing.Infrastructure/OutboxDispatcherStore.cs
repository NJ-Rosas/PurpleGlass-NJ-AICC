using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PurpleGlass.Eventing;
using PurpleGlass.Observability;

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
        using var activity = PurpleGlassTelemetry.Eventing.StartActivity("outbox.lease", System.Diagnostics.ActivityKind.Consumer);
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
        long recovered = messages.LongCount(message => message.Status == OutboxMessage.ProcessingStatus);
        foreach (OutboxMessage message in messages)
        {
            message.Claim(leaseId, leaseExpiresAtUtc);
        }

        _ = await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        PurpleGlassTelemetry.OutboxLeased.Add(messages.Count);
        PurpleGlassTelemetry.OutboxLeaseRecovered.Add(recovered);
        foreach (OutboxMessage message in messages)
            PurpleGlassTelemetry.OutboxAge.Record(Math.Max(0, (now - message.CreatedAtUtc).TotalSeconds));
        activity?.SetTag("messaging.batch.message_count", messages.Count);
        activity?.SetTag("purpleglass.outbox.recovered_count", recovered);
        return messages;
    }

    public async Task MarkPublishedAsync(
        OutboxMessage message,
        Guid leaseId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken)
    {
        using var activity = PurpleGlassTelemetry.Eventing.StartActivity("outbox.delivery.complete");
        activity?.SetTag("messaging.message.id", message.Id);
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
        using var activity = PurpleGlassTelemetry.Eventing.StartActivity("outbox.retry.schedule");
        activity?.SetTag("messaging.message.id", message.Id);
        message.MarkFailed(
            leaseId,
            error,
            failedAtUtc,
            maximumAttempts,
            initialRetryDelay,
            maximumRetryDelay);
        _ = await dbContext.SaveChangesAsync(cancellationToken);
        PurpleGlassTelemetry.OutboxPublishFailures.Add(1);
        if (message.Status == OutboxMessage.DeadLetterStatus) PurpleGlassTelemetry.OutboxDeadLetters.Add(1);
        else PurpleGlassTelemetry.OutboxRetries.Add(1);
        activity?.SetTag("purpleglass.outbox.outcome", message.Status);
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, TelemetrySanitizer.UnknownErrorCode);
    }
}
