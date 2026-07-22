using PurpleGlass.Adapters.AI.Mock;
using PurpleGlass.Adapters.Speech.Mock;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Infrastructure;

namespace PurpleGlass.CallOrchestrator.Worker;

public static class CallOrchestratorServiceCollectionExtensions
{
    public static IServiceCollection AddCallOrchestrator(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        CallOrchestratorOptions options = configuration
            .GetRequiredSection(CallOrchestratorOptions.SectionName)
            .Get<CallOrchestratorOptions>()
            ?? throw new InvalidOperationException("CallOrchestrator configuration is required.");
        options.Validate();

        services.AddCallManagementInfrastructure(connectionString);
        services.AddConversationInfrastructure(connectionString);
        services.AddSingleton(options);
        services.AddSingleton(options.Conversation);
        services.AddSingleton(options.MockAi);
        services.AddSingleton(options.MockSpeech);
        services.AddSingleton<IAiConversationRuntime, MockAiConversationRuntime>();
        services.AddSingleton<ISpeechRecognizer, MockSpeechRecognizer>();
        services.AddSingleton<ISpeechSynthesizer, MockSpeechSynthesizer>();
        services.AddScoped<CallOrchestrationService>();
        services.AddSingleton<OrchestratorHeartbeat>();
        services.AddHostedService<HeartbeatWorker>();
        services.AddSingleton(new DatabaseReadinessOptions(connectionString));
        services.AddHealthChecks()
            .AddCheck<DatabaseReadinessHealthCheck>("postgres", tags: ["ready"])
            .AddCheck<AdapterReadinessHealthCheck>("adapters", tags: ["ready"])
            .AddCheck<OrchestratorLivenessHealthCheck>("orchestrator", tags: ["live"]);
        return services;
    }
}
