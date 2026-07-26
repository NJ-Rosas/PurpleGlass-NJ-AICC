using Microsoft.Extensions.Configuration;
using PurpleGlass.Eventing.Infrastructure;

namespace PurpleGlass.UnitTests;

public sealed class MqttConfigurationTests
{
    [Fact]
    public void FromUsesLocalDefaults()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        MqttConnectionSettings settings = MqttConnectionSettings.From(configuration);

        Assert.Equal("localhost", settings.Host);
        Assert.Equal(1883, settings.Port);
        Assert.False(settings.UseTls);
        Assert.Null(settings.Username);
        Assert.Null(settings.Password);
    }

    [Fact]
    public void FromReadsAuthenticatedTlsConfiguration()
    {
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["Mqtt:Host"] = "example.s1.eu.hivemq.cloud",
            ["Mqtt:Port"] = "8883",
            ["Mqtt:UseTls"] = "true",
            ["Mqtt:Username"] = "purpleglass",
            ["Mqtt:Password"] = "not-a-real-secret",
        });

        MqttConnectionSettings settings = MqttConnectionSettings.From(configuration);

        Assert.True(settings.UseTls);
        Assert.Equal("purpleglass", settings.Username);
        Assert.Equal("not-a-real-secret", settings.Password);
    }

    [Fact]
    public void FromRejectsPartialCredentials()
    {
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["Mqtt:Username"] = "purpleglass",
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MqttConnectionSettings.From(configuration));

        Assert.Equal("MQTT username and password must be configured together.", exception.Message);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
