using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// One external monitor as the wizard lists it (Plan J): what the service detected and worked out — the monitor's name,
/// size and figure, and where the figure came from — with the user's choice, whether it counts and a figure of their own.
/// A monitor without a choice counts, at PowerLedger's own figure. A figure the user typed is theirs until they clear it.
/// </summary>
internal sealed class MonitorRow : ObservableObject
{
    private readonly CultureInfo _culture;

    /// <summary>The figure the user's choice holds, or null for PowerLedger's own.</summary>
    private readonly double? _typed;

    /// <summary>PowerLedger's own figure, or null when the service gave none: it reports only the typed figure for a
    /// monitor it holds one for.</summary>
    private readonly double? _own;

    private string _watts;
    private bool _counted;

    public MonitorRow(MonitorStatus monitor, MonitorChoice? choice, CultureInfo culture)
    {
        _culture = culture;
        Key = monitor.Key;
        _typed = choice?.Watts;
        _counted = choice?.Counted ?? true;
        Name = string.IsNullOrWhiteSpace(monitor.Name) ? "External monitor" : monitor.Name.Trim();
        Size = SizeOf(monitor);
        _own = monitor.Source != MonitorSource.Typed && double.IsFinite(monitor.OnWatts) && monitor.OnWatts >= 0 ? monitor.OnWatts : null;
        // A typed figure is the user's own; otherwise the service's word for where its figure came from. A service that
        // reports a typed figure the choice doesn't hold hasn't caught up with a save yet, so nothing is claimed for it.
        Source = _typed is not null ? "typed" : monitor.Source switch
        {
            MonitorSource.Model => "measured for this model",
            MonitorSource.Estimate => "estimated from its size — correct it if you know better",
            _ => "",
        };
        _watts = _typed is { } typed ? Figure(typed) : _own is { } own ? Figure(own) : "";
    }

    /// <summary>The service's key for the monitor, which its choice is saved under.</summary>
    public string Key { get; }

    /// <summary>"DELL U2723QE".</summary>
    public string Name { get; }

    /// <summary>"27 in · 3840 × 2160", or as much of it as the monitor reported.</summary>
    public string Size { get; }

    /// <summary>Where the figure came from: "measured for this model", "estimated from its size — correct it if you know
    /// better", or "typed".</summary>
    public string Source { get; }

    /// <summary>On-mode watts, as typed; blank for PowerLedger's own figure.</summary>
    public string Watts { get => _watts; set => SetProperty(ref _watts, value); }

    public bool Counted { get => _counted; set => SetProperty(ref _counted, value); }

    /// <summary>The choice to save, given the watts the form read from <see cref="Watts"/>: a figure counts as typed only
    /// when it isn't PowerLedger's own figure as the row shows it.</summary>
    internal MonitorChoice Choice(double? watts) => new()
    {
        Key = Key,
        Counted = Counted,
        Watts = watts is { } figure && !(_own is { } own && figure == double.Parse(Figure(own), NumberStyles.AllowDecimalPoint, _culture)) ? figure : null,
    };

    /// <summary>Whole or one-decimal watts: "27", "26.9".</summary>
    private string Figure(double watts) => watts.ToString("0.#", _culture);

    private string SizeOf(MonitorStatus monitor)
    {
        var inches = double.IsFinite(monitor.Inches) && monitor.Inches > 0 ? monitor.Inches.ToString("0.#", _culture) + " in" : null;
        var pixels = monitor is { Width: > 0, Height: > 0 } ? $"{monitor.Width.ToString(_culture)} × {monitor.Height.ToString(_culture)}" : null;
        return string.Join(" · ", new[] { inches, pixels }.OfType<string>());
    }
}
