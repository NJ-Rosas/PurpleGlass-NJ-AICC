using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Infrastructure;

namespace PurpleGlass.Integrations.Worker;

public sealed class WorkerRuntimeState
{
    private int started;
    private int processingHealthy;
    private int mqttConnected;

    public bool Started => Volatile.Read(ref started) == 1;
    public bool ProcessingHealthy => Volatile.Read(ref processingHealthy) == 1;
    public bool MqttConnected => Volatile.Read(ref mqttConnected) == 1;

    public void MarkStarted() => Volatile.Write(ref started, 1);

    public void MarkCycleHealthy(bool connected)
    {
        Volatile.Write(ref mqttConnected, connected ? 1 : 0);
        Volatile.Write(ref processingHealthy, 1);
    }

    public void MarkCycleFailure(bool connected)
    {
        Volatile.Write(ref mqttConnected, connected ? 1 : 0);
        Volatile.Write(ref processingHealthy, 0);
    }

    public void MarkStopping()
    {
        Volatile.Write(ref processingHealthy, 0);
        Volatile.Write(ref mqttConnected, 0);
        Volatile.Write(ref started, 0);
    }
}

public sealed class WorkerDatabaseHealthCheck(CallManagementDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await dbContext.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("PostgreSQL is reachable.")
            : HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
}

public sealed class WorkerMqttHealthCheck(WorkerRuntimeState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.MqttConnected
            ? HealthCheckResult.Healthy("The worker MQTT client is connected.")
            : HealthCheckResult.Unhealthy("The worker MQTT client is not connected."));
}

public sealed class WorkerProcessingHealthCheck(WorkerRuntimeState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(!state.Started
            ? HealthCheckResult.Unhealthy("The worker is initializing or stopping.")
            : state.ProcessingHealthy
                ? HealthCheckResult.Healthy("The worker processing loop completed a successful cycle.")
                : HealthCheckResult.Unhealthy("The worker processing loop has not completed a successful cycle."));
}

public sealed class WorkerTelephonyHealthCheck(ITelephonyProvider provider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        TelephonyProviderStatus status = provider.Status;
        IReadOnlyDictionary<string, object> data = new Dictionary<string, object>
        {
            ["provider"] = provider.Name,
            ["state"] = status.State,
        };
        return Task.FromResult(!status.Enabled || status.Configured
            ? HealthCheckResult.Healthy(status.Enabled
                ? "Telephony provider is configured."
                : "Telephony provider is disabled.", data)
            : HealthCheckResult.Unhealthy("Telephony provider is not configured.", data: data));
    }
}
