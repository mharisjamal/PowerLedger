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
/// is refreshed with its status, and the choice with its settings when a save reads them again; what the user typed or ticked
/// never is, and a box they haven't touched follows the service's figure, defaults and choice.
/// </summary>
internal sealed class MonitorRow : ObservableObject
{
    private readonly CultureInfo _culture;

    /// <summary>The user's choice for the monitor in the settings the form last took, or null for none.</summary>
    private MonitorChoice? _choice;

    /// <summary>What the service last said of the monitor: its status, with whether a monitor without a choice counts as its
    /// settings say when a save has read them since.</summary>
    private MonitorStatus _monitor;

    /// <summary>PowerLedger's own figure, or null until the service gives it: while it holds a typed figure for the monitor
    /// it reports only that one, so the last own figure it gave is kept.</summary>
    private double? _own;

    /// <summary>What the row last put in <see cref="Watts"/>, to tell a box the user has typed in from one they haven't.</summary>
    private string _filled;

    /// <summary>Whether the service takes the monitor to have a plug of its own when the user hasn't said, as it last said.</summary>
    private bool _ownPlugByDefault;

    /// <summary>Whether the user has ticked or unticked <see cref="Counted"/>, so neither a default nor a choice moves it.</summary>
    private bool _countedTicked;

    /// <summary>Whether the user has ticked or unticked <see cref="OwnPlug"/>, so neither a default nor a choice moves it, and
    /// a desktop keeps showing its box.</summary>
    private bool _ownPlugTicked;

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
        _choice = choice;
        _monitor = monitor;
        _counted = choice?.Counted ?? monitor.CountedByDefault;
        _ownPlug = choice?.OwnPlug ?? monitor.OwnPlugByDefault;
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
            if (OwnPlug && SetProperty(ref _counted, value)) _countedTicked = true;
        }
    }

    /// <summary>Whether the monitor has a plug of its own, drawing outside this PC, rather than running off it.</summary>
    public bool OwnPlug
    {
        get => _ownPlug;
        set
        {
            if (!SetProperty(ref _ownPlug, value)) return;
            _ownPlugTicked = true;
            OnPropertyChanged(nameof(Counted));
            OnPropertyChanged(nameof(ShowsPlug));
        }
    }

    /// <summary>Whether a desktop, which doesn't ask, still shows the "has its own plug" box: for a monitor held to run off the
    /// PC, so that can be undone, and once the user has ticked or unticked it since the row was listed, so it doesn't go from
    /// under the pointer. A laptop always shows it.</summary>
    public bool ShowsPlug => !OwnPlug || _ownPlugTicked;

    /// <summary>Whether the service counts the monitor when the user hasn't said, as it last said.</summary>
    internal bool CountedByDefault { get; private set; }

    /// <summary>What the service now says of the monitor. A figure the user has typed stays, and so does a box they have
    /// ticked; one they haven't follows.</summary>
    internal void Refresh(MonitorStatus monitor)
    {
        var untouched = Watts == _filled;
        _monitor = monitor;
        Show(monitor);
        Follow(untouched);
    }

    /// <summary>The choice the service holds for the monitor, and whether it counts a monitor without one, from its settings
    /// read again since the row was listed. A figure the user has typed stays, and so does a box they have ticked; one they
    /// haven't follows.</summary>
    internal void Take(MonitorChoice? choice, bool countedByDefault)
    {
        var untouched = Watts == _filled;
        _choice = choice;
        _monitor = _monitor with { CountedByDefault = countedByDefault };
        Show(_monitor);
        Follow(untouched);
    }

    /// <summary>The choice to save, given the watts the form read from <see cref="Watts"/>: a figure counts as typed only
    /// when it isn't PowerLedger's own figure as the row shows it, the plug is said when it isn't the one the service takes
    /// the monitor to have, when the choice already says it, or when <paramref name="sayPlug"/>, and whether it counts is as
    /// the row shows it, which for a monitor running off this PC is always so. A plug the choice says stays said while it is
    /// unchanged, because the service's status is from its last reading, which may have taken the plug for the chassis before
    /// the save that said it.</summary>
    /// <param name="sayPlug">Say the plug whichever it is, as when the chassis changes, for which the service would take the
    /// monitor's plug afresh.</param>
    internal MonitorChoice Choice(double? watts, bool sayPlug) => new()
    {
        Key = Key,
        Counted = Counted,
        Watts = watts is { } figure && !(_own is { } own && figure == double.Parse(Figure(own), NumberStyles.AllowDecimalPoint, _culture)) ? figure : null,
        OwnPlug = sayPlug || OwnPlug != _ownPlugByDefault || OwnPlug == _choice?.OwnPlug ? OwnPlug : null,
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
        Source = _choice?.Watts is not null ? "typed" : monitor.Source switch
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

    /// <summary>Puts the service's figure, defaults and choice in what the user hasn't changed: the figure's box when
    /// <paramref name="untouched"/>, and each box they haven't ticked.</summary>
    private void Follow(bool untouched)
    {
        _filled = Fill();
        if (untouched) Watts = _filled;
        if (!_countedTicked) SetProperty(ref _counted, _choice?.Counted ?? CountedByDefault, nameof(Counted));
        if (!_ownPlugTicked && SetProperty(ref _ownPlug, _choice?.OwnPlug ?? _ownPlugByDefault, nameof(OwnPlug)))
        {
            OnPropertyChanged(nameof(Counted));
            OnPropertyChanged(nameof(ShowsPlug));
        }
    }

    private string Fill() => _choice?.Watts is { } typed ? Figure(typed) : _own is { } own ? Figure(own) : "";

    /// <summary>Whole or one-decimal watts: "27", "26.9".</summary>
    private string Figure(double watts) => watts.ToString("0.#", _culture);

    private string SizeOf(MonitorStatus monitor)
    {
        var inches = double.IsFinite(monitor.Inches) && monitor.Inches > 0 ? monitor.Inches.ToString("0.#", _culture) + " in" : null;
        var pixels = monitor is { Width: > 0, Height: > 0 } ? $"{monitor.Width.ToString(_culture)} × {monitor.Height.ToString(_culture)}" : null;
        return string.Join(" · ", new[] { inches, pixels }.OfType<string>());
    }
}
