using PurpleGlass.WebBff;

namespace PurpleGlass.UnitTests;

public sealed class RealtimeEventHubTests
{
    [Fact]
    public async Task PublishDeliversOnlyToMatchingTenant()
    {
        var hub = new RealtimeEventHub();
        Guid tenantId = Guid.NewGuid();
        await using RealtimeSubscription matching = hub.Subscribe(tenantId);
        await using RealtimeSubscription other = hub.Subscribe(Guid.NewGuid());
        var realtimeEvent = new RealtimeEvent(tenantId, "location-display-name-changed", "{}");

        hub.Publish(realtimeEvent);

        Assert.True(matching.Reader.TryRead(out RealtimeEvent? received));
        Assert.Same(realtimeEvent, received);
        Assert.False(other.Reader.TryRead(out _));
    }

    [Fact]
    public void TryCreateExtractsTenantAndEventTypeFromTopic()
    {
        Guid tenantId = Guid.NewGuid();

        bool created = RealtimeEvent.TryCreate(
            $"pg/local/v1/tenants/{tenantId:D}/events/location-display-name-changed",
            "{\"version\":2}",
            out RealtimeEvent? realtimeEvent);

        Assert.True(created);
        Assert.NotNull(realtimeEvent);
        Assert.Equal(tenantId, realtimeEvent.TenantId);
        Assert.Equal("location-display-name-changed", realtimeEvent.EventType);
    }

    [Theory]
    [InlineData("pg/local/v1/tenants/not-a-guid/events/location-display-name-changed")]
    [InlineData("pg/local/v1/tenants/11111111-1111-1111-1111-111111111111/events")]
    [InlineData("other/local/v1/tenants/11111111-1111-1111-1111-111111111111/events/changed")]
    public void TryCreateRejectsInvalidTopic(string topic)
    {
        Assert.False(RealtimeEvent.TryCreate(topic, "{}", out RealtimeEvent? realtimeEvent));
        Assert.Null(realtimeEvent);
    }
}
