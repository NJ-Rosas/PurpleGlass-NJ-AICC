using System.Diagnostics;
using Microsoft.Extensions.Options;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Observability;

namespace PurpleGlass.Integrations.Worker;

public sealed partial class TelephonyTransportWorker(
    IServiceScopeFactory scopeFactory,
    ITelephonyProvider provider,
    IOptions<TelephonyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<TelephonyTransportWorker> logger) : BackgroundService
{
    private readonly TelephonyRuntimeOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            CallManagementService calls = scope.ServiceProvider.GetRequiredService<CallManagementService>();
            TelephonyDispatch? dispatch;
            try
            {
                dispatch = await calls.BeginNextTelephonyDispatchAsync(stoppingToken);
            }
            catch (CallApplicationException exception) when (exception.Code == "call_concurrency_conflict")
            {
                continue;
            }
            if (dispatch is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(settings.PollMilliseconds, 100, 10_000)), timeProvider, stoppingToken);
                continue;
            }

            using Activity? activity = PurpleGlassTelemetry.Integrations.StartActivity(
                dispatch.OperationType == "Hangup" ? "telephony.call.hangup" : "telephony.outbound.start",
                ActivityKind.Consumer);
            activity?.SetTag("purpleglass.correlation_id", dispatch.CallId);
            activity?.SetTag("purpleglass.telephony.provider", dispatch.Provider);
            string? providerCallId = null;
            string? error = null;
            if (!string.Equals(dispatch.Provider, provider.Name, StringComparison.OrdinalIgnoreCase))
            {
                error = provider.Status.Enabled ? "provider_mismatch" : "provider_disabled";
            }
            else if (!Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out Uri? publicBaseUrl)
                || publicBaseUrl.Scheme != Uri.UriSchemeHttps)
            {
                error = "public_base_url_invalid";
            }
            else if (dispatch.OperationType == "StartOutbound")
            {
                var request = new OutboundCallTransport(
                    dispatch.OperationId, dispatch.CallId, dispatch.FromNumber, dispatch.ToNumber,
                    new Uri(publicBaseUrl, $"/telephony/twilio/answer?callId={dispatch.CallId:D}"),
                    new Uri(publicBaseUrl, $"/telephony/twilio/status?operationId={dispatch.OperationId:D}"));
                OutboundCallResult result = await provider.StartOutboundCallAsync(request, stoppingToken);
                providerCallId = result.ProviderCallId;
                error = result.SafeErrorCode;
                PurpleGlassTelemetry.TelephonyOutboundCalls.Add(1,
                    new KeyValuePair<string, object?>("provider", provider.Name),
                    new KeyValuePair<string, object?>("result", result.Succeeded ? "accepted" : "failed"));
            }
            else
            {
                TelephonyProviderResult result = dispatch.ProviderCallId is null
                    ? TelephonyProviderResult.Failure("provider_identity_unavailable")
                    : await provider.HangupCallAsync(dispatch.ProviderCallId, stoppingToken);
                error = result.SafeErrorCode;
            }

            await calls.CompleteTelephonyDispatchAsync(dispatch.OperationId, providerCallId, error, stoppingToken);
            if (error is not null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, error);
                PurpleGlassTelemetry.TelephonyProviderErrors.Add(1,
                    new KeyValuePair<string, object?>("provider", provider.Name),
                    new KeyValuePair<string, object?>("result", error));
                LogDispatchFailed(logger, dispatch.OperationId, error);
            }
        }
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning,
        Message = "Telephony operation {OperationId} failed with safe category {ErrorCode}.")]
    private static partial void LogDispatchFailed(ILogger logger, Guid operationId, string errorCode);
}
