using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// The wash a Midnight button lights under the pointer (plan O 0.5): a layer in the button's template that fades in, so
/// hover animates an opacity and never a palette brush. Each button style names the layer's brush and how far it comes
/// up, which is what tells a primary button's white wash from a quiet button's raised one under a single template.
/// </summary>
internal static class Wash
{
    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached(
        "Brush", typeof(Brush), typeof(Wash), new PropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty StrengthProperty = DependencyProperty.RegisterAttached(
        "Strength", typeof(double), typeof(Wash), new PropertyMetadata(1.0));

    /// <summary>The face's corners, which the wash shares: a control's radius, or a pill's for the round icon button.</summary>
    public static readonly DependencyProperty CornerProperty = DependencyProperty.RegisterAttached(
        "Corner", typeof(CornerRadius), typeof(Wash), new PropertyMetadata(new CornerRadius(8)));

    public static Brush GetBrush(DependencyObject element) => (Brush)element.GetValue(BrushProperty);

    public static void SetBrush(DependencyObject element, Brush value) => element.SetValue(BrushProperty, value);

    public static double GetStrength(DependencyObject element) => (double)element.GetValue(StrengthProperty);

    public static void SetStrength(DependencyObject element, double value) => element.SetValue(StrengthProperty, value);

    public static CornerRadius GetCorner(DependencyObject element) => (CornerRadius)element.GetValue(CornerProperty);

    public static void SetCorner(DependencyObject element, CornerRadius value) => element.SetValue(CornerProperty, value);
}
