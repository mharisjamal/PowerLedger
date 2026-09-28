using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// A Liquid Glass pane (Aero look design §3), drawn by its style in Styles.Aero.xaml: the frost (the blurred scene
/// behind, when the window paints its own backdrop: <see cref="Frost"/>), a light tint, a crisp light rim with a faint dark
/// line inside it, a soft sheen along the top, a sheen that follows the pointer, and a shadow drawn only outside the pane.
/// The shadow is cached as a bitmap and clipped to the outside once per size, so live content inside never re-blurs it;
/// the pointer sheen moves only while the pointer is over the pane. Nothing here runs at rest.
/// </summary>
public class GlassPanel : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius),
        typeof(CornerRadius), typeof(GlassPanel), new PropertyMetadata(new CornerRadius(26), (d, _) => ((GlassPanel)d).UpdateClip()));

    public static readonly DependencyProperty FrostProperty = DependencyProperty.Register(nameof(Frost), typeof(Brush),
        typeof(GlassPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty SheenEnabledProperty = DependencyProperty.Register(nameof(SheenEnabled),
        typeof(bool), typeof(GlassPanel), new PropertyMetadata(true));

    public static readonly DependencyProperty HasShadowProperty = DependencyProperty.Register(nameof(HasShadow),
        typeof(bool), typeof(GlassPanel), new PropertyMetadata(true, (d, _) => ((GlassPanel)d).UpdateClip()));

    private static readonly List<GlassPanel> Panes = [];

    private FrameworkElement? _shadow;
    private Border? _sheen;
    private RadialGradientBrush? _sheenBrush;

    public GlassPanel()
    {
        Focusable = false;
        Loaded += (_, _) =>
        {
            if (!Panes.Contains(this)) Panes.Add(this);
        };
        Unloaded += (_, _) => Panes.Remove(this);
        SizeChanged += (_, _) => UpdateClip();
    }

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>The frosted scene behind the pane, lined up with it by the backdrop (WallpaperFrost); null when the
    /// window is see-through and the system backdrop does the blur, or the backdrop is plain.</summary>
    public Brush? Frost { get => (Brush?)GetValue(FrostProperty); set => SetValue(FrostProperty, value); }

    public bool SheenEnabled { get => (bool)GetValue(SheenEnabledProperty); set => SetValue(SheenEnabledProperty, value); }

    public bool HasShadow { get => (bool)GetValue(HasShadowProperty); set => SetValue(HasShadowProperty, value); }

    /// <summary>Every loaded pane on this thread, so a window's backdrop can line each one's frost up with the scene.</summary>
    public static IReadOnlyList<GlassPanel> Live => Panes;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _shadow = GetTemplateChild("PART_Shadow") as FrameworkElement;
        _sheen = GetTemplateChild("PART_Sheen") as Border;
        _sheenBrush = null;
        UpdateClip();
    }

    /// <summary>Fades the pointer sheen out, as a pane does when the camera leaves it.</summary>
    public void HideSheen()
    {
        if (_sheen != null) AeroMotion.Fade(_sheen, OpacityProperty, 0, AeroMotion.Sheen, AeroMotion.Glide);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_sheen == null || !SheenEnabled) return;
        _sheen.Background = SheenBrush();
        AeroMotion.Fade(_sheen, OpacityProperty, 1, AeroMotion.Sheen, AeroMotion.Glide);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HideSheen();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_sheenBrush == null || !SheenEnabled) return;
        var p = e.GetPosition(this);
        _sheenBrush.Center = p;
        _sheenBrush.GradientOrigin = p;
    }

    /// <summary>The HTML's <c>radial-gradient(420px circle at the pointer, sheen, transparent 55%)</c>, in the palette's
    /// colour of the moment (GlassMaterial may have changed it).</summary>
    private RadialGradientBrush SheenBrush()
    {
        var colour = TryFindResource("A.C.PointerSheen") is Color c ? c : Color.FromArgb(0x1A, 255, 255, 255);
        var radius = TryFindResource("A.Glass.SheenRadius") is double r ? r : 420;
        if (_sheenBrush is { } existing && existing.GradientStops[0].Color == colour && existing.RadiusX == radius) return existing;
        var stops = new GradientStopCollection { new(colour, 0), new(Color.FromArgb(0, colour.R, colour.G, colour.B), .55) };
        _sheenBrush = new RadialGradientBrush(stops) { MappingMode = BrushMappingMode.Absolute, RadiusX = radius, RadiusY = radius };
        return _sheenBrush;
    }

    /// <summary>Clips the shadow to the outside of the pane, so the tint shows the scene, not the shadow's own body.</summary>
    private void UpdateClip()
    {
        if (_shadow == null) return;
        if (!HasShadow)
        {
            _shadow.Visibility = Visibility.Collapsed;
            return;
        }
        _shadow.Visibility = Visibility.Visible;
        if (ActualWidth <= 0) return;
        var m = -_shadow.Margin.Left;
        var r = CornerRadius.TopLeft;
        var outer = new RectangleGeometry(new Rect(0, 0, ActualWidth + 2 * m, ActualHeight + 2 * m));
        var inner = new RectangleGeometry(new Rect(m, m, ActualWidth, ActualHeight), r, r);
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        clip.Freeze();
        _shadow.Clip = clip;
    }
}
