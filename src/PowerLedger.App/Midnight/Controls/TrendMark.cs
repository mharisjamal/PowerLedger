using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>
/// A trend (plan O M1-2): ▲ in green for a rise, ▼ in red for a fall, — in muted ink for no change, each with a few
/// words; words alone for <see cref="TrendKind.Text"/>; nothing for <see cref="TrendKind.Quality"/>, whose slot the view
/// gives to a chip. Its template is the implicit local:TrendMark style in Styles.Midnight.xaml.
/// </summary>
internal sealed class TrendMark : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(TrendKind), typeof(TrendMark), new PropertyMetadata(TrendKind.Flat));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TrendMark), new PropertyMetadata(string.Empty));

    public TrendMark()
    {
        Focusable = false;
    }

    public TrendKind Kind { get => (TrendKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>The mark for a kind: the arrows and the dash, or nothing.</summary>
    internal static string MarkFor(TrendKind kind) => kind switch
    {
        TrendKind.Up => "▲",
        TrendKind.Down => "▼",
        TrendKind.Flat => "—",
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
