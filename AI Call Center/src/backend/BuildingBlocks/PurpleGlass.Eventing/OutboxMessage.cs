using System.Diagnostics;

namespace PurpleGlass.Eventing;

public sealed class OutboxMessage
{
    public const string PendingStatus = "Pending";
    public const string ProcessingStatus = "Processing";
    public const string PublishedStatus = "Published";
    public const string DeadLetterStatus = "DeadLetter";

    private OutboxMessage()
    {
    }

    private OutboxMessage(
        Guid id,
        Guid tenantId,
        Guid locationId,
        string topic,
        string messageType,
        string payload,
        Guid correlationId,
        DateTimeOffset occurredAtUtc,
        int schemaVersion,
        Guid? causationId,
        string? traceId,
        string? traceParent,
        string? traceState,
        string producer,
        string dataClassification)
    {
        Id = id;
        TenantId = tenantId;
        LocationId = locationId;
        Topic = topic;
        MessageType = messageType;
        Payload = payload;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
        SchemaVersion = schemaVersion;
        CausationId = causationId;
        TraceId = traceId;
        TraceParent = traceParent;
        TraceState = traceState;
        Producer = producer;
        DataClassification = dataClassification;
        Status = PendingStatus;
        CreatedAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid LocationId { get; private set; }

    public string Topic { get; private set; } = string.Empty;

    public string MessageType { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public Guid CorrelationId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public int SchemaVersion { get; private set; }

    public Guid? CausationId { get; private set; }

    public string? TraceId { get; private set; }
    public string? TraceParent { get; private set; }
    public string? TraceState { get; private set; }

    public string Producer { get; private set; } = string.Empty;

    public string DataClassification { get; private set; } = string.Empty;

    public string Status { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public DateTimeOffset? LastAttemptAtUtc { get; private set; }

    public DateTimeOffset? DeadLetteredAtUtc { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public Guid? LeaseId { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public int RecoveryCount { get; private set; }

    public DateTimeOffset? LastRecoveredAtUtc { get; private set; }

    public string? LastRecoveredBy { get; private set; }

    public Guid? LastRecoveryCorrelationId { get; private set; }

    public static OutboxMessage Create(
        Guid tenantId,
        Guid locationId,
        string topic,
        string messageType,
        string payload,
        Guid correlationId,
        DateTimeOffset occurredAtUtc,
        int schemaVersion = 1,
        Guid? causationId = null,
        string? traceId = null,
        string producer = "purpleglass-platform",
        string dataClassification = "internal",
        string? traceParent = null,
        string? traceState = null)
    {
        Activity? current = Activity.Current;
        traceId ??= current?.TraceId.ToHexString();
        traceParent ??= current?.Id;
        traceState ??= current?.TraceStateString;
        return new(
            Guid.NewGuid(),
            tenantId,
            locationId,
            topic,
            messageType,
            payload,
            correlationId,
            occurredAtUtc,
            schemaVersion,
            causationId,
            traceId,
            traceParent,
            traceState,
            producer,
            dataClassification);
    }

    public void Claim(Guid leaseId, DateTimeOffset leaseExpiresAtUtc)
    {
        if (Status is not PendingStatus and not ProcessingStatus)
        {
            throw new InvalidOperationException($"An outbox message in {Status} cannot be claimed.");
        }

        LeaseId = leaseId;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        Status = ProcessingStatus;
    }

    public void MarkPublished(Guid leaseId, DateTimeOffset publishedAtUtc)
    {
        EnsureLease(leaseId);
        PublishedAtUtc = publishedAtUtc;
        LastAttemptAtUtc = publishedAtUtc;
        Status = PublishedStatus;
        NextAttemptAtUtc = null;
        Attempts++;
        ClearLease();
    }

    public void MarkFailed(
        Guid leaseId,
        string error,
        DateTimeOffset failedAtUtc,
        int maximumAttempts,
        TimeSpan initialRetryDelay,
        TimeSpan maximumRetryDelay)
    {
        EnsureLease(leaseId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRetryDelay, initialRetryDelay);

        LastError = error[..Math.Min(error.Length, 1_000)];
        LastAttemptAtUtc = failedAtUtc;
        Attempts++;
        if (Attempts >= maximumAttempts)
        {
            Status = DeadLetterStatus;
            NextAttemptAtUtc = null;
            DeadLetteredAtUtc = failedAtUtc;
        }
        else
        {
            double delaySeconds = Math.Min(
                maximumRetryDelay.TotalSeconds,
                initialRetryDelay.TotalSeconds * Math.Pow(2, Attempts - 1));
            Status = PendingStatus;
            NextAttemptAtUtc = failedAtUtc.AddSeconds(delaySeconds);
        }

        ClearLease();
    }

    public void Requeue(string actorId, Guid recoveryCorrelationId, DateTimeOffset recoveredAtUtc)
    {
        if (Status != DeadLetterStatus)
            throw new InvalidOperationException("Only a dead-lettered outbox message can be requeued.");
        if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentException("An actor identifier is required.", nameof(actorId));
        if (recoveryCorrelationId == Guid.Empty) throw new ArgumentException("A recovery correlation identifier is required.", nameof(recoveryCorrelationId));

        Status = PendingStatus;
        NextAttemptAtUtc = recoveredAtUtc;
        LastRecoveredAtUtc = recoveredAtUtc;
        LastRecoveredBy = actorId[..Math.Min(actorId.Length, 200)];
        LastRecoveryCorrelationId = recoveryCorrelationId;
        RecoveryCount++;
        ClearLease();
    }

    private void EnsureLease(Guid leaseId)
    {
        if (Status != ProcessingStatus || LeaseId != leaseId)
        {
            throw new InvalidOperationException("The outbox message is not owned by the supplied lease.");
        }
    }

    private void ClearLease()
    {
        LeaseId = null;
        LeaseExpiresAtUtc = null;
    }
}
