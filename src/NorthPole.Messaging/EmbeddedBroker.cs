using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet.Server;

namespace NorthPole.Messaging;

public sealed class EmbeddedBrokerOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 1883;
}

/// <summary>
/// A tiny MQTT broker inside the HQ process, so the Pi 5 doesn't need Mosquitto.
/// Anything on your LAN (the signal box, a Unity digital twin, MQTT Explorer) can connect to port 1883.
/// Switch it off and run Mosquitto instead if you want authentication or persistence.
/// </summary>
public sealed class EmbeddedBroker(IOptions<EmbeddedBrokerOptions> options, ILogger<EmbeddedBroker> log) : IHostedService
{
    MqttServer? _server;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Enabled) return;

        var serverOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(o.Port)
            .Build();
        _server = new MqttServerFactory().CreateMqttServer(serverOptions);
        _server.ClientConnectedAsync += e =>
        {
            log.LogInformation("MQTT client connected: {ClientId}", e.ClientId);
            return Task.CompletedTask;
        };
        await _server.StartAsync();
        log.LogInformation("Embedded MQTT broker listening on port {Port}", o.Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null) await _server.StopAsync();
    }
}

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Registers the MQTT link (and the broker, which must start first so the link can connect to it).</summary>
    public static IServiceCollection AddNorthPoleMessaging(this IServiceCollection services,
        Action<MqttLinkOptions> configureLink,
        Action<EmbeddedBrokerOptions>? configureBroker = null)
    {
        services.Configure(configureLink);
        if (configureBroker is not null)
        {
            services.Configure(configureBroker);
            services.AddHostedService<EmbeddedBroker>();
        }
        services.AddSingleton<MqttLink>();
        services.AddHostedService(sp => sp.GetRequiredService<MqttLink>());
        return services;
    }
}
