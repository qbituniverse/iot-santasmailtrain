using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NorthPole.Contracts;
using NorthPole.Messaging;

namespace NorthPole.SignalBox;

/// <summary>
/// The signal box's brain. It takes mail jobs from HQ and runs them one at a time:
///   Mailbox: signal red, flag up, headlights blink (Loading)
///   → signal green, cruise (Departed) → the station sensor trips at the North Pole, brake (Arrived)
///   → wait for Santa to finish reading → back to the mailbox (Returning → Home).
/// Every stage also goes to the station board at the mailbox.
///
/// Safety interlock: HQ (and the AI behind it) can only *ask*. Speeds are capped here, every leg has a
/// timeout, and the train brakes if the MQTT link to HQ drops or anything throws.
/// </summary>
public sealed class MailRunService : BackgroundService
{
    readonly SignalBoxOptions _o;
    readonly ITrain _train;
    readonly ITrackside _track;
    readonly MqttLink _mqtt;
    readonly StationBoard _board;
    readonly ILogger<MailRunService> _log;
    readonly Channel<MailDispatch> _queue = Channel.CreateBounded<MailDispatch>(10);
    readonly Lock _returnLock = new();
    TaskCompletionSource? _santaFinished;
    CancellationTokenSource? _run; // the mail run in progress, so the dashboard's Stop can end it
    CancellationTokenSource? _scan; // the search for the hub in progress, so Disconnect can end it
    readonly SemaphoreSlim _wake = new(0, 1); // Connect, or a dropped hub: look for it now, not after the pause
    volatile bool _listening = true; // look for the hub from start-up, until an elf presses Disconnect
    Guid? _currentWish;
    int _speed;

    public MailRunService(IOptions<SignalBoxOptions> options, ITrain train, ITrackside track, MqttLink mqtt,
        StationBoard board, ILogger<MailRunService> log)
    {
        _o = options.Value;
        _train = train;
        _track = track;
        _mqtt = mqtt;
        _board = board;
        _log = log;

        mqtt.On<MailDispatch>(Topics.Dispatch, OnDispatchAsync);
        mqtt.On<ReturnHome>(Topics.ReturnHome, OnReturnHome);
        mqtt.On<TrainCommand>(Topics.Command, OnCommandAsync);
        train.StatusChanged += () =>
        {
            _ = PublishStateQuietlyAsync(); // a dropped hub or a new battery reading: tell HQ now
            if (!train.IsConnected) Wake(); // and look for a dropped hub straight away
        };
        mqtt.ConnectionChanged += connected =>
        {
            if (connected)
            {
                _ = PublishStateAsync(); // let HQ know straight away that the train is here
                return;
            }
            if (_speed != 0)
            {
                _log.LogWarning("Lost HQ: emergency brake");
                _ = SafeBrakeAsync();
            }
        };
    }

    async Task OnDispatchAsync(MailDispatch job)
    {
        if (!_train.IsConnected)
        {
            await ReportAsync(job, MailStage.Rejected, "The train is asleep (LEGO hub not connected)");
            return;
        }
        if (_train.BatteryPercent is { } battery && battery < _o.Train.LowBatteryPercent)
        {
            await ReportAsync(job, MailStage.Rejected, $"Train batteries are low ({battery}%)");
            return;
        }
        if (_currentWish is null && _speed != 0)
        {
            // Somewhere on the line, not at the mailbox: the train driver has to stop it first.
            await ReportAsync(job, MailStage.Rejected, "An elf is driving the train");
            return;
        }
        if (!_queue.Writer.TryWrite(job))
        {
            await ReportAsync(job, MailStage.Rejected, "Too much post in the queue");
            return;
        }
        await ReportAsync(job, MailStage.Accepted, _currentWish is null ? null : "Queued behind another letter");
    }

    Task OnReturnHome(ReturnHome msg)
    {
        lock (_returnLock)
        {
            if (_currentWish == msg.WishId) _santaFinished?.TrySetResult();
        }
        return Task.CompletedTask;
    }

    async Task OnCommandAsync(TrainCommand cmd)
    {
        var ct = CancellationToken.None;
        switch (cmd.Kind)
        {
            case TrainCommandKind.Stop:
                _log.LogInformation("Stop from the dashboard");
                try { _run?.Cancel(); } // the run brakes and reports itself stopped
                catch (ObjectDisposedException) { } // it had just finished
                await SafeBrakeAsync();
                await PublishStateAsync();
                break;
            case TrainCommandKind.Drive:
                if (_currentWish is not null)
                {
                    _log.LogWarning("Train driver ignored: the train is on a mail run");
                    break;
                }
                if (!_train.IsConnected)
                {
                    _log.LogWarning("Train driver ignored: the LEGO hub isn't connected");
                    break;
                }
                if (cmd.Speed is not (> 0 and var speed))
                {
                    await SafeBrakeAsync();
                }
                else
                {
                    _log.LogInformation("Train driver: speed {Speed}", speed);
                    await SetSpeedAsync(speed, ct); // capped at MaxSpeed, like every other speed
                }
                await PublishStateAsync();
                break;
            case TrainCommandKind.TestRun:
                await OnDispatchAsync(new MailDispatch(Guid.NewGuid(), "Test", DateTimeOffset.Now, WaitForSanta: false));
                break;
            case TrainCommandKind.LightsOn:
                await _train.SetLightAsync(100, ct);
                break;
            case TrainCommandKind.LightsOff:
                await _train.SetLightAsync(0, ct);
                break;
            case TrainCommandKind.Whistle:
                await BlinkLightsAsync(3, ct);
                break;
            case TrainCommandKind.Connect:
                _log.LogInformation("Connect from the dashboard: looking for the hub, press its green button");
                _listening = true;
                Wake();
                await PublishStateAsync();
                break;
            case TrainCommandKind.Disconnect:
                await DisconnectAsync();
                break;
        }
    }

    /// <summary>The dashboard's Disconnect: stop looking for the hub, end any run like Stop, then let the hub go.</summary>
    async Task DisconnectAsync()
    {
        _log.LogInformation("Disconnect from the dashboard");
        _listening = false;
        try { _scan?.Cancel(); } // a search in progress gives up
        catch (ObjectDisposedException) { } // it had just finished
        try { _run?.Cancel(); }
        catch (ObjectDisposedException) { }
        await SafeBrakeAsync();

        // A stopped run brakes and reports itself first, while the hub is still there to hear it.
        for (var i = 0; i < 30 && _currentWish is not null; i++) await Task.Delay(100);
        try
        {
            await _train.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning("The hub didn't disconnect cleanly ({Message})", ex.Message);
        }
        await PublishStateAsync();
    }

    void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { } // already woken
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _track.SetSignal(SignalAspect.Red);
        _track.SetFlag(false);
        _ = KeepTrainConnectedAsync(stoppingToken);
        _ = PublishStateLoopAsync(stoppingToken);

        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            _currentWish = job.WishId;
            using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _run = run;
            try
            {
                await PublishStateAsync(); // busy now, so the dashboard's train driver steps aside at once
                await RunMailAsync(job, run.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested)
            {
                await SafeBrakeAsync();
                await ReportAsync(job, MailStage.Failed, "Stopped from the dashboard");
                _board.Show(job, MailStage.Home); // a grown-up stopped it: no "Grown-up needed" on the board
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Mail run for {Wish} failed", job.WishId);
                await SafeBrakeAsync();
                await ReportAsync(job, MailStage.Failed, ex is TimeoutException ? ex.Message : "Something went wrong on the line");
            }
            finally
            {
                _run = null;
                _currentWish = null;
                _track.SetFlag(false);
                _track.SetSignal(SignalAspect.Red);
                await PublishStateAsync();
            }
        }

        await SafeBrakeAsync();
    }

    async Task RunMailAsync(MailDispatch job, CancellationToken ct)
    {
        _log.LogInformation("Mail run for {Child} ({Wish})", job.ChildName, job.WishId);

        // 1. Loading at the mailbox.
        await ReportAsync(job, MailStage.Loading, $"Loading {job.ChildName}'s letter");
        _track.SetSignal(SignalAspect.Red);
        _track.SetFlag(true);
        await BlinkLightsAsync(3, ct);
        await Task.Delay(TimeSpan.FromSeconds(_o.Run.LoadingSeconds), ct);

        // 2. Depart and run to the North Pole.
        _track.SetSignal(SignalAspect.Green);
        await _train.SetLightAsync(100, ct);
        await DriveToAsync(Station.NorthPole, job, MailStage.Departed, ct);
        _track.SetFlag(false);
        _track.SetSignal(SignalAspect.Red);
        await ReportAsync(job, MailStage.Arrived, "Arrived at the North Pole");

        // 3. Wait while Santa reads the letter out loud (HQ tells us when he's done).
        if (!_track.HasHomeSensor)
        {
            await ReportAsync(job, MailStage.Home, "Parked at the North Pole");
            return;
        }

        if (job.WaitForSanta)
        {
            TaskCompletionSource santaDone;
            lock (_returnLock)
            {
                santaDone = _santaFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            await Task.WhenAny(santaDone.Task, Task.Delay(TimeSpan.FromSeconds(_o.Run.MaxWaitAtNorthPoleSeconds), ct));
            ct.ThrowIfCancellationRequested(); // WhenAny doesn't throw: a Stop while Santa reads ends the run here
        }
        else
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        // 4. Home to the mailbox, ready for the next letter.
        _track.SetSignal(SignalAspect.Green);
        await DriveToAsync(Station.Home, job, MailStage.Returning, ct);
        _track.SetSignal(SignalAspect.Red);
        await _train.SetLightAsync(30, ct);
        await ReportAsync(job, MailStage.Home, "Back at the mailbox");
    }

    async Task DriveToAsync(Station station, MailDispatch job, MailStage stage, CancellationToken ct)
    {
        await SetSpeedAsync(_o.Train.CruiseSpeed, ct);
        _track.NotifyTrainMoving(station);
        await ReportAsync(job, stage, station == Station.NorthPole ? "Choo choo! Heading north" : "Heading home");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_o.Run.ArrivalTimeoutSeconds));
        try
        {
            await _track.WaitForTrainAsync(station, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The train didn't reach {station} in {_o.Run.ArrivalTimeoutSeconds:0}s (derailed? sensor blocked?)");
        }

        // Sensor sits a little before the platform: ease in, then brake.
        await SetSpeedAsync(_o.Train.ApproachSpeed, ct);
        await Task.Delay(350, ct);
        await _train.BrakeAsync(ct);
        _speed = 0;
    }

    /// <summary>The only way the motor gets power. Caps the speed whatever was asked.</summary>
    async Task SetSpeedAsync(int requested, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); // a stopped run never starts the motor again
        var max = Math.Clamp(_o.Train.MaxSpeed, 0, 100);
        var speed = Math.Clamp(requested, -max, max);
        if (speed != requested) _log.LogWarning("Speed {Requested} capped to {Speed}", requested, speed);
        await _train.SetPowerAsync(speed, ct);
        _speed = speed;
    }

    async Task SafeBrakeAsync()
    {
        if (!_train.IsConnected)
        {
            _speed = 0; // no hub to send the brake to (after Disconnect, Stop is a no-op)
            return;
        }
        try
        {
            await _train.BrakeAsync(CancellationToken.None);
            _speed = 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not brake: is the hub still connected?");
        }
    }

    async Task BlinkLightsAsync(int times, CancellationToken ct)
    {
        for (var i = 0; i < times; i++)
        {
            await _train.SetLightAsync(100, ct);
            await Task.Delay(250, ct);
            await _train.SetLightAsync(0, ct);
            await Task.Delay(250, ct);
        }
    }

    Task ReportAsync(MailDispatch job, MailStage stage, string? detail)
    {
        _log.LogInformation("{Stage}: {Detail}", stage, detail);
        _board.Show(job, stage);
        return _mqtt.PublishAsync(Topics.Progress, new MailProgress(job.WishId, stage, detail, DateTimeOffset.Now));
    }

    /// <summary>
    /// While listening, looks for the hub whenever it isn't connected: 60 s searches with 15 s pauses, cut short by
    /// Connect or a dropped hub. After Disconnect it only waits for the next Connect.
    /// </summary>
    async Task KeepTrainConnectedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_listening && !_train.IsConnected)
            {
                using var scan = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _scan = scan;
                try
                {
                    await _train.ConnectAsync(scan.Token);
                    if (!_listening)
                    {
                        // Disconnect came while the hub was connecting: let it go again.
                        await _train.DisconnectAsync(ct);
                    }
                    else
                    {
                        await _train.SetLightAsync(30, ct);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Disconnect while searching.
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning("Train not reachable yet ({Message}). Still looking", ex.Message);
                }
                finally
                {
                    _scan = null;
                }
                await PublishStateQuietlyAsync();
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task PublishStateLoopAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(_o.Run.StatePublishSeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_train.IsConnected && _currentWish is null) await _train.RefreshBatteryAsync(ct);
                await PublishStateAsync();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogDebug(ex, "State publish failed");
            }
            try { await Task.Delay(period, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    Task PublishStateAsync() => _mqtt.PublishAsync(Topics.TrainState, new TrainState(
        _train.IsConnected, _train.BatteryPercent, _speed, _currentWish is not null, _train.Mode, _track.Mode, DateTimeOffset.Now,
        Math.Clamp(_o.Train.MaxSpeed, 0, 100), _train.BatteryVolts, _train.ConnectedSince, _o.Train.LowBatteryPercent,
        _listening), retain: true);

    async Task PublishStateQuietlyAsync()
    {
        try { await PublishStateAsync(); }
        catch (Exception ex) { _log.LogDebug(ex, "State publish failed"); } // the next regular publish catches up
    }
}
