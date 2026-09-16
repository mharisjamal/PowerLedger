using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>
/// One external monitor as the wizard and Settings list it (Plan J): what the service detected and worked out — the
/// monitor's name, size and figure, where the figure came from, its brightness and what it draws now — with the user's
/// choice, whether it counts and a figure of their own. A monitor without a choice counts, at PowerLedger's own figure. A
/// figure the user typed is theirs until they clear it. The service's word is refreshed with its status; what the user
/// typed or ticked never is, and a box they haven't touched follows the service's figure.
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

    private string _name = "";
    private string _size = "";
    private string _source = "";
    private string _brightness = "";
    private string _now = "";
    private string _watts;
    private bool _counted;

    public MonitorRow(MonitorStatus monitor, MonitorChoice? choice, CultureInfo culture)
    {
        _culture = culture;
        Key = monitor.Key;
        _typed = choice?.Watts;
        _counted = choice?.Counted ?? true;
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
    /// better", or "typed".</summary>
    public string Source { get => _source; private set => SetProperty(ref _source, value); }

    /// <summary>"brightness 60%, read from the monitor", or "brightness unknown, assumed 75%"; for a typed figure, which no
    /// brightness scales, just "brightness unknown".</summary>
    public string Brightness { get => _brightness; private set => SetProperty(ref _brightness, value); }

    /// <summary>What the service counts it as drawing now: "24.3 W now", or "not counted".</summary>
    public string Now { get => _now; private set => SetProperty(ref _now, value); }

    /// <summary>On-mode watts, as typed; blank for PowerLedger's own figure.</summary>
    public string Watts { get => _watts; set => SetProperty(ref _watts, value); }

    public bool Counted { get => _counted; set => SetProperty(ref _counted, value); }

    /// <summary>What the service now says of the monitor. A figure the user has typed stays; one they haven't follows.</summary>
    internal void Refresh(MonitorStatus monitor)
    {
        var untouched = Watts == _filled;
        Show(monitor);
        _filled = Fill();
        if (untouched) Watts = _filled;
    }

    /// <summary>The choice to save, given the watts the form read from <see cref="Watts"/>: a figure counts as typed only
    /// when it isn't PowerLedger's own figure as the row shows it.</summary>
    internal MonitorChoice Choice(double? watts) => new()
    {
        Key = Key,
        Counted = Counted,
        Watts = watts is { } figure && !(_own is { } own && figure == double.Parse(Figure(own), NumberStyles.AllowDecimalPoint, _culture)) ? figure : null,
    };

    private void Show(MonitorStatus monitor)
    {
        Name = string.IsNullOrWhiteSpace(monitor.Name) ? "External monitor" : monitor.Name.Trim();
        Size = SizeOf(monitor);
        if (monitor.Source != MonitorSource.Typed && double.IsFinite(monitor.OnWatts) && monitor.OnWatts >= 0) _own = monitor.OnWatts;
        // A typed figure is the user's own; otherwise the service's word for where its figure came from. A service that
        // reports a typed figure the choice doesn't hold hasn't caught up with a save yet, so nothing is claimed for it.
        Source = _typed is not null ? "typed" : monitor.Source switch
        {
            MonitorSource.Model => "measured for this model",
            MonitorSource.Estimate => "estimated from its size — correct it if you know better",
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
