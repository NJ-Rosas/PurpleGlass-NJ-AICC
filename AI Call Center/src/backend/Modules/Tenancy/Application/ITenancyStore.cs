using PurpleGlass.Eventing;
using PurpleGlass.Modules.Tenancy.Contracts;
using PurpleGlass.Modules.Tenancy.Domain;

namespace PurpleGlass.Modules.Tenancy.Application;

public interface ITenancyStore
{
    Task<TenantSummary?> GetSummaryAsync(
        TenantId tenantId,
        LocationId locationId,
        CancellationToken cancellationToken);

    Task<Location?> GetLocationAsync(
        TenantId tenantId,
        LocationId locationId,
        CancellationToken cancellationToken);

    void AddOutbox(OutboxMessage message);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
