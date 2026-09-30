using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// An on or off switch whose knob springs across, as the HTML's <c>.switch</c> (Aero look design §1, the Glass settings):
/// the track fills with the accent over 300 ms and the knob travels 18 px on the spring. Under reduced motion the knob
/// jumps and the fill keeps a short fade. A screen reader hears a toggle button, on or off, by its AutomationProperties name.
/// </summary>
public sealed class GlassSwitch : ToggleButton
{
    /// <summary>How far the knob travels: the Styles board's 52 px track less its 24 px knob and their 3 px insets.</summary>
    public const double Travel = 22;

    private FrameworkElement? _on;
    private TranslateTransform? _knob;
    private bool _shown;

    public GlassSwitch()
    {
        // A value that arrives before the switch is first drawn (a binding, the usual order) puts the knob in place; only a
        // toggle after that moves it.
        Loaded += (_, _) => Dispatcher.InvokeAsync(() => _shown = IsLoaded, System.Windows.Threading.DispatcherPriority.Input);
        Unloaded += (_, _) => _shown = false;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _on = GetTemplateChild("PART_On") as FrameworkElement;
        if (GetTemplateChild("PART_Knob") is FrameworkElement knob)
        {
            // A template's transform is frozen with it; the knob needs one of its own to move.
            _knob = new TranslateTransform();
            knob.RenderTransform = _knob;
        }
        Sync(false);
    }

    /// <summary>Where the knob sits, for a test: 0 off, <see cref="Travel"/> on.</summary>
    internal double KnobOffset => _knob?.X ?? 0;

    /// <summary>Whether a change now springs the knob across: once the switch has been drawn.</summary>
    internal bool Shown => _shown;

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        Sync(_shown);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        Sync(_shown);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ToggleButtonAutomationPeer(this);

    private void Sync(bool animate)
    {
        var on = IsChecked == true;
        if (_on != null) AeroMotion.Fade(_on, OpacityProperty, on ? 1 : 0, animate ? AeroMotion.SwitchFill : 0, AeroMotion.Ease);
        if (_knob != null) AeroMotion.Move(_knob, TranslateTransform.XProperty, on ? Travel : 0, animate ? AeroMotion.Switch : 0, AeroMotion.Spring);
    }
}
