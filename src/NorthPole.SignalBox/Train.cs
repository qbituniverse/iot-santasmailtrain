using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Microsoft.Extensions.Options;
using SharpBrick.PoweredUp;
using SharpBrick.PoweredUp.Protocol;
using SharpBrick.PoweredUp.Protocol.Messages;
using BlueZDevice = Linux.Bluetooth.Device;

namespace NorthPole.SignalBox;

/// <summary>The locomotive. Power is -100..100 (negative = reverse), light is 0..100.</summary>
public interface ITrain
{
    bool IsConnected { get; }
    int? BatteryPercent { get; }
    string Mode { get; }

    Task ConnectAsync(CancellationToken ct);
    Task SetPowerAsync(int power, CancellationToken ct);
    Task BrakeAsync(CancellationToken ct);
    Task SetLightAsync(int brightness, CancellationToken ct);
    Task RefreshBatteryAsync(CancellationToken ct);
}

/// <summary>
/// Real LEGO Powered Up hub (88009, built into the 60337 locomotive) via SharpBrick.PoweredUp.
/// On Linux SharpBrick talks Bluetooth through a BlueGiga BLED112 USB dongle (the Pi's built-in radio is not supported by
/// the library; for that, use <see cref="BlueZTrain"/>).
/// We send raw LWP "StartPower" commands: on the motor port that is speed, on the light port it is brightness.
/// </summary>
public sealed class PoweredUpTrain(IOptions<SignalBoxOptions> options, ILoggerFactory loggerFactory, ILogger<PoweredUpTrain> log)
    : ITrain, IAsyncDisposable
{
    const sbyte BrakePower = 127; // LWP: 127 = brake, 0 = float

    readonly TrainOptions _o = options.Value.Train;
    ServiceProvider? _services;
    TwoPortHub? _hub;

    public bool IsConnected => _hub?.IsConnected == true;
    public int? BatteryPercent { get; private set; }
    public string Mode => "PoweredUp";

    public async Task ConnectAsync(CancellationToken ct)
    {
        _services ??= new ServiceCollection()
            .AddSingleton(loggerFactory)
            .AddLogging()
            .AddPoweredUp()
            .AddBlueGigaBLEBluetooth(o =>
            {
                o.COMPortName = _o.BlueGigaPort;
                o.TraceDebug = false;
            })
            .BuildServiceProvider();

        var host = _services.GetRequiredService<PoweredUpHost>();
        log.LogInformation("Looking for the LEGO hub... press the green button on the locomotive");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        _hub = await host.DiscoverAsync<TwoPortHub>(timeout.Token);
        await _hub.ConnectAsync();
        await RefreshBatteryAsync(ct);
        log.LogInformation("Connected to hub {Name}, battery {Battery}%", _hub.AdvertisingName, BatteryPercent);
    }

    public Task SetPowerAsync(int power, CancellationToken ct)
    {
        var p = Math.Clamp(power, -100, 100);
        if (_o.ReverseDirection) p = -p;
        return SendAsync(_o.MotorPort, (sbyte)p);
    }

    public Task BrakeAsync(CancellationToken ct) => SendAsync(_o.MotorPort, BrakePower);

    public Task SetLightAsync(int brightness, CancellationToken ct) =>
        SendAsync(_o.LightPort, (sbyte)Math.Clamp(brightness, 0, 100));

    public async Task RefreshBatteryAsync(CancellationToken ct)
    {
        if (_hub is null) return;
        await _hub.RequestHubPropertySingleUpdate(HubProperty.BatteryVoltage);
        await Task.Delay(200, ct); // the reply arrives asynchronously and updates the property
        BatteryPercent = _hub.BatteryVoltageInPercent;
    }

    async Task SendAsync(byte port, sbyte power)
    {
        if (_hub?.Protocol is not { } protocol)
            throw new InvalidOperationException("LEGO hub is not connected");

        try
        {
            await protocol.SendPortOutputCommandAsync(new PortOutputCommandStartPowerMessage(
                port,
                PortOutputCommandStartupInformation.ExecuteImmediately,
                PortOutputCommandCompletionInformation.CommandFeedback,
                power)
            {
                HubId = 0,
            });
        }
        catch
        {
            _hub = null; // hub switched off or out of range: the signal box will reconnect
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_hub is not null)
            {
                await SendAsync(_o.MotorPort, BrakePower);
                await _hub.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Hub did not disconnect cleanly");
        }
        if (_services is not null) await _services.DisposeAsync();
    }
}

/// <summary>
/// The same hub through Linux's own Bluetooth stack (BlueZ, over D-Bus with Linux.Bluetooth), so the Pi's built-in
/// radio or any standard USB Bluetooth adapter will do: no BLED112 needed. It speaks the LEGO Wireless Protocol itself,
/// sending the same bytes SharpBrick sends in <see cref="PoweredUpTrain"/> (see <see cref="LegoWireless"/>).
/// </summary>
public sealed class BlueZTrain(IOptions<SignalBoxOptions> options, ILogger<BlueZTrain> log) : ITrain, IAsyncDisposable
{
    const sbyte BrakePower = 127; // LWP: 127 = brake, 0 = float

    readonly TrainOptions _o = options.Value.Train;
    BlueZDevice? _device;
    GattCharacteristic? _lwp;
    volatile bool _connected;

    public bool IsConnected => _connected;
    public int? BatteryPercent { get; private set; }
    public string Mode => "BlueZ";

    public async Task ConnectAsync(CancellationToken ct)
    {
        Release();
        var adapter = await BlueZManager.GetAdapterAsync(_o.BluetoothAdapter);
        log.LogInformation("Looking for the LEGO hub on {Adapter}... press the green button on the locomotive", _o.BluetoothAdapter);

        var device = await FindHubAsync(adapter, ct);
        var timeout = TimeSpan.FromSeconds(15);
        await device.ConnectAsync();
        await device.WaitForPropertyValueAsync("Connected", true, timeout);
        await device.WaitForPropertyValueAsync("ServicesResolved", true, timeout);

        var service = await device.GetServiceAsync(LegoWireless.HubService)
                      ?? throw new InvalidOperationException("The hub has no LEGO Wireless Protocol service");
        var lwp = await service.GetCharacteristicAsync(LegoWireless.HubCharacteristic)
                  ?? throw new InvalidOperationException("The hub has no LEGO Wireless Protocol characteristic");

        lwp.Value += OnHubMessageAsync; // subscribing turns notifications on
        device.Disconnected += OnDisconnectedAsync;
        (_device, _lwp, _connected) = (device, lwp, true);

        await RefreshBatteryAsync(ct);
        log.LogInformation("Connected to hub {Name}, battery {Battery}%", await device.GetAliasAsync(), BatteryPercent);
    }

    /// <summary>Scans for an advertising LEGO two-port hub; 60 s, then the signal box tries again.</summary>
    async Task<BlueZDevice> FindHubAsync(Adapter adapter, CancellationToken ct)
    {
        var found = new TaskCompletionSource<BlueZDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task OnDeviceFoundAsync(Adapter sender, DeviceFoundEventArgs e)
        {
            try
            {
                if (LegoWireless.IsTrainHub(await e.Device.GetManufacturerDataAsync())) found.TrySetResult(e.Device);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Skipped a Bluetooth device"); // no manufacturer data, or it vanished mid-scan
            }
        }

        adapter.DeviceFound += OnDeviceFoundAsync;
        try
        {
            await adapter.SetDiscoveryFilterAsync(new Dictionary<string, object>
            {
                ["UUIDs"] = new[] { LegoWireless.HubService },
                ["Transport"] = "le",
            });
            await adapter.StartDiscoveryAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            return await found.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            adapter.DeviceFound -= OnDeviceFoundAsync;
            try { await adapter.StopDiscoveryAsync(); }
            catch (Exception ex) { log.LogDebug(ex, "Bluetooth discovery was already stopped"); }
        }
    }

    public Task SetPowerAsync(int power, CancellationToken ct)
    {
        var p = Math.Clamp(power, -100, 100);
        if (_o.ReverseDirection) p = -p;
        return WriteAsync(LegoWireless.StartPower(_o.MotorPort, (sbyte)p));
    }

    public Task BrakeAsync(CancellationToken ct) => WriteAsync(LegoWireless.StartPower(_o.MotorPort, BrakePower));

    public Task SetLightAsync(int brightness, CancellationToken ct) =>
        WriteAsync(LegoWireless.StartPower(_o.LightPort, (sbyte)Math.Clamp(brightness, 0, 100)));

    public async Task RefreshBatteryAsync(CancellationToken ct)
    {
        if (!_connected) return;
        await WriteAsync(LegoWireless.RequestBattery);
        await Task.Delay(200, ct); // the reply arrives as a notification and updates BatteryPercent
    }

    Task OnHubMessageAsync(GattCharacteristic sender, GattCharacteristicValueEventArgs e)
    {
        if (LegoWireless.TryReadBattery(e.Value, out var percent)) BatteryPercent = percent;
        return Task.CompletedTask;
    }

    Task OnDisconnectedAsync(BlueZDevice sender, BlueZEventArgs e)
    {
        _connected = false; // switched off or out of range: the signal box will reconnect
        log.LogWarning("The LEGO hub disconnected");
        return Task.CompletedTask;
    }

    async Task WriteAsync(byte[] message)
    {
        if (!_connected || _lwp is null)
            throw new InvalidOperationException("LEGO hub is not connected");

        try
        {
            await _lwp.WriteValueAsync(message, new Dictionary<string, object>());
        }
        catch
        {
            _connected = false; // hub switched off or out of range: the signal box will reconnect
            throw;
        }
    }

    void Release()
    {
        _connected = false;
        if (_lwp is not null)
        {
            _lwp.Value -= OnHubMessageAsync;
            _lwp.Dispose();
            _lwp = null;
        }
        if (_device is not null)
        {
            _device.Disconnected -= OnDisconnectedAsync;
            _device.Dispose();
            _device = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_connected && _device is not null)
            {
                await WriteAsync(LegoWireless.StartPower(_o.MotorPort, BrakePower));
                await _device.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Hub did not disconnect cleanly");
        }
        Release();
    }
}

/// <summary>
/// The few LEGO Wireless Protocol 3.0 messages the train needs, byte for byte as SharpBrick.PoweredUp encodes them.
/// Every message starts with a common header: [length, hub id 0x00, message type].
/// </summary>
public static class LegoWireless
{
    public const string HubService = "00001623-1212-efde-1623-785feabcd123";
    public const string HubCharacteristic = "00001624-1212-efde-1623-785feabcd123";

    /// <summary>Bluetooth company id of LEGO System A/S, the key of the hub's manufacturer data.</summary>
    public const ushort LegoCompanyId = 0x0397;

    /// <summary>System type of the two-port hub (88009) in set 60337, second byte of the manufacturer data.</summary>
    public const byte TwoPortHub = 0x41;

    /// <summary>Hub property 0x06 (battery voltage, in percent), operation 0x05 (request update).</summary>
    public static readonly byte[] RequestBattery = [0x05, 0x00, 0x01, 0x06, 0x05];

    /// <summary>
    /// Port output command 0x81: execute immediately with feedback (0x11), WriteDirectModeData (0x51) in mode 0, which is
    /// StartPower: -100..100 on a motor port, 0..100 on a light port, 127 to brake.
    /// </summary>
    public static byte[] StartPower(byte port, sbyte power) => [0x08, 0x00, 0x81, port, 0x11, 0x51, 0x00, (byte)power];

    /// <summary>Hub property update (0x01, operation 0x06) for the battery: [6, hub, 0x01, 0x06, 0x06, percent].</summary>
    public static bool TryReadBattery(byte[] message, out byte percent)
    {
        if (message is [_, _, 0x01, 0x06, 0x06, var value, ..])
        {
            percent = value;
            return true;
        }
        percent = 0;
        return false;
    }

    /// <summary>BlueZ hands over manufacturer data keyed by company id, without the id: [button, system type, ...].</summary>
    public static bool IsTrainHub(IDictionary<ushort, object> manufacturerData) =>
        manufacturerData.TryGetValue(LegoCompanyId, out var value) && value is byte[] { Length: > 1 } data && data[1] == TwoPortHub;
}

/// <summary>A pretend train for working on the laptop: logs commands and drains a pretend battery.</summary>
public sealed class SimulatedTrain(ILogger<SimulatedTrain> log) : ITrain
{
    double _battery = 100;

    public bool IsConnected { get; private set; }
    public int? BatteryPercent => (int)_battery;
    public string Mode => "Simulated";
    public int Power { get; private set; }

    public Task ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        log.LogInformation("Simulated train ready (no Bluetooth needed)");
        return Task.CompletedTask;
    }

    public Task SetPowerAsync(int power, CancellationToken ct)
    {
        Power = power;
        _battery = Math.Max(0, _battery - Math.Abs(power) / 200.0);
        log.LogInformation("Train power {Power}", power);
        return Task.CompletedTask;
    }

    public Task BrakeAsync(CancellationToken ct)
    {
        Power = 0;
        log.LogInformation("Train brakes");
        return Task.CompletedTask;
    }

    public Task SetLightAsync(int brightness, CancellationToken ct)
    {
        log.LogDebug("Headlights {Brightness}%", brightness);
        return Task.CompletedTask;
    }

    public Task RefreshBatteryAsync(CancellationToken ct) => Task.CompletedTask;
}
