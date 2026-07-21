using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PurpleGlass.Modules.CallManagement.Application;

namespace PurpleGlass.Modules.CallManagement.Infrastructure;

public static class CallManagementInfrastructureExtensions
{
    public static IServiceCollection AddCallManagementInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<CallManagementDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<ICallStore>(provider => provider.GetRequiredService<CallManagementDbContext>());
        services.AddScoped<CallManagementService>();
        services.AddScoped<PurpleGlass.Modules.CallManagement.Contracts.ICallEligibilityQuery>(provider => provider.GetRequiredService<CallManagementService>());
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
