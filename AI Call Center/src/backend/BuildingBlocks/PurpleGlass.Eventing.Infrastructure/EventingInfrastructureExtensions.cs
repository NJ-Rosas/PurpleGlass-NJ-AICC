using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PurpleGlass.Eventing.Infrastructure;

public static class EventingInfrastructureExtensions
{
    public static IServiceCollection AddEventingInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<EventingDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<OutboxDispatcherStore>();
        services.AddScoped<InboxDeduplicationStore>();
        services.AddScoped<DeadLetterOperationsService>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
