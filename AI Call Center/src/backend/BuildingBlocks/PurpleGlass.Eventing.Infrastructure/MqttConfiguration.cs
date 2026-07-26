using Microsoft.Extensions.Configuration;

namespace PurpleGlass.Eventing.Infrastructure;

public sealed record MqttConnectionSettings(
    string Host,
    int Port,
    bool UseTls,
    string? Username,
    string? Password)
{
    public static MqttConnectionSettings From(IConfiguration configuration)
    {
        string host = configuration["Mqtt:Host"] ?? "localhost";
        int port = configuration.GetValue("Mqtt:Port", 1883);
        bool useTls = configuration.GetValue("Mqtt:UseTls", false);
        string? username = configuration["Mqtt:Username"];
        string? password = configuration["Mqtt:Password"];

        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
            throw new InvalidOperationException("MQTT host and port configuration is invalid.");
        if (string.IsNullOrWhiteSpace(username) != string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("MQTT username and password must be configured together.");

        return new(host, port, useTls, username, password);
    }
}
