using System.Text;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using PurpleGlass.Eventing;
using PurpleGlass.Eventing.Infrastructure;

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
        string host = configuration["Mqtt:Host"] ?? "localhost";
        int port = configuration.GetValue("Mqtt:Port", 1883);
        var factory = new MqttClientFactory();
        using IMqttClient client = factory.CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithClientId($"purpleglass-outbox-{Environment.MachineName}-{Environment.ProcessId}")
            .WithTcpServer(host, port)
            .WithCleanSession()
            .Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    _ = await client.ConnectAsync(options, stoppingToken);
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
            try
            {
                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(message.Topic)
                    .WithPayload(Encoding.UTF8.GetBytes(message.Payload))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(false)
                    .Build();

                _ = await client.PublishAsync(mqttMessage, cancellationToken);
                await store.MarkPublishedAsync(
                    message,
                    leaseId,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await store.MarkFailedAsync(
                    message,
                    leaseId,
                    exception.Message,
                    timeProvider.GetUtcNow(),
                    settings.MaximumAttempts,
                    settings.InitialRetryDelay,
                    settings.MaximumRetryDelay,
                    cancellationToken);
                LogMessagePublishFailure(logger, message.Id, exception);
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
