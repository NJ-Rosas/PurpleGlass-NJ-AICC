using System.Diagnostics;
using Microsoft.Extensions.Options;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Observability;

namespace PurpleGlass.Integrations.Worker;

public sealed class TelephonyTransportWorker(
    TelephonyDispatchProcessor processor,
    IOptions<TelephonyRuntimeOptions> options,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly TelephonyRuntimeOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed = await processor.ProcessNextAsync(stoppingToken);
            if (!processed)
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Clamp(settings.PollMilliseconds, 100, 10_000)),
                    timeProvider,
                    stoppingToken);
        }
    }
}

public sealed partial class TelephonyDispatchProcessor(
    IServiceScopeFactory scopeFactory,
    ITelephonyProvider provider,
    IOptions<TelephonyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<TelephonyDispatchProcessor> logger)
{
    private const int CompletionAttempts = 3;
    private readonly TelephonyRuntimeOptions settings = options.Value;

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        TelephonyDispatch? dispatch;
        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            CallManagementService calls = scope.ServiceProvider.GetRequiredService<CallManagementService>();
            try
            {
                dispatch = await calls.BeginNextTelephonyDispatchAsync(cancellationToken);
            }
            catch (CallApplicationException exception) when (exception.Code == "concurrency_conflict")
            {
                LogDispatchClaimConflict(logger, exception);
                return true;
            }
        }

        if (dispatch is null) return false;

        TimeSpan maximumQueueAge = TimeSpan.FromSeconds(Math.Clamp(settings.MaximumQueueAgeSeconds, 30, 900));
        if (dispatch.OperationType == "StartOutbound"
            && timeProvider.GetUtcNow() - dispatch.CreatedAtUtc > maximumQueueAge)
        {
            _ = await CompleteWithRecoveryAsync(
                dispatch, null, "telephony_runtime_unavailable", cancellationToken);
            LogStaleDispatchRejected(logger, dispatch.OperationId, dispatch.CallId,
                dispatch.TenantId, dispatch.LocationId, maximumQueueAge.TotalSeconds);
            return true;
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
                new Uri(publicBaseUrl, $"/telephony/twilio/answer?operationId={dispatch.OperationId:D}"),
                new Uri(publicBaseUrl, $"/telephony/twilio/status?operationId={dispatch.OperationId:D}"));
            OutboundCallResult result = await provider.StartOutboundCallAsync(request, cancellationToken);
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
                : await provider.HangupCallAsync(dispatch.ProviderCallId, cancellationToken);
            error = result.SafeErrorCode;
        }

        bool persisted = await CompleteWithRecoveryAsync(dispatch, providerCallId, error, cancellationToken);
        string resultCode = persisted ? error ?? "succeeded" : "concurrency_retry_exhausted";
        LogDispatchCompleted(logger, dispatch.OperationId, dispatch.CallId, dispatch.TenantId,
            dispatch.LocationId, dispatch.Provider, providerCallId ?? dispatch.ProviderCallId, resultCode);
        if (error is not null || !persisted)
        {
            activity?.SetStatus(ActivityStatusCode.Error, resultCode);
            PurpleGlassTelemetry.TelephonyProviderErrors.Add(1,
                new KeyValuePair<string, object?>("provider", provider.Name),
                new KeyValuePair<string, object?>("result", resultCode));
            LogDispatchFailed(logger, dispatch.OperationId, resultCode);
        }
        return true;
    }

    private async Task<bool> CompleteWithRecoveryAsync(
        TelephonyDispatch dispatch,
        string? providerCallId,
        string? safeErrorCode,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= CompletionAttempts; attempt++)
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            CallManagementService calls = scope.ServiceProvider.GetRequiredService<CallManagementService>();
            try
            {
                await calls.CompleteTelephonyDispatchAsync(
                    dispatch.OperationId, providerCallId, safeErrorCode, cancellationToken);
                if (attempt > 1)
                    LogDispatchConverged(logger, dispatch.OperationId, dispatch.CallId, attempt);
                return true;
            }
            catch (CallApplicationException exception) when (exception.Code == "concurrency_conflict")
            {
                if (attempt == CompletionAttempts)
                {
                    LogDispatchConcurrencyExhausted(
                        logger, dispatch.OperationId, dispatch.CallId, dispatch.TenantId,
                        dispatch.LocationId, attempt, exception);
                    return false;
                }

                LogDispatchConcurrencyRetry(
                    logger, dispatch.OperationId, dispatch.CallId, dispatch.TenantId,
                    dispatch.LocationId, attempt, exception);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), timeProvider, cancellationToken);
            }
        }
        return false;
    }

    [LoggerMessage(EventId = 19, Level = LogLevel.Information,
        Message = "Telephony dispatch claim lost a concurrency race; the worker will continue.")]
    private static partial void LogDispatchClaimConflict(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning,
        Message = "Telephony operation {OperationId} failed with safe category {ErrorCode}.")]
    private static partial void LogDispatchFailed(ILogger logger, Guid operationId, string errorCode);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information,
        Message = "Telephony operation completed; OperationId={OperationId}, CallId={CallId}, TenantId={TenantId}, LocationId={LocationId}, Provider={Provider}, ProviderCallId={ProviderCallId}, Result={Result}.")]
    private static partial void LogDispatchCompleted(ILogger logger, Guid operationId, Guid callId,
        Guid tenantId, Guid locationId, string provider, string? providerCallId, string result);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning,
        Message = "Telephony completion concurrency conflict; OperationId={OperationId}, CallId={CallId}, TenantId={TenantId}, LocationId={LocationId}, RetryAttempt={RetryAttempt}.")]
    private static partial void LogDispatchConcurrencyRetry(ILogger logger, Guid operationId, Guid callId,
        Guid tenantId, Guid locationId, int retryAttempt, Exception exception);

    [LoggerMessage(EventId = 23, Level = LogLevel.Information,
        Message = "Telephony completion converged after reloading durable state; OperationId={OperationId}, CallId={CallId}, RetryAttempt={RetryAttempt}.")]
    private static partial void LogDispatchConverged(ILogger logger, Guid operationId, Guid callId, int retryAttempt);

    [LoggerMessage(EventId = 24, Level = LogLevel.Error,
        Message = "Telephony completion concurrency retry budget exhausted; OperationId={OperationId}, CallId={CallId}, TenantId={TenantId}, LocationId={LocationId}, RetryAttempt={RetryAttempt}. The worker will continue without repeating the provider action.")]
    private static partial void LogDispatchConcurrencyExhausted(ILogger logger, Guid operationId, Guid callId,
        Guid tenantId, Guid locationId, int retryAttempt, Exception exception);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning,
        Message = "Stale outbound telephony operation rejected without contacting the provider; OperationId={OperationId}, CallId={CallId}, TenantId={TenantId}, LocationId={LocationId}, MaximumQueueAgeSeconds={MaximumQueueAgeSeconds}, Result=telephony_runtime_unavailable.")]
    private static partial void LogStaleDispatchRejected(ILogger logger, Guid operationId, Guid callId,
        Guid tenantId, Guid locationId, double maximumQueueAgeSeconds);
}
