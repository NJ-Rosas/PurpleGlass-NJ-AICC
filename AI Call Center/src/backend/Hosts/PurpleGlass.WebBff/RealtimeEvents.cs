using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using MQTTnet;
using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Observability;
using System.Diagnostics;

namespace PurpleGlass.WebBff;

public sealed record RealtimeEvent(Guid TenantId, Guid? LocationId, Guid CorrelationId, Guid MessageId, string EventType, string Payload, string? TraceParent, string? TraceState)
{
    public RealtimeEvent(Guid tenantId, string eventType, string payload)
        : this(tenantId, null, Guid.Empty, Guid.Empty, eventType, payload, null, null) { }

    public static bool TryCreate(string topic, string payload, out RealtimeEvent? realtimeEvent) =>
        TryCreate(topic, payload, new Dictionary<string, string>(), out realtimeEvent);

    public static bool TryCreate(string topic, string payload, IReadOnlyDictionary<string, string> metadata, out RealtimeEvent? realtimeEvent)
    {
        string[] segments = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool recognizedShape =
            segments is ["pg", _, "v1", "tenants", _, "events", _]
            or ["pg", _, "v1", "tenants", _, "calls", _, "events", _];
        string? tenantId = recognizedShape ? segments[4] : null;
        string? eventType = recognizedShape ? segments[^1] : null;
        if (tenantId is not null
            && eventType is not null
            && Guid.TryParse(tenantId, out Guid parsedTenantId)
            && !string.IsNullOrWhiteSpace(eventType))
        {
            _ = Guid.TryParse(metadata.GetValueOrDefault("location-id"), out Guid locationId);
            _ = Guid.TryParse(metadata.GetValueOrDefault("correlation-id"), out Guid correlationId);
            _ = Guid.TryParse(metadata.GetValueOrDefault("message-id"), out Guid messageId);
            realtimeEvent = new RealtimeEvent(parsedTenantId, locationId == Guid.Empty ? null : locationId,
                correlationId, messageId, eventType, payload,
                metadata.GetValueOrDefault("traceparent"), metadata.GetValueOrDefault("tracestate"));
            return true;
        }

        realtimeEvent = null;
        return false;
    }
}

public sealed class RealtimeEventHub
{
    private readonly ConcurrentDictionary<Guid, Subscriber> subscribers = new();

    public RealtimeSubscription Subscribe(Guid tenantId, Guid? locationId = null)
    {
        Guid id = Guid.NewGuid();
        Channel<RealtimeEvent> channel = Channel.CreateBounded<RealtimeEvent>(new BoundedChannelOptions(20)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        subscribers[id] = new Subscriber(tenantId, locationId, channel);
        return new RealtimeSubscription(id, channel.Reader, Remove);
    }

    public void Publish(RealtimeEvent realtimeEvent)
    {
        foreach (Subscriber subscriber in subscribers.Values)
        {
            if (subscriber.TenantId == realtimeEvent.TenantId
                && (subscriber.LocationId is null || realtimeEvent.LocationId is null || subscriber.LocationId == realtimeEvent.LocationId))
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

    private sealed record Subscriber(Guid TenantId, Guid? LocationId, Channel<RealtimeEvent> Channel);
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
            Dictionary<string, string> metadata = args.ApplicationMessage.UserProperties
                .Where(property => !string.IsNullOrWhiteSpace(property.Name))
                .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => Encoding.UTF8.GetString(group.Last().ValueBuffer.Span), StringComparer.OrdinalIgnoreCase);
            string payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload.ToArray());
            if (RealtimeEvent.TryCreate(args.ApplicationMessage.Topic, payload, metadata, out RealtimeEvent? realtimeEvent))
            {
                ActivityContext parent = default;
                _ = ActivityContext.TryParse(realtimeEvent!.TraceParent, realtimeEvent.TraceState, true, out parent);
                using Activity? activity = PurpleGlassTelemetry.Messaging.StartActivity("mqtt.consume", ActivityKind.Consumer, parent);
                activity?.SetTag("messaging.system", "mqtt");
                activity?.SetTag("messaging.message.id", realtimeEvent.MessageId);
                activity?.SetTag("purpleglass.correlation_id", realtimeEvent.CorrelationId);
                PurpleGlassTelemetry.MqttReceived.Add(1);
                eventHub.Publish(realtimeEvent);
            }
            else
            {
                LogInvalidTopic(logger, args.ApplicationMessage.Topic);
            }

            return Task.CompletedTask;
        };

        MqttConnectionSettings settings = MqttConnectionSettings.From(configuration);
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId($"purpleglass-bff-{Environment.ProcessId}")
            .WithTcpServer(settings.Host, settings.Port)
            .WithCleanSession();
        if (!string.IsNullOrWhiteSpace(settings.Username))
            optionsBuilder.WithCredentials(settings.Username, settings.Password!);
        if (settings.UseTls)
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
                    var subscription = new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(configuration["Mqtt:Topic"] ?? "pg/local/v1/tenants/+/events/+")
                        .WithTopicFilter(configuration["Mqtt:CallTopic"] ?? "pg/local/v1/tenants/+/calls/+/events/+")
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
