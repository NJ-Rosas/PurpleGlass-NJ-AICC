namespace PurpleGlass.Eventing;

public sealed class OutboxMessage
{
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
        Producer = producer;
        DataClassification = dataClassification;
        Status = "Pending";
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

    public string Producer { get; private set; } = string.Empty;

    public string DataClassification { get; private set; } = string.Empty;

    public string Status { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

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
        string dataClassification = "internal") =>
        new(
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
            producer,
            dataClassification);

    public void MarkPublished(DateTimeOffset publishedAtUtc)
    {
        PublishedAtUtc = publishedAtUtc;
        Status = "Published";
        NextAttemptAtUtc = null;
        LastError = null;
        Attempts++;
    }

    public void MarkFailed(string error, DateTimeOffset? nextAttemptAtUtc = null)
    {
        LastError = error[..Math.Min(error.Length, 1_000)];
        Attempts++;
        Status = "Pending";
        NextAttemptAtUtc = nextAttemptAtUtc;
    }
}
