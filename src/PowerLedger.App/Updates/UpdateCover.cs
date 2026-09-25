using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PowerLedger.App;

/// <summary>
/// The blocking "PowerLedger needs an update" panel both looks lay over their pages (Plan Q §3), bound to
/// <see cref="Updater.UpdateRequired"/>. While it shows, what it covers is out of the keyboard's reach and Update now has
/// the focus; it fades in, quickly under reduced motion, rather than cutting in.
/// </summary>
internal static class UpdateCover
{
    /// <summary>Wires <paramref name="cover"/> to disable <paramref name="covered"/> and focus <paramref name="updateNow"/>
    /// whenever it shows.</summary>
    public static void Attach(FrameworkElement cover, IReadOnlyList<UIElement> covered, Button updateNow)
    {
        cover.IsVisibleChanged += (_, e) =>
        {
            var shown = (bool)e.NewValue;
            foreach (var element in covered) element.IsEnabled = !shown;
            if (!shown) return;
            cover.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion.Fade(Motion.Base)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            cover.Dispatcher.BeginInvoke(() => updateNow.Focus(), DispatcherPriority.Input);
        };
    }
}
