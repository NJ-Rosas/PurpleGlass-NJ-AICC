using Microsoft.Extensions.Diagnostics.HealthChecks;
using PurpleGlass.Adapters.Telephony.Fake;
using PurpleGlass.Integrations.Worker;

namespace PurpleGlass.UnitTests;

public sealed class IntegrationsWorkerHealthTests
{
    [Fact]
    public async Task ReadinessStartsUnhealthyUntilWorkerCompletesConnectedCycle()
    {
        var state = new WorkerRuntimeState();
        var worker = new WorkerProcessingHealthCheck(state);
        var mqtt = new WorkerMqttHealthCheck(state);

        Assert.Equal(HealthStatus.Unhealthy, (await worker.CheckHealthAsync(new())).Status);
        Assert.Equal(HealthStatus.Unhealthy, (await mqtt.CheckHealthAsync(new())).Status);

        state.MarkStarted();
        Assert.Equal(HealthStatus.Unhealthy, (await worker.CheckHealthAsync(new())).Status);

        state.MarkCycleHealthy(true);
        Assert.Equal(HealthStatus.Healthy, (await worker.CheckHealthAsync(new())).Status);
        Assert.Equal(HealthStatus.Healthy, (await mqtt.CheckHealthAsync(new())).Status);
    }

    [Fact]
    public async Task DependencyFailureDegradesReadinessWithoutChangingProcessLivenessState()
    {
        var state = new WorkerRuntimeState();
        state.MarkStarted();
        state.MarkCycleHealthy(true);
        state.MarkCycleFailure(false);

        Assert.True(state.Started);
        Assert.Equal(HealthStatus.Unhealthy,
            (await new WorkerProcessingHealthCheck(state).CheckHealthAsync(new())).Status);
        Assert.Equal(HealthStatus.Unhealthy,
            (await new WorkerMqttHealthCheck(state).CheckHealthAsync(new())).Status);

        state.MarkCycleHealthy(true);
        Assert.Equal(HealthStatus.Healthy,
            (await new WorkerProcessingHealthCheck(state).CheckHealthAsync(new())).Status);
        Assert.Equal(HealthStatus.Healthy,
            (await new WorkerMqttHealthCheck(state).CheckHealthAsync(new())).Status);
    }

    [Fact]
    public async Task TelephonyReadinessUsesConfigurationWithoutCallingProvider()
    {
        var provider = new FakeTelephonyProvider();
        HealthCheckResult result = await new WorkerTelephonyHealthCheck(provider).CheckHealthAsync(new());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Empty(provider.Calls);
    }
}
