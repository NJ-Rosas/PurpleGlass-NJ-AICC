namespace PurpleGlass.Modules.Audit.Domain;

public sealed class AuditRecord
{
    private AuditRecord()
    {
    }

    private AuditRecord(
        Guid id,
        Guid tenantId,
        Guid locationId,
        string actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        Guid correlationId,
        DateTimeOffset occurredAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        LocationId = locationId;
        ActorId = actorId;
        Action = action;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Outcome = outcome;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid LocationId { get; private set; }

    public string ActorId { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;

    public string ResourceType { get; private set; } = string.Empty;

    public string ResourceId { get; private set; } = string.Empty;

    public string Outcome { get; private set; } = string.Empty;

    public Guid CorrelationId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static AuditRecord Create(
        Guid tenantId,
        Guid locationId,
        string actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        Guid correlationId,
        DateTimeOffset occurredAtUtc) =>
        new(
            Guid.NewGuid(),
            tenantId,
            locationId,
            actorId,
            action,
            resourceType,
            resourceId,
            outcome,
            correlationId,
            occurredAtUtc);
}
