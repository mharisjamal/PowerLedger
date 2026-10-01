using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerLedger.App.Aero;

/// <summary>One of the Styles board's four cards: its style, its name, the badge on its pill, its note, its glass (the
/// board's tint for the style, the Colour one in the user's hue), the watts now, and whether it is the style chosen.</summary>
internal sealed partial class StyleCard(GlassStyle style, string name, string badge, string note) : ObservableObject
{
    public GlassStyle Style { get; } = style;

    public string Name { get; } = name;

    public string Badge { get; } = badge;

    public string Note { get; } = note;

    [ObservableProperty]
    private Brush _tint = Brushes.Transparent;

    [ObservableProperty]
    private string _watts = Format.Missing;

    [ObservableProperty]
    private string _unit = "W";

    [ObservableProperty]
    private bool _isChosen;

    /// <summary>The board's tint for <paramref name="style"/>: white at 2 % (Clear) or 10 % (Tinted), the smoked navy at
    /// 55 % (Dark), or <paramref name="hue"/> at 36 % (Colour), whatever the strength, so the four show apart.</summary>
    public static Color TintOf(GlassStyle style, string hue)
    {
        static Color Alpha(Color c, double a) => Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B);
        return style switch
        {
            GlassStyle.Clear => Alpha(Colors.White, GlassMaterial.ClearWhite),
            GlassStyle.Tinted => Alpha(Colors.White, GlassMaterial.TintedWhite),
            GlassStyle.Dark => GlassMaterial.DarkTint,
            _ => Alpha((Color)ColorConverter.ConvertFromString(hue), GlassMaterial.ColourAlpha),
        };
    }
}

/// <summary>
/// Aero's Settings page (Aero look design §1 and §3; 0.10.9, the liquid glass mockup's Styles board) over <see
/// cref="SettingsViewModel"/>: the four glass styles as cards with the watts now in each, the Glass pane in two columns,
/// then every section the other looks have (overlay, tariff, machine, sampling, calibration, preferences with the theme,
/// the look, the intro and the tour, privacy, household, about), each a glass pane. Enter in a box of the service's
/// settings saves through <see cref="SettingsEntry"/>, as in the other looks.
/// </summary>
internal partial class SettingsView : UserControl
{
    private readonly StyleCard[] _cards =
    [
        new(GlassStyle.Clear, "Clear", "Most see-through", "Thin glass. A light blur and a bright rim, so the desktop reads clearly through it."),
        new(GlassStyle.Tinted, "Tinted", "Default", "Frosted glass. A soft white body with a strong blur, the demo look."),
        new(GlassStyle.Dark, "Dark", "Smoked", "Smoked glass. The rim and sheen stay, the body darkens for calm reading."),
        new(GlassStyle.Colour, "Colour", "Your hue", "Tinted with your colour. Same thickness and light, a coloured body."),
    ];

    private SettingsViewModel? _settings;
    private NowViewModel? _now;

    public SettingsView()
    {
        InitializeComponent();
        StyleCards.ItemsSource = _cards;
        DataContextChanged += (_, _) => Follow(DataContext as SettingsViewModel);
        Loaded += (_, _) =>
        {
            Follow(DataContext as SettingsViewModel);
            if (Window.GetWindow(this)?.DataContext is ShellViewModel { Now: { } now } && _now != now)
            {
                if (_now is not null) _now.PropertyChanged -= OnNowChanged;
                _now = now;
                now.PropertyChanged += OnNowChanged;
            }
            ShowWatts();
        };
        Unloaded += (_, _) =>
        {
            if (_now is not null) _now.PropertyChanged -= OnNowChanged;
            _now = null;
            Follow(null);
        };
    }

    /// <summary>The cards, for a test.</summary>
    internal IReadOnlyList<StyleCard> Cards => _cards;

    private void Follow(SettingsViewModel? settings)
    {
        if (_settings == settings) return;
        if (_settings is not null) _settings.PropertyChanged -= OnSettingsChanged;
        _settings = settings;
        if (settings is not null) settings.PropertyChanged += OnSettingsChanged;
        ShowCards();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Glass)) ShowCards();
    }

    private void OnNowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowViewModel.Live)) ShowWatts();
    }

    /// <summary>Each card's glass, and which is chosen.</summary>
    private void ShowCards()
    {
        var glass = _settings?.Glass ?? GlassSettings.Default;
        foreach (var card in _cards)
        {
            var brush = new SolidColorBrush(StyleCard.TintOf(card.Style, glass.TintColor));
            brush.Freeze();
            card.Tint = brush;
            card.IsChosen = card.Style == glass.Style;
        }
    }

    /// <summary>The watts now in every card, as the Dashboard's Power now has them.</summary>
    private void ShowWatts()
    {
        var watts = _now?.Live.Watts ?? double.NaN;
        var (value, unit) = DashboardFigures.PowerNow(watts, false, null, null, CultureInfo.CurrentCulture);
        foreach (var card in _cards)
        {
            card.Watts = value;
            card.Unit = unit;
        }
    }

    /// <summary>A card clicked or chosen from the keyboard: its style, through the Glass section, which saves it.</summary>
    private void StyleCardChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StyleCard card } && _settings is { } settings && settings.GlassSection.Style != card.Style)
            settings.GlassSection.Style = card.Style;
    }

    /// <summary>Watch the Aero intro (Plan S intro): the window's intro dialog, out of the button.</summary>
    private void WatchIntroClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as AeroWindow)?.WatchIntroVideo(WatchIntroButton);

    /// <summary>The demo's Replay intro: the window's opening again, on the Dashboard.</summary>
    private void ReplayIntroClick(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as AeroWindow)?.ReplayIntro();

    /// <summary>The demo's Play tour: the camera on the Dashboard's panes in turn.</summary>
    private void PlayTourClick(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as AeroWindow)?.PlayTour();
}

/// <summary>True when the two values are the same text, ignoring case: a preset's swatch is ticked while it is the
/// Colour style's tint.</summary>
internal sealed class SameText : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is string a && values[1] is string b && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [Binding.DoNothing, Binding.DoNothing];
}
