using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>Energy each day's scale, pure: how tall a bar stands as a share of the room it has. The mockup's busiest bar
/// stands at 90 % of the well's room (294 of 326 px), every other in proportion, and an empty one is a sliver.</summary>
internal static class BarScale
{
    public const double Tallest = 0.9;
    public const double Least = 0.03;

    public static double Share(double kwh, double busiest)
        => busiest > 0 && double.IsFinite(kwh) ? Math.Max(Least, Math.Clamp(kwh / busiest, 0, 1) * Tallest) : Least;
}

/// <summary>
/// Energy each day (0.10.9, the liquid glass mockup's): fourteen glass bars side by side, 10 apart, each a capsule of the
/// recipe's glass (14 corners) standing from the well's foot in proportion to its period's energy, the latest in the
/// accent's glass (lime at 78 %), each period's label under it in the mockup's .sub at 11; a bar's tip names its period
/// and its cost or energy. The bars are laid out by the grid's star rows, so nothing measures; the intro grows them from
/// their feet (<see cref="Grow"/>), and nothing runs at rest.
/// </summary>
internal sealed class EnergyBarChart : Grid
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(nameof(Bars), typeof(IReadOnlyList<EnergyBar>),
        typeof(EnergyBarChart), new PropertyMetadata(null, (d, _) => ((EnergyBarChart)d).Build()));

    /// <summary>The accent, followed live (GlassMaterial repaints it), for the latest bar's glass.</summary>
    private static readonly DependencyProperty AccentProperty = DependencyProperty.Register("Accent", typeof(Color),
        typeof(EnergyBarChart), new PropertyMetadata(Color.FromRgb(0xD9, 0xF2, 0x5A), (d, _) => ((EnergyBarChart)d).Tint()));

    /// <summary>The mockup's lime at 78 % on the latest bar.</summary>
    public const double LatestAlpha = 0.78;

    private const double Gap = 10;
    private const double LabelGap = 8;
    private readonly List<GlassPanel> _bars = [];
    private readonly TextBlock _empty;

    public EnergyBarChart()
    {
        SetResourceReference(AccentProperty, "A.C.Accent");
        AutomationProperties.SetName(this, "Energy each day");
        _empty = new TextBlock { Text = "No history yet", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _empty.SetResourceReference(StyleProperty, "A.Text.Secondary");
        Build();
    }

    /// <summary>The fourteen periods, oldest first.</summary>
    public IReadOnlyList<EnergyBar>? Bars { get => (IReadOnlyList<EnergyBar>?)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }

    /// <summary>The bars' glass, oldest first, for a test and the intro.</summary>
    internal IReadOnlyList<GlassPanel> BarPieces => _bars;

    /// <summary>The bars grow from their feet, each <paramref name="stagger"/> ms after the one before, over
    /// <paramref name="ms"/>; under reduced motion they are simply there.</summary>
    internal void Grow(double ms, double stagger)
    {
        for (var i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            var scale = new ScaleTransform(1, 0);
            bar.RenderTransformOrigin = new Point(.5, 1);
            bar.RenderTransform = scale;
            AeroMotion.Move(scale, ScaleTransform.ScaleYProperty, 1, ms, AeroMotion.Glide, i * stagger, from: 0,
                done: () => bar.RenderTransform = Transform.Identity);
        }
    }

    /// <summary>The bars held at nothing, before the intro grows them.</summary>
    internal void Hold()
    {
        foreach (var bar in _bars)
        {
            bar.RenderTransformOrigin = new Point(.5, 1);
            bar.RenderTransform = new ScaleTransform(1, 0);
        }
    }

    private void Build()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        _bars.Clear();
        var bars = Bars ?? [];
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(LabelGap) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var busiest = bars.Select(b => b.Kwh).DefaultIfEmpty(0).Max();
        var culture = CultureInfo.CurrentCulture;
        for (var i = 0; i < bars.Count; i++)
        {
            if (i > 0) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gap) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var column = i * 2;
            var share = BarScale.Share(bars[i].Kwh, busiest);
            var cell = new Grid();
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - share, GridUnitType.Star) });
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(share, GridUnitType.Star) });
            var bar = new GlassPanel { CornerRadius = new CornerRadius(14), Bubble = true, ToolTip = bars[i].Tip, Focusable = false };
            AutomationProperties.SetName(bar, bars[i].Tip);
            SetRow(bar, 1);
            cell.Children.Add(bar);
            SetColumn(cell, column);
            Children.Add(cell);
            _bars.Add(bar);
            var label = new TextBlock { Text = bars[i].Label, FontSize = 11, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center };
            label.SetResourceReference(StyleProperty, "A.Text.Secondary");
            SetRow(label, 2);
            SetColumn(label, column);
            Children.Add(label);
        }
        _empty.Visibility = bars.Count == 0 || busiest <= 0 ? Visibility.Visible : Visibility.Collapsed;
        SetColumnSpan(_empty, Math.Max(1, ColumnDefinitions.Count));
        Children.Add(_empty);
        AutomationProperties.SetHelpText(this, bars.Count == 0 ? Format.NoReading
            : $"{Format.Kwh(bars.Sum(b => b.Kwh), culture)} kWh over {bars.Count} periods, the most {Format.Kwh(busiest, culture)} kWh");
        Tint();
    }

    /// <summary>The latest bar in the accent's glass; the rest clear.</summary>
    private void Tint()
    {
        if (_bars.Count == 0) return;
        var accent = (Color)GetValue(AccentProperty);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(LatestAlpha * 255), accent.R, accent.G, accent.B));
        brush.Freeze();
        for (var i = 0; i < _bars.Count; i++)
        {
            if (i == _bars.Count - 1) _bars[i].Background = brush;
            else _bars[i].Background = Brushes.Transparent;
        }
    }
}
