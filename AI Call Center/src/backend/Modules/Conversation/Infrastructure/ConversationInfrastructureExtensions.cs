using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Modules.Conversation.Infrastructure;

public static class ConversationInfrastructureExtensions
{
    public static IServiceCollection AddConversationInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<ConversationDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IConversationStore>(provider => provider.GetRequiredService<ConversationDbContext>());
        services.AddScoped<ConversationService>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
