using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using MQTTnet;

namespace PurpleGlass.WebBff;

public sealed record RealtimeEvent(Guid TenantId, string EventType, string Payload)
{
    public static bool TryCreate(string topic, string payload, out RealtimeEvent? realtimeEvent)
    {
        string[] segments = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is ["pg", _, "v1", "tenants", string tenantId, "events", string eventType]
            && Guid.TryParse(tenantId, out Guid parsedTenantId)
            && !string.IsNullOrWhiteSpace(eventType))
        {
            realtimeEvent = new RealtimeEvent(parsedTenantId, eventType, payload);
            return true;
        }

        realtimeEvent = null;
        return false;
    }
}

public sealed class RealtimeEventHub
{
    private readonly ConcurrentDictionary<Guid, Subscriber> subscribers = new();

    public RealtimeSubscription Subscribe(Guid tenantId)
    {
        Guid id = Guid.NewGuid();
        Channel<RealtimeEvent> channel = Channel.CreateBounded<RealtimeEvent>(new BoundedChannelOptions(20)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        subscribers[id] = new Subscriber(tenantId, channel);
        return new RealtimeSubscription(id, channel.Reader, Remove);
    }

    public void Publish(RealtimeEvent realtimeEvent)
    {
        foreach (Subscriber subscriber in subscribers.Values)
        {
            if (subscriber.TenantId == realtimeEvent.TenantId)
            {
                _ = subscriber.Channel.Writer.TryWrite(realtimeEvent);
            }
        }
    }

    private void Remove(Guid id)
    {
        if (subscribers.TryRemove(id, out Subscriber? subscriber))
        {
            subscriber.Channel.Writer.TryComplete();
        }
    }

    private sealed record Subscriber(Guid TenantId, Channel<RealtimeEvent> Channel);
}

public sealed class RealtimeSubscription(
    Guid id,
    ChannelReader<RealtimeEvent> reader,
    Action<Guid> remove) : IAsyncDisposable
{
    public ChannelReader<RealtimeEvent> Reader { get; } = reader;

    public ValueTask DisposeAsync()
    {
        remove(id);
        return ValueTask.CompletedTask;
    }
}

public sealed partial class MqttRealtimeSubscriber(
    IConfiguration configuration,
    RealtimeEventHub eventHub,
    ILogger<MqttRealtimeSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new MqttClientFactory();
        using IMqttClient client = factory.CreateMqttClient();
        client.ApplicationMessageReceivedAsync += args =>
        {
            string payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload.ToArray());
            if (RealtimeEvent.TryCreate(args.ApplicationMessage.Topic, payload, out RealtimeEvent? realtimeEvent))
            {
                eventHub.Publish(realtimeEvent!);
            }
            else
            {
                LogInvalidTopic(logger, args.ApplicationMessage.Topic);
            }

            return Task.CompletedTask;
        };

        var options = new MqttClientOptionsBuilder()
            .WithClientId($"purpleglass-bff-{Environment.ProcessId}")
            .WithTcpServer(configuration["Mqtt:Host"] ?? "localhost", configuration.GetValue("Mqtt:Port", 1883))
            .WithCleanSession()
            .Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    _ = await client.ConnectAsync(options, stoppingToken);
                    var subscription = new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(configuration["Mqtt:Topic"] ?? "pg/local/v1/tenants/+/events/+")
                        .Build();
                    _ = await client.SubscribeAsync(subscription, stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSubscriptionFailure(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "The MQTT realtime subscription cycle failed.")]
    private static partial void LogSubscriptionFailure(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Ignored realtime message with invalid topic {Topic}.")]
    private static partial void LogInvalidTopic(ILogger logger, string topic);
}
