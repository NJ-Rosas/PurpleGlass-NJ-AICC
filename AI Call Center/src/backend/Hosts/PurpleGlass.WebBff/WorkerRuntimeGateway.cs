using System.Threading.Channels;
using PurpleGlass.Modules.CallManagement.Application;

namespace PurpleGlass.WebBff;

public sealed class WorkerRuntimeOptions
{
    public const string SectionName = "WorkerRuntime";
    public string ReadyUrl { get; init; } = string.Empty;
    public int ReadyTimeoutSeconds { get; init; } = 75;
    public int PollMilliseconds { get; init; } = 1000;
}

public sealed partial class WorkerRuntimeGateway(
    IHttpClientFactory clients,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<WorkerRuntimeGateway> logger) : BackgroundService
{
    private readonly WorkerRuntimeOptions settings =
        configuration.GetSection(WorkerRuntimeOptions.SectionName).Get<WorkerRuntimeOptions>() ?? new();
    private readonly Channel<bool> wakeRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });

    public void RequestWake() => wakeRequests.Writer.TryWrite(true);

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(settings.ReadyUrl, UriKind.Absolute, out Uri? readyUrl)
            || readyUrl.Scheme != Uri.UriSchemeHttps)
            throw Unavailable();

        TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(settings.ReadyTimeoutSeconds, 5, 120));
        DateTimeOffset deadline = timeProvider.GetUtcNow() + timeout;
        using HttpClient client = clients.CreateClient(nameof(WorkerRuntimeGateway));
        while (timeProvider.GetUtcNow() < deadline)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(readyUrl, cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException exception)
            {
                LogWakeAttemptFailed(logger, exception);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                LogWakeAttemptTimedOut(logger);
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(Math.Clamp(settings.PollMilliseconds, 250, 5000)),
                timeProvider,
                cancellationToken);
        }

        throw Unavailable();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (bool _ in wakeRequests.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ReadyTimeoutSeconds, 5, 120)));
                await EnsureReadyAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                LogBackgroundWakeFailed(logger, exception);
            }
        }
    }

    private static CallApplicationException Unavailable() => new(
        "telephony_runtime_unavailable",
        "The telephony worker did not become ready within the bounded wake period.");

    [LoggerMessage(EventId = 301, Level = LogLevel.Debug,
        Message = "Telephony worker readiness attempt failed; a bounded retry will follow.")]
    private static partial void LogWakeAttemptFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 302, Level = LogLevel.Debug,
        Message = "Telephony worker readiness attempt timed out; a bounded retry will follow.")]
    private static partial void LogWakeAttemptTimedOut(ILogger logger);

    [LoggerMessage(EventId = 303, Level = LogLevel.Warning,
        Message = "Background telephony worker wake did not reach readiness.")]
    private static partial void LogBackgroundWakeFailed(ILogger logger, Exception exception);
}
