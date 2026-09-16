using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>
/// One external monitor as the wizard and Settings list it (Plan J): what the service detected and worked out — the
/// monitor's name, size and figure, where the figure came from, its brightness and what it draws now — with the user's
/// choice: whether it counts, a figure of their own, and whether it has a plug of its own. A monitor without a choice
/// counts, and has a plug of its own, as the service's defaults say, at PowerLedger's own figure; one that runs off this PC
/// always counts, being part of what the PC draws. A figure the user typed is theirs until they clear it. The service's word
/// is refreshed with its status; what the user typed or ticked never is, and a box they haven't touched follows the
/// service's figure and defaults.
/// </summary>
internal sealed class MonitorRow : ObservableObject
{
    private readonly CultureInfo _culture;

    /// <summary>The figure the user's choice holds, or null for PowerLedger's own.</summary>
    private readonly double? _typed;

    /// <summary>PowerLedger's own figure, or null until the service gives it: while it holds a typed figure for the monitor
    /// it reports only that one, so the last own figure it gave is kept.</summary>
    private double? _own;

    /// <summary>What the row last put in <see cref="Watts"/>, to tell a box the user has typed in from one they haven't.</summary>
    private string _filled;

    /// <summary>Whether the service takes the monitor to have a plug of its own when the user hasn't said, as it last said.</summary>
    private bool _ownPlugByDefault;

    /// <summary>Whether <see cref="Counted"/> is the user's, ticked by them or held by their choice, so no default moves it.</summary>
    private bool _countedChosen;

    /// <summary>Whether <see cref="OwnPlug"/> is the user's, ticked by them or held by their choice.</summary>
    private bool _ownPlugChosen;

    private string _name = "";
    private string _size = "";
    private string _source = "";
    private string _brightness = "";
    private string _now = "";
    private string _watts;
    private bool _counted;
    private bool _ownPlug;

    public MonitorRow(MonitorStatus monitor, MonitorChoice? choice, CultureInfo culture)
    {
        _culture = culture;
        Key = monitor.Key;
        _typed = choice?.Watts;
        _counted = choice?.Counted ?? monitor.CountedByDefault;
        _ownPlug = choice?.OwnPlug ?? monitor.OwnPlugByDefault;
        _countedChosen = choice is not null;
        _ownPlugChosen = choice?.OwnPlug is not null;
        Show(monitor);
        _filled = Fill();
        _watts = _filled;
    }

    /// <summary>The service's key for the monitor, which its choice is saved under.</summary>
    public string Key { get; }

    /// <summary>"DELL U2723QE".</summary>
    public string Name { get => _name; private set => SetProperty(ref _name, value); }

    /// <summary>"27 in · 3840 × 2160", or as much of it as the monitor reported.</summary>
    public string Size { get => _size; private set => SetProperty(ref _size, value); }

    /// <summary>Where the figure came from: "measured for this model", "estimated from its size — correct it if you know
    /// better" or, for a monitor without its size or resolution, "estimated — correct it if you know better", or "typed".</summary>
    public string Source { get => _source; private set => SetProperty(ref _source, value); }

    /// <summary>"brightness 60%, read from the monitor", or "brightness unknown, assumed 75%"; for a typed figure, which no
    /// brightness scales, just "brightness unknown".</summary>
    public string Brightness { get => _brightness; private set => SetProperty(ref _brightness, value); }

    /// <summary>What the service counts it as drawing now: "24.3 W now", or "not counted".</summary>
    public string Now { get => _now; private set => SetProperty(ref _now, value); }

    /// <summary>On-mode watts, as typed; blank for PowerLedger's own figure.</summary>
    public string Watts { get => _watts; set => SetProperty(ref _watts, value); }

    /// <summary>Whether the monitor counts. One that runs off this PC always does, being part of what the PC draws, so while
    /// it does its box shows ticked and takes no change, and what was ticked before comes back once it has a plug of its
    /// own.</summary>
    public bool Counted
    {
        get => !OwnPlug || _counted;
        set
        {
            if (OwnPlug && SetProperty(ref _counted, value)) _countedChosen = true;
        }
    }

    /// <summary>Whether the monitor has a plug of its own, drawing outside this PC, rather than running off it.</summary>
    public bool OwnPlug
    {
        get => _ownPlug;
        set
        {
            if (!SetProperty(ref _ownPlug, value)) return;
            _ownPlugChosen = true;
            OnPropertyChanged(nameof(Counted));
        }
    }

    /// <summary>Whether the service counts the monitor when the user hasn't said, as it last said.</summary>
    internal bool CountedByDefault { get; private set; }

    /// <summary>What the service now says of the monitor. A figure the user has typed stays, and so does a box they have
    /// ticked; one they haven't follows.</summary>
    internal void Refresh(MonitorStatus monitor)
    {
        var untouched = Watts == _filled;
        Show(monitor);
        _filled = Fill();
        if (untouched) Watts = _filled;
        if (!_countedChosen) SetProperty(ref _counted, CountedByDefault, nameof(Counted));
        if (!_ownPlugChosen && SetProperty(ref _ownPlug, _ownPlugByDefault, nameof(OwnPlug))) OnPropertyChanged(nameof(Counted));
    }

    /// <summary>The choice to save, given the watts the form read from <see cref="Watts"/>: a figure counts as typed only
    /// when it isn't PowerLedger's own figure as the row shows it, the plug is said only when it isn't the one the service
    /// takes the monitor to have, and whether it counts is as the row shows it, which for a monitor running off this PC is
    /// always so.</summary>
    internal MonitorChoice Choice(double? watts) => new()
    {
        Key = Key,
        Counted = Counted,
        Watts = watts is { } figure && !(_own is { } own && figure == double.Parse(Figure(own), NumberStyles.AllowDecimalPoint, _culture)) ? figure : null,
        OwnPlug = OwnPlug != _ownPlugByDefault ? OwnPlug : null,
    };

    private void Show(MonitorStatus monitor)
    {
        CountedByDefault = monitor.CountedByDefault;
        _ownPlugByDefault = monitor.OwnPlugByDefault;
        Name = string.IsNullOrWhiteSpace(monitor.Name) ? "External monitor" : monitor.Name.Trim();
        Size = SizeOf(monitor);
        if (monitor.Source != MonitorSource.Typed && double.IsFinite(monitor.OnWatts) && monitor.OnWatts >= 0) _own = monitor.OnWatts;
        // A typed figure is the user's own; otherwise the service's word for where its figure came from. A service that
        // reports a typed figure the choice doesn't hold hasn't caught up with a save yet, so nothing is claimed for it. A
        // monitor that doesn't give both its size and its resolution is estimated as the median of all monitors, not from
        // its size.
        Source = _typed is not null ? "typed" : monitor.Source switch
        {
            MonitorSource.Model => "measured for this model",
            MonitorSource.Estimate when double.IsFinite(monitor.Inches) && monitor is { Inches: > 0, Width: > 0, Height: > 0 }
                => "estimated from its size — correct it if you know better",
            MonitorSource.Estimate => "estimated — correct it if you know better",
            _ => "",
        };
        // A figure the service reports as typed is taken as it is, so no brightness is assumed for it.
        Brightness = monitor.Brightness is { } brightness && double.IsFinite(brightness)
            ? $"brightness {Format.Percent(brightness, _culture)}, read from the monitor"
            : monitor.Source == MonitorSource.Typed
                ? "brightness unknown"
                : $"brightness unknown, assumed {Format.Percent(MonitorPower.ListedBrightness, _culture)}";
        Now = monitor.Counted ? $"{Format.Watts(monitor.WattsNow, _culture)} W now" : "not counted";
    }

    private string Fill() => _typed is { } typed ? Figure(typed) : _own is { } own ? Figure(own) : "";

    /// <summary>Whole or one-decimal watts: "27", "26.9".</summary>
    private string Figure(double watts) => watts.ToString("0.#", _culture);

    private string SizeOf(MonitorStatus monitor)
    {
        var inches = double.IsFinite(monitor.Inches) && monitor.Inches > 0 ? monitor.Inches.ToString("0.#", _culture) + " in" : null;
        var pixels = monitor is { Width: > 0, Height: > 0 } ? $"{monitor.Width.ToString(_culture)} × {monitor.Height.ToString(_culture)}" : null;
        return string.Join(" · ", new[] { inches, pixels }.OfType<string>());
    }
}
