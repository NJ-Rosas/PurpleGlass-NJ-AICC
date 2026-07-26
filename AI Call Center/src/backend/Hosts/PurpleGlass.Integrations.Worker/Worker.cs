using System.Text;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using PurpleGlass.Eventing;
using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Observability;
using System.Diagnostics;

namespace PurpleGlass.Integrations.Worker;

public sealed partial class Worker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    IOptions<OutboxPublisherOptions> publisherOptions,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly OutboxPublisherOptions settings = Validate(publisherOptions.Value);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MqttConnectionSettings mqttSettings = MqttConnectionSettings.From(configuration);
        var factory = new MqttClientFactory();
        using IMqttClient client = factory.CreateMqttClient();
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId($"purpleglass-outbox-{Environment.MachineName}-{Environment.ProcessId}")
            .WithTcpServer(mqttSettings.Host, mqttSettings.Port)
            .WithCleanSession();
        if (!string.IsNullOrWhiteSpace(mqttSettings.Username))
            optionsBuilder.WithCredentials(mqttSettings.Username, mqttSettings.Password!);
        if (mqttSettings.UseTls)
            optionsBuilder.WithTlsOptions(tls => tls.UseTls(true));
        var options = optionsBuilder.Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    _ = await client.ConnectAsync(options, stoppingToken);
                    PurpleGlassTelemetry.MqttReconnects.Add(1);
                }

                await PublishBatchAsync(client, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPublisherCycleFailure(logger, exception);
                await Task.Delay(settings.FailureRetryDelay, timeProvider, stoppingToken);
                continue;
            }

            await Task.Delay(settings.PollInterval, timeProvider, stoppingToken);
        }
    }

    private async Task PublishBatchAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        OutboxDispatcherStore store = scope.ServiceProvider.GetRequiredService<OutboxDispatcherStore>();
        Guid leaseId = Guid.NewGuid();
        DateTimeOffset now = timeProvider.GetUtcNow();
        IReadOnlyList<OutboxMessage> messages = await store.ClaimBatchAsync(
            leaseId,
            now,
            settings.LeaseDuration,
            settings.BatchSize,
            cancellationToken);

        foreach (OutboxMessage message in messages)
        {
            ActivityContext parent = default;
            _ = ActivityContext.TryParse(message.TraceParent, message.TraceState, true, out parent);
            using Activity? activity = PurpleGlassTelemetry.Messaging.StartActivity("mqtt.publish", ActivityKind.Producer, parent);
            activity?.SetTag("messaging.system", "mqtt");
            activity?.SetTag("messaging.message.id", message.Id);
            activity?.SetTag("purpleglass.correlation_id", message.CorrelationId);
            long started = timeProvider.GetTimestamp();
            try
            {
                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(message.Topic)
                    .WithPayload(Encoding.UTF8.GetBytes(message.Payload))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(false)
                    .WithUserProperty("message-id", Encoding.UTF8.GetBytes(message.Id.ToString("D")))
                    .WithUserProperty("correlation-id", Encoding.UTF8.GetBytes(message.CorrelationId.ToString("D")))
                    .WithUserProperty("location-id", Encoding.UTF8.GetBytes(message.LocationId.ToString("D")))
                    .WithUserProperty("traceparent", Encoding.UTF8.GetBytes(activity?.Id ?? message.TraceParent ?? string.Empty))
                    .WithUserProperty("tracestate", Encoding.UTF8.GetBytes(activity?.TraceStateString ?? message.TraceState ?? string.Empty))
                    .Build();

                _ = await client.PublishAsync(mqttMessage, cancellationToken);
                await store.MarkPublishedAsync(
                    message,
                    leaseId,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                PurpleGlassTelemetry.MqttPublished.Add(1);
                PurpleGlassTelemetry.OutboxPublishDuration.Record(timeProvider.GetElapsedTime(started).TotalMilliseconds);
                if (message.RecoveryCount > 0) PurpleGlassTelemetry.RecoveredMessagesCompleted.Add(1);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await store.MarkFailedAsync(
                    message,
                    leaseId,
                    TelemetrySanitizer.ErrorCode(exception),
                    timeProvider.GetUtcNow(),
                    settings.MaximumAttempts,
                    settings.InitialRetryDelay,
                    settings.MaximumRetryDelay,
                    cancellationToken);
                LogMessagePublishFailure(logger, message.Id, exception);
                PurpleGlassTelemetry.MqttPublishFailures.Add(1);
                PurpleGlassTelemetry.OutboxPublishDuration.Record(timeProvider.GetElapsedTime(started).TotalMilliseconds);
                activity?.SetStatus(ActivityStatusCode.Error, TelemetrySanitizer.ErrorCode(exception));
                if (message.RecoveryCount > 0 && message.Status == OutboxMessage.DeadLetterStatus)
                    PurpleGlassTelemetry.RecoveredMessagesFailed.Add(1);
                if (message.Status == OutboxMessage.DeadLetterStatus)
                {
                    LogMessageDeadLettered(logger, message.Id, message.Attempts);
                }
            }
        }
    }

    private static OutboxPublisherOptions Validate(OutboxPublisherOptions options)
    {
        options.Validate();
        return options;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "The outbox publisher cycle failed.")]
    private static partial void LogPublisherCycleFailure(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Publishing outbox message {OutboxMessageId} failed.")]
    private static partial void LogMessagePublishFailure(ILogger logger, Guid outboxMessageId, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Outbox message {OutboxMessageId} moved to dead letter after {Attempts} attempts.")]
    private static partial void LogMessageDeadLettered(ILogger logger, Guid outboxMessageId, int attempts);
}
