namespace SurfaceFrame;

public enum DisplayCommand
{
    None,
    TurnOn,
    TurnOff,
    KeepOff
}

public enum PresenceKind
{
    Unknown,
    Home,
    Away
}

/// <summary>
/// Black until presence is known home. Initial away stays black without the 10 minute wait.
/// After home has been confirmed, known-away time accumulates and then the UI covers the
/// screen before the display is powered off. Unknown and disconnected samples pause the accumulator.
/// </summary>
public sealed class PresenceDirector
{
    DateTime? _last;
    bool _countingAway;
    bool _atHome;
    bool _arrived;
    DateTime _maintUntil = DateTime.MinValue;
    DisplayCommand _cmd = DisplayCommand.None;

    public bool Shield { get; private set; } = true;
    public bool EverHome { get; private set; }
    public bool MonitorLatched { get; private set; }
    public TimeSpan AwayAccumulated { get; private set; }

    public void Sample(PresenceKind kind, bool live, DateTime now)
    {
        if (_last is DateTime previous && _countingAway && now > previous)
            AwayAccumulated += now - previous;
        _last = now;

        if (!live || kind == PresenceKind.Unknown)
        {
            _countingAway = false;
            return;
        }

        if (kind == PresenceKind.Home)
        {
            var entering = !_atHome;
            _atHome = true;
            EverHome = true;
            AwayAccumulated = TimeSpan.Zero;
            _countingAway = false;
            Shield = false;
            if (MonitorLatched)
            {
                MonitorLatched = false;
                _cmd = DisplayCommand.TurnOn;
            }
            else if (entering)
            {
                _cmd = DisplayCommand.TurnOn;
            }
            if (entering)
                _arrived = true;
            return;
        }

        _atHome = false;
        if (!EverHome)
        {
            Shield = true;
            _countingAway = false;
            if (!MonitorLatched)
            {
                MonitorLatched = true;
                _cmd = DisplayCommand.TurnOff;
            }
            return;
        }

        _countingAway = true;
        if (!MonitorLatched && AwayAccumulated >= FrameTiming.AwayBeforeSleep)
        {
            Shield = true;
            MonitorLatched = true;
            _cmd = DisplayCommand.TurnOff;
        }
    }

    public DisplayCommand ConsumeCommand()
    {
        var command = _cmd;
        _cmd = DisplayCommand.None;
        return command;
    }

    public bool ConsumeArrivedHome()
    {
        var arrived = _arrived;
        _arrived = false;
        return arrived;
    }

    public void BeginMaintenance(DateTime now)
        => _maintUntil = now + FrameTiming.MaintenanceWake;

    public bool MaintenanceActive(DateTime now) => now < _maintUntil;

    public DisplayCommand OnAccidentalWake(DateTime now)
        => MonitorLatched && !MaintenanceActive(now) ? DisplayCommand.KeepOff : DisplayCommand.None;
}
