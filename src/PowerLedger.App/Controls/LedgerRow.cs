using System.Windows;
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>One line of a ledger (spec §9: dotted leaders between labels and values), templated in Styles.xaml.</summary>
internal sealed class LedgerRow : Control
{
    public static readonly DependencyProperty KeyProperty = Text(nameof(Key));
    public static readonly DependencyProperty ValueProperty = Text(nameof(Value));
    public static readonly DependencyProperty NoteProperty = Text(nameof(Note));
    public static readonly DependencyProperty IsHeroProperty = DependencyProperty.Register(
        nameof(IsHero), typeof(bool), typeof(LedgerRow), new PropertyMetadata(false));

    public string Key { get => (string)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public string Note { get => (string)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }

    /// <summary>The first line of a ledger, set large.</summary>
    public bool IsHero { get => (bool)GetValue(IsHeroProperty); set => SetValue(IsHeroProperty, value); }

    private static DependencyProperty Text(string name)
        => DependencyProperty.Register(name, typeof(string), typeof(LedgerRow), new PropertyMetadata(string.Empty));
}
