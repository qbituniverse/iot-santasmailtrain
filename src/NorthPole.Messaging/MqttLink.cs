using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using NorthPole.Contracts;

namespace NorthPole.Messaging;

public sealed class MqttLinkOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = $"northpole-{Environment.MachineName}";

    /// <summary>Optional "last will": published by the broker if this client vanishes (power cut, crash).</summary>
    public string? WillTopic { get; set; }
    public string? WillPayload { get; set; }
}

/// <summary>
/// Keeps one MQTT connection alive, re-subscribes after reconnects and hands out typed messages.
/// Handlers run off the network thread, one message at a time per topic.
/// </summary>
public sealed class MqttLink : BackgroundService
{
    readonly MqttLinkOptions _o;
    readonly ILogger<MqttLink> _log;
    readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    readonly ConcurrentDictionary<string, Func<string, Task>> _handlers = new();

    public MqttLink(IOptions<MqttLinkOptions> options, ILogger<MqttLink> log)
    {
        _o = options.Value;
        _log = log;
        _client.ApplicationMessageReceivedAsync += e =>
        {
            var topic = e.ApplicationMessage.Topic;
            if (!_handlers.TryGetValue(topic, out var handler)) return Task.CompletedTask;
            var payload = e.ApplicationMessage.ConvertPayloadToString();
            _ = Task.Run(async () =>
            {
                try { await handler(payload); }
                catch (Exception ex) { _log.LogError(ex, "Handler for {Topic} failed", topic); }
            });
            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _client.IsConnected;

    /// <summary>Raised with true on (re)connect and false when the link drops.</summary>
    public event Action<bool>? ConnectionChanged;

    public void On<T>(string topic, Func<T, Task> handler)
    {
        _handlers[topic] = async json =>
        {
            var message = NorthPoleJson.Deserialize<T>(json);
            if (message is not null) await handler(message);
        };
        if (_client.IsConnected)
            _ = _client.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce);
    }

    public async Task<bool> PublishAsync<T>(string topic, T message, bool retain = false, CancellationToken ct = default)
    {
        if (!_client.IsConnected)
        {
            _log.LogDebug("Not connected to MQTT, dropped message on {Topic}", topic);
            return false;
        }
        await _client.PublishStringAsync(topic, NorthPoleJson.Serialize(message), MqttQualityOfServiceLevel.AtLeastOnce, retain, ct);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_o.Host, _o.Port)
            .WithClientId(_o.ClientId)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(5))
            .WithTimeout(TimeSpan.FromSeconds(5));
        if (_o.WillTopic is not null && _o.WillPayload is not null)
            builder = builder.WithWillTopic(_o.WillTopic).WithWillPayload(_o.WillPayload).WithWillRetain();
        var options = builder.Build();

        var wasConnected = false;
        var saidWaiting = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                if (wasConnected)
                {
                    wasConnected = false;
                    _log.LogWarning("MQTT link lost");
                    ConnectionChanged?.Invoke(false);
                }
                try
                {
                    await _client.ConnectAsync(options, stoppingToken);
                    foreach (var topic in _handlers.Keys)
                        await _client.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce, stoppingToken);
                    wasConnected = true;
                    saidWaiting = false;
                    _log.LogInformation("MQTT connected to {Host}:{Port} as {ClientId}", _o.Host, _o.Port, _o.ClientId);
                    ConnectionChanged?.Invoke(true);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _log.LogDebug(ex, "MQTT connect failed");
                    if (!saidWaiting) _log.LogInformation("Waiting for the MQTT broker at {Host}:{Port}...", _o.Host, _o.Port);
                    saidWaiting = true;
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        if (_client.IsConnected)
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
