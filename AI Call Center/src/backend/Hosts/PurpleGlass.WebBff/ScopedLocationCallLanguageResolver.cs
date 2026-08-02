using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.Tenancy.Application;
using PurpleGlass.Modules.Tenancy.Domain;

namespace PurpleGlass.WebBff;

public sealed class ScopedLocationCallLanguageResolver(IServiceScopeFactory scopeFactory)
    : ILocationCallLanguageResolver
{
    public async Task<string?> ResolveDefaultLanguageAsync(
        Guid tenantId, Guid locationId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ITenancyStore store = scope.ServiceProvider.GetRequiredService<ITenancyStore>();
        Location? location = await store.GetLocationAsync(
            new TenantId(tenantId), new LocationId(locationId), cancellationToken);
        return location is { IsActive: true } ? location.DefaultCallLanguageCode : null;
    }
}
