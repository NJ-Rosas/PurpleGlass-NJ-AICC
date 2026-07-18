using System.Text;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.Tenancy.Infrastructure;

namespace PurpleGlass.Integrations.Worker;

public sealed partial class Worker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string host = configuration["Mqtt:Host"] ?? "localhost";
        int port = configuration.GetValue("Mqtt:Port", 1883);
        var factory = new MqttClientFactory();
        using IMqttClient client = factory.CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithClientId($"purpleglass-outbox-{Environment.MachineName}")
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
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
        }
    }

    private async Task PublishBatchAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        TenancyDbContext dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        List<OutboxMessage> messages = await dbContext.OutboxMessages
            .Where(message => message.PublishedAtUtc == null && message.Attempts < 10)
            .OrderBy(message => message.OccurredAtUtc)
            .Take(20)
            .ToListAsync(cancellationToken);

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
                message.MarkPublished(timeProvider.GetUtcNow());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.MarkFailed(exception.Message);
                LogMessagePublishFailure(logger, message.Id, exception);
            }

            _ = await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "The outbox publisher cycle failed.")]
    private static partial void LogPublisherCycleFailure(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Publishing outbox message {OutboxMessageId} failed.")]
    private static partial void LogMessagePublishFailure(ILogger logger, Guid outboxMessageId, Exception exception);
}
