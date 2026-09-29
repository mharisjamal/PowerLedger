using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// A password box that hands on what the user typed when they leave it, as the other boxes in Settings do, and only then:
/// a box left without typing sends nothing, which keeps the password the service holds. The typed text goes one way, into
/// the binding (bind <see cref="TypedProperty"/> with Mode=OneWayToSource); nothing the form holds is ever shown in the box.
/// Only a password box bound this way is listened to.
/// </summary>
public static class PasswordEntry
{
    public static readonly DependencyProperty TypedProperty = DependencyProperty.RegisterAttached(
        "Typed", typeof(string), typeof(PasswordEntry), new FrameworkPropertyMetadata(null));

    private static readonly DependencyProperty EditedProperty = DependencyProperty.RegisterAttached(
        "Edited", typeof(bool), typeof(PasswordEntry), new PropertyMetadata(false));

    static PasswordEntry()
    {
        EventManager.RegisterClassHandler(typeof(PasswordBox), PasswordBox.PasswordChangedEvent, new RoutedEventHandler(OnPasswordChanged));
        EventManager.RegisterClassHandler(typeof(PasswordBox), UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostFocus));
    }

    public static string? GetTyped(DependencyObject box) => (string?)box.GetValue(TypedProperty);

    public static void SetTyped(DependencyObject box, string? value) => box.SetValue(TypedProperty, value);

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && BindingOperations.IsDataBound(box, TypedProperty)) box.SetValue(EditedProperty, true);
    }

    private static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not PasswordBox box || !(bool)box.GetValue(EditedProperty)) return;
        if (e.NewFocus is DependencyObject next && (ReferenceEquals(next, box) || (next is Visual visual && box.IsAncestorOf(visual)))) return;
        box.SetValue(EditedProperty, false);
        // The same text typed twice is still said twice: the property is cleared first, and null is taken as nothing typed.
        box.SetCurrentValue(TypedProperty, null);
        box.SetCurrentValue(TypedProperty, box.Password);
    }
}
