using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>What a status pill's dot says: all well, a caution, or a fault.</summary>
internal enum StatusKind
{
    Good,
    Warn,
    Bad,
}

/// <summary>
/// The page header's status (plan O M1-2): a dot and a few words on a pill, in one of the three M.StatusPill styles by
/// <see cref="Kind"/>. Given the service's <see cref="Running"/> and the live <see cref="Quality"/> it chooses its own
/// words: Recording, Estimating, or Service not running, the states the Classic status bar shows.
/// </summary>
internal sealed class StatusPill : ContentControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusKind), typeof(StatusPill), new PropertyMetadata(StatusKind.Good, OnKindChanged));
    public static readonly DependencyProperty RunningProperty = DependencyProperty.Register(
        nameof(Running), typeof(bool), typeof(StatusPill), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty QualityProperty = DependencyProperty.Register(
        nameof(Quality), typeof(Quality?), typeof(StatusPill), new PropertyMetadata(null, OnStateChanged));

    public StatusPill()
    {
        Focusable = false;
        OnStateChanged(this, default);   // the words for the defaults, not running: a later Content or Kind of the view's own overrides them
    }

    public StatusKind Kind { get => (StatusKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public bool Running { get => (bool)GetValue(RunningProperty); set => SetValue(RunningProperty, value); }

    public Quality? Quality { get => (Quality?)GetValue(QualityProperty); set => SetValue(QualityProperty, value); }

    /// <summary>The pill for a service state: not running is a fault; running on estimates a caution; anything else all well.</summary>
    internal static (StatusKind Kind, string Text) For(bool running, Quality? quality) => running
        ? quality == Contracts.Quality.Estimated ? (StatusKind.Warn, "Estimating") : (StatusKind.Good, "Recording")
        : (StatusKind.Bad, "Service not running");

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private static void OnKindChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        => ((StatusPill)element).SetResourceReference(StyleProperty, "M.StatusPill." + e.NewValue);

    private static void OnStateChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        var pill = (StatusPill)element;
        var (kind, text) = For(pill.Running, pill.Quality);
        pill.Kind = kind;
        pill.Content = text;
    }

    private sealed class Peer(StatusPill owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(StatusPill);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((StatusPill)Owner).Content?.ToString() ?? string.Empty : name;
        }
    }
}
