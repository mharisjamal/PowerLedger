using System.Windows;

namespace PowerLedger.App.Aero;

/// <summary>
/// How a glass button's capsule casts (0.10.9's audit): the kit's .pill has a box-shadow of its own
/// (<see cref="GlassLift.Pill"/>) and no drop-shadow filter, the default; the Dashboard's lime Open report is the Main
/// board's .lg.cap, the recipe's drop shadow of its lime and no box-shadow (A.AccentBtn sets both).
/// </summary>
public static class GlassButton
{
    public static readonly DependencyProperty CastsProperty = DependencyProperty.RegisterAttached("Casts", typeof(bool), typeof(GlassButton),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty LiftProperty = DependencyProperty.RegisterAttached("Lift", typeof(GlassLift?), typeof(GlassButton),
        new FrameworkPropertyMetadata(GlassLift.Pill));

    public static bool GetCasts(DependencyObject element) => (bool)element.GetValue(CastsProperty);

    public static void SetCasts(DependencyObject element, bool value) => element.SetValue(CastsProperty, value);

    public static GlassLift? GetLift(DependencyObject element) => (GlassLift?)element.GetValue(LiftProperty);

    public static void SetLift(DependencyObject element, GlassLift? value) => element.SetValue(LiftProperty, value);
}
