using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PowerLedger.App.Aero;

/// <summary>
/// A labelled figure in a well, as the demo's report and household dialogs list them (<c>.kv</c>): the key in the second
/// ink at the left, the value at the right with a small note under it. A hero row sets its value large, as the Report's
/// first figure of a card. Templated in Styles.Aero.Pages.xaml; to a screen reader it is one text, "key: value, note".
/// </summary>
internal sealed class KeyValue : Control
{
    public static readonly DependencyProperty KeyProperty = Register(nameof(Key));
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value));
    public static readonly DependencyProperty NoteProperty = Register(nameof(Note));
    public static readonly DependencyProperty IsHeroProperty = DependencyProperty.Register(nameof(IsHero), typeof(bool), typeof(KeyValue), new PropertyMetadata(false));

    static KeyValue()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(KeyValue), new FrameworkPropertyMetadata(typeof(KeyValue)));
        FocusableProperty.OverrideMetadata(typeof(KeyValue), new FrameworkPropertyMetadata(false));
    }

    public string Key { get => (string)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>Words under the value: "at 0.38 kg / kWh"; none takes no room.</summary>
    public string Note { get => (string)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }

    public bool IsHero { get => (bool)GetValue(IsHeroProperty); set => SetValue(IsHeroProperty, value); }

    /// <summary>What a screen reader reads: the key, the value and any note.</summary>
    internal string Spoken => string.IsNullOrEmpty(Note) ? $"{Key}: {Value}" : $"{Key}: {Value}, {Note}";

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private static DependencyProperty Register(string name) => DependencyProperty.Register(name, typeof(string), typeof(KeyValue), new PropertyMetadata(""));

    private sealed class Peer(KeyValue owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(KeyValue);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((KeyValue)Owner).Spoken : name;
        }
    }
}
