using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PowerLedger.App;

/// <summary>
/// Where a Midnight page shows (plan O 0.5): two presenters that take turns, so when the page changes the one leaving
/// fades out (Fast) while the one arriving fades in and rises 8 px (Base, ease-out), and the old view stays alive until
/// its fade ends. The templates come from the window's resources, as a ContentControl's would. Under reduced motion the
/// fades keep 120 ms and nothing moves.
/// </summary>
internal sealed class PageHost : Grid
{
    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
        nameof(Content), typeof(object), typeof(PageHost), new PropertyMetadata(null, OnContentChanged));

    private const double Rise = 8;

    private readonly ContentPresenter _a = new() { Focusable = false };
    private readonly ContentPresenter _b = new() { Focusable = false };
    private ContentPresenter _showing;

    public PageHost()
    {
        _showing = _a;
        _b.Opacity = 0;
        _b.Visibility = Visibility.Collapsed;
        foreach (var presenter in new[] { _a, _b }) presenter.RenderTransform = new TranslateTransform();
        Children.Add(_a);
        Children.Add(_b);
    }

    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }

    /// <summary>The presenter the page is in, for a test to find the view under.</summary>
    internal ContentPresenter Showing => _showing;

    private static void OnContentChanged(DependencyObject element, DependencyPropertyChangedEventArgs e) => ((PageHost)element).Show(e.NewValue);

    private void Show(object? content)
    {
        var leaving = _showing;
        var arriving = leaving == _a ? _b : _a;
        if (leaving.Content is null)
        {
            // The first page: nothing to leave, so nothing to fade from.
            leaving.Content = content;
            leaving.Visibility = Visibility.Visible;
            leaving.Opacity = 1;
            return;
        }
        _showing = arriving;
        arriving.Content = content;
        arriving.Visibility = Visibility.Visible;
        SetZIndex(arriving, 1);
        SetZIndex(leaving, 0);

        var fadeOut = new DoubleAnimation(0, Motion.Fade(Motion.Fast)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fadeOut.Completed += (_, _) =>
        {
            if (_showing == leaving) return;   // it came back before the fade ended
            leaving.Visibility = Visibility.Collapsed;
            leaving.Content = null;
        };
        leaving.BeginAnimation(OpacityProperty, fadeOut);

        arriving.Opacity = 0;
        arriving.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Motion.Fade(Motion.Base)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var rise = (TranslateTransform)arriving.RenderTransform;
        rise.BeginAnimation(TranslateTransform.YProperty, Motion.Reduced
            ? new DoubleAnimation(0, TimeSpan.Zero)
            : new DoubleAnimation(Rise, 0, Motion.Of(Motion.Base)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
