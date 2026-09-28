using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace PowerLedger.App.Aero;

/// <summary>
/// The demo's segmented pills (Aero look design §1; Plan S, P1): a hairline capsule holding <c>A.SegItem</c> radio
/// buttons, with one white pill behind the chosen one that slides and stretches to the next on the spring, rather than
/// each pill lighting on its own ("tab active indicator morphs between positions", CLAUDE.md). The pill is placed without
/// travel when the track is laid out or resized, and under reduced motion jumps (AeroMotion.Move). It moves a transform
/// and a width only, so nothing is re-rendered but the pill.
/// </summary>
internal sealed class SegTrack : ContentControl
{
    public static readonly DependencyProperty IndicatorBrushProperty = DependencyProperty.Register(
        nameof(IndicatorBrush), typeof(Brush), typeof(SegTrack), new PropertyMetadata(Brushes.White));

    private FrameworkElement? _host;
    private FrameworkElement? _indicator;
    private TranslateTransform? _shift;

    public SegTrack()
    {
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Later(animate: true)));
        Loaded += (_, _) => Later(animate: false);
        SizeChanged += (_, _) => Place(animate: false);
    }

    /// <summary>The sliding pill's fill, the demo's near-white <c>--pill</c>.</summary>
    public Brush IndicatorBrush { get => (Brush)GetValue(IndicatorBrushProperty); set => SetValue(IndicatorBrushProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _host = GetTemplateChild("PART_Host") as FrameworkElement;
        _indicator = GetTemplateChild("PART_Indicator") as FrameworkElement;
        if (_indicator is not null) _indicator.RenderTransform = _shift = new TranslateTransform();
        Place(animate: false);
    }

    /// <summary>The chosen pill, or null: the first visible checked radio button in the track.</summary>
    internal RadioButton? Chosen => _host is null ? null : Checked(_host);

    /// <summary>Where the pill sits now, in the track's host: its offset and width (for tests).</summary>
    internal (double X, double Width) Indicator => (_shift?.X ?? 0, _indicator?.Width ?? 0);

    /// <summary>After the check has been laid out, so the chosen button's place is known.</summary>
    private void Later(bool animate)
        => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Place(animate && IsLoaded));

    private void Place(bool animate)
    {
        if (_host is null || _indicator is null || _shift is null) return;
        if (Chosen is not { } chosen || !chosen.IsVisible || chosen.ActualWidth <= 0)
        {
            _indicator.Visibility = Visibility.Hidden;
            return;
        }
        var x = chosen.TranslatePoint(default, _host).X;
        var width = chosen.ActualWidth;
        var shown = _indicator.Visibility == Visibility.Visible;
        _indicator.Visibility = Visibility.Visible;
        var ms = animate && shown ? AeroMotion.SegPill : 0;
        AeroMotion.Move(_shift, TranslateTransform.XProperty, x, ms, AeroMotion.Spring);
        AeroMotion.Move(_indicator, WidthProperty, width, ms, AeroMotion.Spring);
    }

    private static RadioButton? Checked(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton { IsChecked: true, IsVisible: true } button) return button;
            if (Checked(child) is { } deeper) return deeper;
        }
        return null;
    }
}
