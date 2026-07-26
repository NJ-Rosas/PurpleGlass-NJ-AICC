using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PurpleGlass.Eventing;

namespace PurpleGlass.Eventing.Infrastructure;

public sealed class InboxDeduplicationStore(EventingDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<bool> ExecuteOnceAsync(
        string consumerName,
        Guid messageId,
        Guid tenantId,
        Func<CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        string normalizedConsumer = consumerName.Trim();
        if (normalizedConsumer.Length is < 1 or > 150)
        {
            throw new ArgumentException("A consumer name between 1 and 150 characters is required.", nameof(consumerName));
        }
        if (messageId == Guid.Empty) throw new ArgumentException("A message identifier is required.", nameof(messageId));
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant identifier is required.", nameof(tenantId));
        ArgumentNullException.ThrowIfNull(handler);

        await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        DateTimeOffset receivedAtUtc = timeProvider.GetUtcNow();
        InboxMessage receipt = InboxMessage.Create(
            normalizedConsumer,
            messageId,
            tenantId,
            receivedAtUtc);
        dbContext.InboxMessages.Add(receipt);

        try
        {
            _ = await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.Entry(receipt).State = EntityState.Detached;
            return false;
        }

        await handler(cancellationToken);
        receipt.MarkProcessed(timeProvider.GetUtcNow());
        _ = await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
