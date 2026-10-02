using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// A liquid glass piece (0.10.9, the owner's recipe; Styles.Aero.xaml draws it): every Aero glass surface is one, from a
/// pane to a pill, a bubble, a menu or the grab bar. Its template lays, bottom to top:
/// <list type="number">
/// <item>the engine's <see cref="LiquidGlassBackdrop"/>: what is behind the window, brightened, blurred and bent by the
/// turbulence, clipped to the piece;</item>
/// <item>the shadows (<see cref="GlassShadow"/>): the recipe's drop shadow of what the piece paints, and a bubble's own
/// soft shadow under it (<see cref="Bubble"/>);</item>
/// <item>the tint (Background): none by default, the lime of Open report, the Dark or Colour style's;</item>
/// <item>the glowing edge (<see cref="GlassGlow"/>, <see cref="HasGlow"/>);</item>
/// <item>the content, above the glow so its text reads.</item>
/// </list>
/// Nothing here runs at rest: the edge and the shadows are drawn once, again only when the piece's size or the glass
/// changes.
/// </summary>
public class GlassPanel : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius),
        typeof(CornerRadius), typeof(GlassPanel), new PropertyMetadata(new CornerRadius(LiquidGlassRecipe.CornerRadius)));

    public static readonly DependencyProperty FrostProperty = DependencyProperty.Register(nameof(Frost), typeof(Brush),
        typeof(GlassPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty HasGlowProperty = DependencyProperty.Register(nameof(HasGlow),
        typeof(bool), typeof(GlassPanel), new PropertyMetadata(true));

    public static readonly DependencyProperty HasShadowProperty = DependencyProperty.Register(nameof(HasShadow),
        typeof(bool), typeof(GlassPanel), new PropertyMetadata(true));

    public static readonly DependencyProperty BubbleProperty = DependencyProperty.Register(nameof(Bubble),
        typeof(bool), typeof(GlassPanel), new PropertyMetadata(false));

    public static readonly DependencyProperty LiftProperty = DependencyProperty.Register(nameof(Lift),
        typeof(GlassLift?), typeof(GlassPanel), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey IsNestedKey = DependencyProperty.RegisterReadOnly(nameof(IsNested),
        typeof(bool), typeof(GlassPanel), new PropertyMetadata(false));

    public static readonly DependencyProperty IsNestedProperty = IsNestedKey.DependencyProperty;

    private static readonly List<GlassPanel> Panes = [];

    public GlassPanel()
    {
        Focusable = false;
        Loaded += (_, _) =>
        {
            if (!Panes.Contains(this)) Panes.Add(this);
            SetValue(IsNestedKey, Outer(this) is not null);
        };
        Unloaded += (_, _) => Panes.Remove(this);
    }

    /// <summary>The piece's corners: the recipe's 28 for a pane; a capsule binds half its height (Capsule).</summary>
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>What earlier versions laid under the tint: a frosted copy of the wallpaper (WallpaperFrost). Since 0.10.9 the
    /// engine's backdrop is the glass's only picture of what is behind it, and the template draws no frost.</summary>
    public Brush? Frost { get => (Brush?)GetValue(FrostProperty); set => SetValue(FrostProperty, value); }

    /// <summary>Whether the piece draws the recipe's glowing edge; false for a bare hit area (a scroll bar's host).</summary>
    public bool HasGlow { get => (bool)GetValue(HasGlowProperty); set => SetValue(HasGlowProperty, value); }

    /// <summary>Whether the piece draws the recipe's drop shadow.</summary>
    public bool HasShadow { get => (bool)GetValue(HasShadowProperty); set => SetValue(HasShadowProperty, value); }

    /// <summary>A bubble floating on glass (the chosen page, a segmented control's choice, a figure): the mockup's
    /// <c>.bubble</c>, a soft shadow under it as well.</summary>
    public bool Bubble { get => (bool)GetValue(BubbleProperty); set => SetValue(BubbleProperty, value); }

    /// <summary>The piece's own box-shadow when it isn't a bubble's (<see cref="GlassLift.Pill"/>, <see cref="GlassLift.Bar"/>).</summary>
    public GlassLift? Lift { get => (GlassLift?)GetValue(LiftProperty); set => SetValue(LiftProperty, value); }

    /// <summary>Whether the piece sits on another piece's glass, whose content's text shadow already reaches it.</summary>
    public bool IsNested => (bool)GetValue(IsNestedProperty);

    /// <summary>Every loaded piece on this thread, so a window's shape can follow each one.</summary>
    public static IReadOnlyList<GlassPanel> Live => Panes;

    /// <summary>The piece <paramref name="pane"/> sits on, or null for one on its own.</summary>
    internal static GlassPanel? Outer(DependencyObject pane)
    {
        for (var node = VisualTreeHelper.GetParent(pane); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is GlassPanel outer) return outer;
        }
        return null;
    }
}
