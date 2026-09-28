using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App.Aero;

/// <summary>A colour the Glass section offers: its name, for the screen reader and the tooltip, and its #RRGGBB.</summary>
internal sealed record TintPreset(string Name, string Hex)
{
    public Color Colour => (Color)ColorConverter.ConvertFromString(Hex);
}

/// <summary>An accent the Glass section offers: the setting, its name and the demo's swatch.</summary>
internal sealed record AccentSwatch(GlassAccent Accent, string Name, string Hex)
{
    public Color Colour => (Color)ColorConverter.ConvertFromString(Hex);
}

/// <summary>
/// Settings' Glass section in Aero (Aero look design §3), like the iPhone's: Clear, Tinted, Dark or a Colour of the user's
/// own from 8 presets or a hue and saturation wheel; tint strength, frost and edge light; the accent; what shows behind
/// the glass; Reduce transparency, Increase contrast and Reduce motion; tilt and parallax. Each choice sets
/// <see cref="SettingsViewModel.Glass"/> to a new record with the one field changed, which saves it in range and raises
/// it, the one live channel the glass follows (Plan S 0.4); the section follows that channel back, so it shows what was
/// kept whoever changed it. Reduce motion shows Windows' setting until the user changes it here, and Follow Windows hands
/// it back.
/// </summary>
internal sealed class GlassSection : ObservableObject
{
    /// <summary>The Colour style's presets: the demo's violet first, then seven that keep white text readable on the
    /// glass at the default strength.</summary>
    public static IReadOnlyList<TintPreset> Presets { get; } =
    [
        new("Violet", GlassSettings.DefaultTint),
        new("Ocean", "#3A7BD5"),
        new("Teal", "#2A9D8F"),
        new("Moss", "#5B8C3A"),
        new("Amber", "#D08B2C"),
        new("Coral", "#D9644A"),
        new("Rose", "#C2527F"),
        new("Graphite", "#6B7280"),
    ];

    /// <summary>The accents, with the demo's swatches (its "Try the look" panel).</summary>
    public static IReadOnlyList<AccentSwatch> Accents { get; } =
    [
        new(GlassAccent.Lime, "Lime", "#D3F03F"),
        new(GlassAccent.Ice, "Ice", "#7FD4FF"),
        new(GlassAccent.Indigo, "Indigo", "#9AA2FF"),
        new(GlassAccent.Amber, "Amber", "#FFC857"),
        new(GlassAccent.Rose, "Rose", "#FF8FB1"),
    ];

    private readonly SettingsViewModel _settings;
    private readonly Func<bool> _windowsReducesMotion;

    /// <param name="windowsReducesMotion">Whether Windows reduces motion now (<c>!SystemParameters.ClientAreaAnimation</c>),
    /// read each time it is shown rather than once, since the user may change it while the App runs.</param>
    public GlassSection(SettingsViewModel settings, Func<bool> windowsReducesMotion)
    {
        _settings = settings;
        _windowsReducesMotion = windowsReducesMotion;
        ChoosePreset = new RelayCommand<string>(hex =>
        {
            if (hex is not null) Change(Glass with { Style = GlassStyle.Colour, TintColor = hex });
        });
        FollowWindows = new RelayCommand(() => Change(Glass with { ReduceMotion = null }));
        Reset = new RelayCommand(() => Change(GlassSettings.Default));
        settings.PropertyChanged += OnSettingsChanged;
    }

    public GlassStyle Style { get => Glass.Style; set => Change(Glass with { Style = value }); }

    /// <summary>The Colour style is chosen: the presets and the wheel show.</summary>
    public bool IsColour => Glass.Style == GlassStyle.Colour;

    /// <summary>The Colour style's tint, #RRGGBB.</summary>
    public string TintColor => Glass.TintColor;

    /// <summary>The Colour style's tint as the wheel and the preview draw it; a colour chosen is kept as #RRGGBB, any alpha
    /// dropped, since the strength slider says how much of it shows.</summary>
    public Color Tint
    {
        get => (Color)ColorConverter.ConvertFromString(Glass.TintColor);
        set => Change(Glass with { TintColor = string.Create(CultureInfo.InvariantCulture, $"#{value.R:X2}{value.G:X2}{value.B:X2}") });
    }

    public double TintStrength { get => Glass.TintStrength; set => Change(Glass with { TintStrength = value }); }

    public double Frost { get => Glass.Frost; set => Change(Glass with { Frost = value }); }

    public double EdgeLight { get => Glass.EdgeLight; set => Change(Glass with { EdgeLight = value }); }

    public GlassAccent Accent { get => Glass.Accent; set => Change(Glass with { Accent = value }); }

    public GlassBackdrop Backdrop { get => Glass.Backdrop; set => Change(Glass with { Backdrop = value }); }

    public bool ReduceTransparency { get => Glass.ReduceTransparency; set => Change(Glass with { ReduceTransparency = value }); }

    public bool IncreaseContrast { get => Glass.IncreaseContrast; set => Change(Glass with { IncreaseContrast = value }); }

    /// <summary>Whether motion is reduced: as chosen here, or Windows' setting until then. Choosing here, even what
    /// Windows has, stops following Windows (Aero look design §3).</summary>
    public bool ReduceMotion
    {
        get => Glass.ReduceMotion ?? _windowsReducesMotion();
        set
        {
            if (Glass.ReduceMotion == value) return;
            Change(Glass with { ReduceMotion = value });
        }
    }

    /// <summary>Reduce motion still follows Windows.</summary>
    public bool FollowsWindows => Glass.ReduceMotion is null;

    /// <summary>Under the Reduce motion switch: where its state comes from.</summary>
    public string ReduceMotionNote => FollowsWindows
        ? "As Windows has it"
        : _windowsReducesMotion() ? "Set here. Windows reduces motion." : "Set here. Windows doesn't reduce motion.";

    /// <summary>Tilt and parallax as the pointer moves, the user's choice, kept while motion is reduced.</summary>
    public bool Parallax { get => Glass.Parallax; set => Change(Glass with { Parallax = value }); }

    /// <summary>Parallax can show: never under reduced motion, whatever its switch says.</summary>
    public bool ParallaxAvailable => !ReduceMotion;

    /// <summary>A preset's #RRGGBB: the Colour style, in that tint.</summary>
    public ICommand ChoosePreset { get; }

    /// <summary>Reduce motion follows Windows again.</summary>
    public ICommand FollowWindows { get; }

    /// <summary>The demo's glass back, every field.</summary>
    public ICommand Reset { get; }

    private GlassSettings Glass => _settings.Glass;

    private void Change(GlassSettings glass) => _settings.Glass = glass;

    /// <summary>The glass changed, here or anywhere: everything the section shows may have.</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.Glass)) return;
        foreach (var name in Shown) OnPropertyChanged(name);
    }

    /// <summary>Each property the section shows, raised by name rather than all at once, so a binding that listens for its
    /// own name alone hears it.</summary>
    private static readonly string[] Shown =
    [
        nameof(Style), nameof(IsColour), nameof(TintColor), nameof(Tint), nameof(TintStrength), nameof(Frost), nameof(EdgeLight),
        nameof(Accent), nameof(Backdrop), nameof(ReduceTransparency), nameof(IncreaseContrast), nameof(ReduceMotion),
        nameof(FollowsWindows), nameof(ReduceMotionNote), nameof(Parallax), nameof(ParallaxAvailable),
    ];
}
