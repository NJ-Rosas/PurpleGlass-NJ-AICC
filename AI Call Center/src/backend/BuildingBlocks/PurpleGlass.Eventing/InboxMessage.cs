namespace PurpleGlass.Eventing;

public sealed class InboxMessage
{
    private InboxMessage()
    {
    }

    private InboxMessage(
        Guid id,
        string consumerName,
        Guid messageId,
        Guid tenantId,
        DateTimeOffset receivedAtUtc)
    {
        Id = id;
        ConsumerName = consumerName;
        MessageId = messageId;
        TenantId = tenantId;
        ReceivedAtUtc = receivedAtUtc;
    }

    public Guid Id { get; private set; }

    public string ConsumerName { get; private set; } = string.Empty;

    public Guid MessageId { get; private set; }

    public Guid TenantId { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public static InboxMessage Create(
        string consumerName,
        Guid messageId,
        Guid tenantId,
        DateTimeOffset receivedAtUtc) =>
        new(Guid.NewGuid(), consumerName, messageId, tenantId, receivedAtUtc);

    public void MarkProcessed(DateTimeOffset processedAtUtc)
    {
        if (ProcessedAtUtc.HasValue) throw new InvalidOperationException("The inbox message was already processed.");
        ProcessedAtUtc = processedAtUtc;
    }
}
