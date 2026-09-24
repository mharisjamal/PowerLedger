using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>
/// A trend (plan O M1-2): ▲ for a rise, ▼ for a fall, ● for no change, each with a few words; words alone for
/// <see cref="TrendKind.Text"/>; nothing for <see cref="TrendKind.Quality"/>, whose slot the view gives to a chip. The
/// arrow says which way the figure went; the colour says whether that is good news (<see cref="Sense"/>): green or red,
/// the other way round where <see cref="LowerIsBetter"/>, muted for neither. Its template is the implicit local:TrendMark
/// style in Styles.Midnight.xaml.
/// </summary>
internal sealed class TrendMark : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(TrendKind), typeof(TrendMark), new PropertyMetadata(TrendKind.Flat, OnSenseChanged));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TrendMark), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty LowerIsBetterProperty = DependencyProperty.Register(
        nameof(LowerIsBetter), typeof(bool), typeof(TrendMark), new PropertyMetadata(false, OnSenseChanged));
    private static readonly DependencyPropertyKey SenseKey = DependencyProperty.RegisterReadOnly(
        nameof(Sense), typeof(TrendSense), typeof(TrendMark), new PropertyMetadata(TrendSense.Neutral));
    public static readonly DependencyProperty SenseProperty = SenseKey.DependencyProperty;

    public TrendMark()
    {
        Focusable = false;
    }

    public TrendKind Kind { get => (TrendKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>Whether a fall is the good news, as it is for energy.</summary>
    public bool LowerIsBetter { get => (bool)GetValue(LowerIsBetterProperty); set => SetValue(LowerIsBetterProperty, value); }

    /// <summary>Good news, bad or neither, by <see cref="DashboardMaths.Sense"/>; the template colours by it.</summary>
    public TrendSense Sense => (TrendSense)GetValue(SenseProperty);

    private static void OnSenseChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        var mark = (TrendMark)element;
        mark.SetValue(SenseKey, DashboardMaths.Sense(mark.Kind, mark.LowerIsBetter));
    }

    /// <summary>The mark for a kind: an arrowhead up or down, a dot for level, or nothing.</summary>
    internal static string MarkFor(TrendKind kind) => kind switch
    {
        TrendKind.Up => "▲",
        TrendKind.Down => "▼",
        TrendKind.Flat => "●",
        _ => string.Empty,
    };

    internal string Describe() => Kind switch
    {
        TrendKind.Up => $"Up {Text}",
        TrendKind.Down => $"Down {Text}",
        TrendKind.Flat => $"Unchanged {Text}".TrimEnd(),
        TrendKind.Text => Text,
        _ => string.Empty,
    };

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(TrendMark owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(TrendMark);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((TrendMark)Owner).Describe() : name;
        }
    }
}
