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
        DateTimeOffset occurredAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        LocationId = locationId;
        Topic = topic;
        MessageType = messageType;
        Payload = payload;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid LocationId { get; private set; }

    public string Topic { get; private set; } = string.Empty;

    public string MessageType { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public Guid CorrelationId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

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
        DateTimeOffset occurredAtUtc) =>
        new(
            Guid.NewGuid(),
            tenantId,
            locationId,
            topic,
            messageType,
            payload,
            correlationId,
            occurredAtUtc);

    public void MarkPublished(DateTimeOffset publishedAtUtc)
    {
        PublishedAtUtc = publishedAtUtc;
        LastError = null;
        Attempts++;
    }

    public void MarkFailed(string error)
    {
        LastError = error[..Math.Min(error.Length, 1_000)];
        Attempts++;
    }
}
