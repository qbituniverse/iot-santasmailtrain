using NorthPole.Contracts;
using NorthPole.Messaging;
using NorthPole.SignalBox;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<SignalBoxOptions>(builder.Configuration.GetSection("SignalBox"));
var options = builder.Configuration.GetSection("SignalBox").Get<SignalBoxOptions>() ?? new();

builder.Services.AddNorthPoleMessaging(link =>
{
    link.Host = options.Mqtt.Host;
    link.Port = options.Mqtt.Port;
    link.ClientId = options.Mqtt.ClientId;
    // If the signal box stops or loses power, the broker tells everyone the train is offline.
    link.WillTopic = Topics.TrainState;
    link.WillPayload = NorthPoleJson.Serialize(new TrainState(false, null, 0, false, "Offline", "Offline", DateTimeOffset.Now));
});

if (options.Train.Mode == TrainMode.BlueZ)
    builder.Services.AddSingleton<ITrain, BlueZTrain>();
else if (options.Train.Mode == TrainMode.PoweredUp)
    builder.Services.AddSingleton<ITrain, PoweredUpTrain>();
else
    builder.Services.AddSingleton<ITrain, SimulatedTrain>();

if (options.Trackside.Mode == TracksideMode.Gpio)
    builder.Services.AddSingleton<ITrackside, GpioTrackside>();
else
    builder.Services.AddSingleton<ITrackside, SimulatedTrackside>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<StationBoard>();
if (options.Trackside.Board.Enabled)
{
    if (options.Trackside.Mode == TracksideMode.Gpio)
        builder.Services.AddSingleton<IBoardDisplay, LcdBoardDisplay>();
    else
        builder.Services.AddSingleton<IBoardDisplay, SimulatedBoardDisplay>();
    builder.Services.AddHostedService<StationBoardService>();
}

builder.Services.AddHostedService<MailRunService>();

var host = builder.Build();
host.Services.GetRequiredService<ILogger<Program>>().LogInformation(
    "Signal box starting: train={Train}, trackside={Track}, broker={Host}:{Port}",
    options.Train.Mode, options.Trackside.Mode, options.Mqtt.Host, options.Mqtt.Port);
await host.RunAsync();
