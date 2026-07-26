using Microsoft.Extensions.Diagnostics.HealthChecks;
using PurpleGlass.Modules.CallManagement.Application;

namespace PurpleGlass.WebBff;

public sealed class TelephonyHealthCheck(
    ITelephonyProvider provider,
    RealtimeVoiceRuntimeStatus voiceRuntime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        TelephonyProviderStatus status = provider.Status;
        bool requiresRealtimeVoice = status.Enabled
            && provider.Name.Equals("Twilio", StringComparison.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, object> data = new Dictionary<string, object>
        {
            ["provider"] = provider.Name,
            ["state"] = status.State,
            ["voiceState"] = voiceRuntime.State,
        };
        HealthCheckResult result = !status.Enabled
            ? HealthCheckResult.Healthy("Telephony provider is disabled.", data)
            : status.Configured && (!requiresRealtimeVoice || voiceRuntime.Ready)
                ? HealthCheckResult.Healthy("Telephony provider is configured.", data)
                : HealthCheckResult.Degraded("Telephony or realtime voice is misconfigured.", data: data);
        return Task.FromResult(result);
    }
}
