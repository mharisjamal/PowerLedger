using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PowerLedger.App;

/// <summary>
/// Enter in a box of the service's settings gives what was typed to the form, as leaving the box does, so a setting that
/// saves itself is saved. An attached behaviour, so both looks' Settings pages share it: set
/// <c>SettingsEntry.SavesOnEnter="True"</c> on the panel that holds the boxes.
/// </summary>
internal static class SettingsEntry
{
    public static readonly DependencyProperty SavesOnEnterProperty = DependencyProperty.RegisterAttached(
        "SavesOnEnter", typeof(bool), typeof(SettingsEntry), new PropertyMetadata(false, OnSavesOnEnterChanged));

    public static bool GetSavesOnEnter(DependencyObject element) => (bool)element.GetValue(SavesOnEnterProperty);

    public static void SetSavesOnEnter(DependencyObject element, bool value) => element.SetValue(SavesOnEnterProperty, value);

    private static void OnSavesOnEnterChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement panel) return;
        if (e.NewValue is true) panel.KeyDown += TakeTyped;
        else panel.KeyDown -= TakeTyped;
    }

    private static void TakeTyped(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
