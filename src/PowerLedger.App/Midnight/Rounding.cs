using System.Windows;
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>
/// A pill's ends. The token M.Radius.Pill is 999, but a WPF Border clamps each corner's radius to half the width and half
/// the height separately, so a large radius on a wide, short Border draws an ellipse rather than a stadium. A Border with
/// Pill set keeps its corners at half its height as it is sized, which is the shape the token means.
/// </summary>
internal static class Rounding
{
    public static readonly DependencyProperty PillProperty = DependencyProperty.RegisterAttached(
        "Pill", typeof(bool), typeof(Rounding), new PropertyMetadata(false, OnPillChanged));

    public static bool GetPill(DependencyObject element) => (bool)element.GetValue(PillProperty);

    public static void SetPill(DependencyObject element, bool value) => element.SetValue(PillProperty, value);

    private static void OnPillChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Border border) return;
        if (e.NewValue is true)
        {
            border.SizeChanged += Round;
            Round(border, null);
        }
        else
        {
            border.SizeChanged -= Round;
        }
    }

    private static void Round(object sender, SizeChangedEventArgs? e)
    {
        var border = (Border)sender;
        if (border.ActualHeight > 0) border.CornerRadius = new CornerRadius(border.ActualHeight / 2);
    }
}
