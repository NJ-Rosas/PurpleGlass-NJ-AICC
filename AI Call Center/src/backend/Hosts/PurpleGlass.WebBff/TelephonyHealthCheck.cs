using Microsoft.Extensions.Diagnostics.HealthChecks;
using PurpleGlass.Modules.CallManagement.Application;

namespace PurpleGlass.WebBff;

public sealed class TelephonyHealthCheck(ITelephonyProvider provider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        TelephonyProviderStatus status = provider.Status;
        IReadOnlyDictionary<string, object> data = new Dictionary<string, object>
        {
            ["provider"] = provider.Name,
            ["state"] = status.State,
        };
        HealthCheckResult result = !status.Enabled
            ? HealthCheckResult.Healthy("Telephony provider is disabled.", data)
            : status.Configured
                ? HealthCheckResult.Healthy("Telephony provider is configured.", data)
                : HealthCheckResult.Degraded("Telephony provider is misconfigured.", data: data);
        return Task.FromResult(result);
    }
}
