using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PurpleGlass.Modules.Audit.Application;
using PurpleGlass.Modules.Tenancy.Application;

namespace PurpleGlass.Modules.Tenancy.Infrastructure;

public static class TenancyInfrastructureExtensions
{
    public static IServiceCollection AddTenancyInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<TenancyDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<ITenancyStore>(provider => provider.GetRequiredService<TenancyDbContext>());
        services.AddScoped<IAuditWriter>(provider => provider.GetRequiredService<TenancyDbContext>());
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
