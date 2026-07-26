using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PurpleGlass.Eventing;
using PurpleGlass.Observability;

namespace PurpleGlass.Eventing.Infrastructure;

public sealed record DeadLetterQuery(
    Guid TenantId,
    IReadOnlySet<Guid> AuthorizedLocationIds,
    Guid? LocationId = null,
    string? MessageType = null,
    string? FailureCategory = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    Guid? MessageId = null,
    Guid? CorrelationId = null,
    string? TraceId = null,
    int Page = 1,
    int PageSize = 20);

public sealed record DeadLetterSummary(
    Guid MessageId,
    Guid TenantId,
    Guid LocationId,
    string MessageType,
    string Status,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? DeadLetteredAtUtc,
    Guid CorrelationId,
    string? TraceId,
    string FailureCategory,
    string FailureSummary,
    int RecoveryCount,
    DateTimeOffset? LastRecoveredAtUtc);

public sealed record DeadLetterDetail(
    DeadLetterSummary Message,
    Guid? CausationId,
    string? TraceParent,
    string Producer,
    string DataClassification,
    Guid? LastRecoveryCorrelationId,
    bool Recoverable);

public sealed record DeadLetterPage(IReadOnlyList<DeadLetterSummary> Items, int Page, int PageSize, int TotalCount);

public enum DeadLetterRecoveryResult { Recovered, NotFound, NotDeadLettered }

public sealed class DeadLetterOperationsService(EventingDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<DeadLetterPage> QueryAsync(DeadLetterQuery request, CancellationToken cancellationToken)
    {
        using Activity? activity = PurpleGlassTelemetry.Eventing.StartActivity("deadletter.query");
        Validate(request);
        IQueryable<OutboxMessage> query = Scoped(request.TenantId, request.AuthorizedLocationIds)
            .Where(message => message.Status == OutboxMessage.DeadLetterStatus);
        if (request.LocationId is { } locationId) query = query.Where(message => message.LocationId == locationId);
        if (!string.IsNullOrWhiteSpace(request.MessageType)) query = query.Where(message => message.MessageType == request.MessageType);
        if (!string.IsNullOrWhiteSpace(request.FailureCategory))
        {
            string failureCode = FailureCode(request.FailureCategory);
            query = query.Where(message => message.LastError == failureCode);
        }
        if (request.FromUtc is { } from) query = query.Where(message => message.DeadLetteredAtUtc >= from);
        if (request.ToUtc is { } to) query = query.Where(message => message.DeadLetteredAtUtc <= to);
        if (request.MessageId is { } messageId) query = query.Where(message => message.Id == messageId);
        if (request.CorrelationId is { } correlationId) query = query.Where(message => message.CorrelationId == correlationId);
        if (!string.IsNullOrWhiteSpace(request.TraceId)) query = query.Where(message => message.TraceId == request.TraceId);

        int total = await query.CountAsync(cancellationToken);
        List<OutboxMessage> messages = await query.AsNoTracking()
            .OrderByDescending(message => message.DeadLetteredAtUtc ?? message.LastAttemptAtUtc ?? message.CreatedAtUtc)
            .ThenByDescending(message => message.Id)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .ToListAsync(cancellationToken);
        activity?.SetTag("purpleglass.result_count", messages.Count);
        return new(messages.Select(ToSummary).ToArray(), request.Page, request.PageSize, total);
    }

    public async Task<DeadLetterDetail?> GetAsync(Guid tenantId, IReadOnlySet<Guid> authorizedLocationIds, Guid messageId, CancellationToken cancellationToken)
    {
        using Activity? activity = PurpleGlassTelemetry.Eventing.StartActivity("deadletter.get");
        activity?.SetTag("messaging.message.id", messageId);
        OutboxMessage? message = await Scoped(tenantId, authorizedLocationIds).AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == messageId
                && (candidate.Status == OutboxMessage.DeadLetterStatus || candidate.RecoveryCount > 0), cancellationToken);
        return message is null ? null : new(ToSummary(message), message.CausationId, message.TraceParent,
            message.Producer, message.DataClassification,
            message.LastRecoveryCorrelationId, message.Status == OutboxMessage.DeadLetterStatus);
    }

    public async Task<DeadLetterRecoveryResult> RequeueAsync(
        Guid tenantId, IReadOnlySet<Guid> authorizedLocationIds, Guid messageId,
        string actorId, Guid recoveryCorrelationId, CancellationToken cancellationToken)
    {
        using Activity? activity = PurpleGlassTelemetry.Eventing.StartActivity("deadletter.requeue");
        activity?.SetTag("messaging.message.id", messageId);
        DateTimeOffset now = timeProvider.GetUtcNow();
        string safeActor = actorId[..Math.Min(actorId.Length, 200)];
        int updated = await Scoped(tenantId, authorizedLocationIds)
            .Where(message => message.Id == messageId && message.Status == OutboxMessage.DeadLetterStatus)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.Status, OutboxMessage.PendingStatus)
                .SetProperty(message => message.NextAttemptAtUtc, now)
                .SetProperty(message => message.LastRecoveredAtUtc, now)
                .SetProperty(message => message.LastRecoveredBy, safeActor)
                .SetProperty(message => message.LastRecoveryCorrelationId, recoveryCorrelationId)
                .SetProperty(message => message.RecoveryCount, message => message.RecoveryCount + 1)
                .SetProperty(message => message.LeaseId, (Guid?)null)
                .SetProperty(message => message.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken);
        if (updated == 1)
        {
            PurpleGlassTelemetry.DeadLettersRequeued.Add(1);
            return DeadLetterRecoveryResult.Recovered;
        }

        bool exists = await Scoped(tenantId, authorizedLocationIds).AnyAsync(message => message.Id == messageId, cancellationToken);
        PurpleGlassTelemetry.DeadLetterRequeueFailures.Add(1, new KeyValuePair<string, object?>("result", exists ? "not_dead_lettered" : "not_found"));
        return exists ? DeadLetterRecoveryResult.NotDeadLettered : DeadLetterRecoveryResult.NotFound;
    }

    private IQueryable<OutboxMessage> Scoped(Guid tenantId, IReadOnlySet<Guid> locations) =>
        dbContext.OutboxMessages.Where(message => message.TenantId == tenantId && locations.Contains(message.LocationId));

    private static DeadLetterSummary ToSummary(OutboxMessage message) => new(
        message.Id, message.TenantId, message.LocationId, message.MessageType, message.Status,
        message.Attempts, message.CreatedAtUtc, message.LastAttemptAtUtc, message.DeadLetteredAtUtc,
        message.CorrelationId, message.TraceId, FailureCategory(message.LastError), FailureSummary(message.LastError),
        message.RecoveryCount, message.LastRecoveredAtUtc);

    private static string FailureCategory(string? code) => code switch
    {
        "timeout" => "Timeout",
        "cancelled" => "Cancelled",
        "dependency_unavailable" => "DependencyUnavailable",
        "validation" => "Validation",
        "authentication" => "Authentication",
        "authorization" => "Authorization",
        "conflict" => "Conflict",
        "network" => "Network",
        _ => "Unhandled"
    };

    private static string FailureCode(string category) => category.Trim().ToLowerInvariant() switch
    {
        "timeout" => "timeout",
        "cancelled" => "cancelled",
        "dependencyunavailable" => "dependency_unavailable",
        "validation" => "validation",
        "authentication" => "authentication",
        "authorization" => "authorization",
        "conflict" => "conflict",
        "network" => "network",
        _ => "operation_failed"
    };

    private static string FailureSummary(string? code) => FailureCategory(code) switch
    {
        "Timeout" => "The operation timed out.",
        "Cancelled" => "The operation was cancelled.",
        "DependencyUnavailable" => "A required dependency was unavailable.",
        "Validation" => "The operation failed validation.",
        "Authentication" => "A dependency rejected authentication.",
        "Authorization" => "A dependency rejected authorization.",
        "Conflict" => "The operation encountered a state conflict.",
        "Network" => "A network operation failed.",
        _ => "The operation failed with a sanitized internal error."
    };

    private static void Validate(DeadLetterQuery request)
    {
        if (request.TenantId == Guid.Empty) throw new ArgumentException("TenantId is required.");
        if (request.AuthorizedLocationIds.Count == 0) throw new ArgumentException("At least one authorized location is required.");
        if (request.LocationId is { } location && !request.AuthorizedLocationIds.Contains(location)) throw new UnauthorizedAccessException();
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.PageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.PageSize, 100);
    }
}
