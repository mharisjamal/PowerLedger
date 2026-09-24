using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PowerLedger.App;

/// <summary>
/// A card's or tooltip's drop shadow from two tokens: the palette's M.Shadow brush and a depth (M.Elevation.*). A style
/// sets both; each element gets an effect of its own, so a hover can raise one card without touching the rest, and the
/// shadow's colour follows a palette swap through the DynamicResource on the brush. WPF's effect takes a colour, not a
/// brush, which is why the two tokens meet here rather than in XAML.
/// </summary>
internal static class Elevation
{
    public static readonly DependencyProperty ShadowProperty = DependencyProperty.RegisterAttached(
        "Shadow", typeof(Brush), typeof(Elevation), new PropertyMetadata(null, Changed));

    public static readonly DependencyProperty DepthProperty = DependencyProperty.RegisterAttached(
        "Depth", typeof(double), typeof(Elevation), new PropertyMetadata(0.0, Changed));

    public static Brush? GetShadow(DependencyObject element) => (Brush?)element.GetValue(ShadowProperty);

    public static void SetShadow(DependencyObject element, Brush? value) => element.SetValue(ShadowProperty, value);

    public static double GetDepth(DependencyObject element) => (double)element.GetValue(DepthProperty);

    public static void SetDepth(DependencyObject element, double value) => element.SetValue(DepthProperty, value);

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target) return;
        var depth = GetDepth(element);
        if (GetShadow(element) is not SolidColorBrush shadow || !(depth > 0))
        {
            if (target.Effect is DropShadowEffect) target.Effect = null;
            return;
        }
        if (target.Effect is not DropShadowEffect effect || effect.IsFrozen)
        {
            target.Effect = effect = new DropShadowEffect { Direction = 270, RenderingBias = RenderingBias.Performance };
        }
        var colour = shadow.Color;
        effect.Color = Color.FromRgb(colour.R, colour.G, colour.B);
        effect.Opacity = colour.A / 255.0;
        effect.ShadowDepth = depth / 3;
        effect.BlurRadius = depth * 1.5;
    }
}
