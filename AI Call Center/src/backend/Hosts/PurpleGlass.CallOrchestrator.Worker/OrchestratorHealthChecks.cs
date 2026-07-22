using System.Threading;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.CallOrchestrator.Worker;

public sealed record DatabaseReadinessOptions(string ConnectionString);

public sealed class DatabaseReadinessHealthCheck(DatabaseReadinessOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
        }
    }
}

public sealed class AdapterReadinessHealthCheck(
    IAiConversationRuntime ai,
    ISpeechRecognizer recognizer,
    ISpeechSynthesizer synthesizer,
    CallOrchestratorOptions options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        bool valid = ai.AdapterKey == options.Conversation.AiAdapterKey
            && recognizer.AdapterKey == options.Conversation.SpeechRecognitionAdapterKey
            && synthesizer.AdapterKey == options.Conversation.SpeechSynthesisAdapterKey;
        return Task.FromResult(valid
            ? HealthCheckResult.Healthy("Provider-neutral adapters are registered.")
            : HealthCheckResult.Unhealthy("Configured adapters are not registered."));
    }
}

public sealed class OrchestratorHeartbeat(TimeProvider timeProvider)
{
    private long lastPulseTimestamp = timeProvider.GetTimestamp();

    public void Pulse() => Interlocked.Exchange(ref lastPulseTimestamp, timeProvider.GetTimestamp());

    public TimeSpan Age => timeProvider.GetElapsedTime(Interlocked.Read(ref lastPulseTimestamp));
}

public sealed class OrchestratorLivenessHealthCheck(OrchestratorHeartbeat heartbeat) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(heartbeat.Age <= TimeSpan.FromSeconds(10)
            ? HealthCheckResult.Healthy("The orchestrator heartbeat is current.")
            : HealthCheckResult.Unhealthy("The orchestrator heartbeat is stale."));
}

public sealed class HeartbeatWorker(OrchestratorHeartbeat heartbeat, TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Pulse();
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
        }
    }
}
