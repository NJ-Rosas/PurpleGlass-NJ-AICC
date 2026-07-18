using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using MQTTnet;

namespace PurpleGlass.WebBff;

public sealed record RealtimeEvent(string EventType, string Payload);

public sealed class RealtimeEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<RealtimeEvent>> subscribers = new();

    public RealtimeSubscription Subscribe()
    {
        Guid id = Guid.NewGuid();
        Channel<RealtimeEvent> channel = Channel.CreateBounded<RealtimeEvent>(new BoundedChannelOptions(20)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        subscribers[id] = channel;
        return new RealtimeSubscription(id, channel.Reader, Remove);
    }

    public void Publish(RealtimeEvent realtimeEvent)
    {
        foreach (Channel<RealtimeEvent> subscriber in subscribers.Values)
        {
            _ = subscriber.Writer.TryWrite(realtimeEvent);
        }
    }

    private void Remove(Guid id)
    {
        if (subscribers.TryRemove(id, out Channel<RealtimeEvent>? channel))
        {
            channel.Writer.TryComplete();
        }
    }
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
            eventHub.Publish(new RealtimeEvent(args.ApplicationMessage.Topic.Split('/').Last(), payload));
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
}
