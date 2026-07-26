using PurpleGlass.Modules.Audit.Domain;

namespace PurpleGlass.Modules.Audit.Application;

public sealed class SecurityAuditService(IAuditWriter writer, TimeProvider timeProvider)
{
    public async Task WriteAsync(
        Guid tenantId,
        Guid locationId,
        string actorId,
        string action,
        string resourceType,
        string resourceId,
        string decision,
        string reasonCode,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        writer.Add(AuditRecord.Create(
            tenantId, locationId, Sanitize(actorId, 200), Sanitize(action, 160),
            Sanitize(resourceType, 100), Sanitize(resourceId, 200),
            $"{Sanitize(decision, 32)}:{Sanitize(reasonCode, 47)}", correlationId,
            timeProvider.GetUtcNow()));
        await writer.SaveChangesAsync(cancellationToken);
    }

    private static string Sanitize(string value, int maximumLength)
    {
        string sanitized = new(value.Where(character => !char.IsControl(character)).ToArray());
        return sanitized.Length <= maximumLength ? sanitized : sanitized[..maximumLength];
    }
}
