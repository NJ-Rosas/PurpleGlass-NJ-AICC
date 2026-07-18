using Microsoft.EntityFrameworkCore;
using PurpleGlass.Modules.Tenancy.Domain;

namespace PurpleGlass.Modules.Tenancy.Infrastructure;

public static class PrototypeSeed
{
    public static readonly TenantId TenantId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    public static readonly LocationId LocationId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    public static async Task ApplyAsync(TenancyDbContext dbContext, CancellationToken cancellationToken)
    {
        if (await dbContext.Tenants.AnyAsync(cancellationToken))
        {
            return;
        }

        dbContext.Tenants.Add(new Tenant(TenantId, "PurpleGlass Prototype Dental"));
        dbContext.Locations.Add(new Location(
            LocationId,
            TenantId,
            "San Juan Prototype Office",
            "America/Puerto_Rico"));

        _ = await dbContext.SaveChangesAsync(cancellationToken);
    }
}
