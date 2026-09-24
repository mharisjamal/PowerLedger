using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan O task M2-4: Enter in a settings box gives what was typed to the form, as leaving the box does, through
/// one attached behaviour both looks' Settings pages put on the panel that holds the service's boxes.</summary>
public class SettingsEntryTests
{
    [Fact]
    public void Enter_in_a_box_under_a_panel_that_saves_on_enter_gives_the_typed_value_to_the_source()
        => Sta.Run(() =>
        {
            var (form, box, window) = Screen(savesOnEnter: true);
            try
            {
                box.Text = "3";
                form.Fans.ShouldBe("");   // still waiting in the box, as it does until the box is left
                PressEnter(box);
                form.Fans.ShouldBe("3");
            }
            finally
            {
                window.Close();
            }
            return 0;
        });

    [Fact]
    public void Without_the_behaviour_enter_leaves_the_typed_value_in_the_box()
        => Sta.Run(() =>
        {
            var (form, box, window) = Screen(savesOnEnter: false);
            try
            {
                box.Text = "3";
                PressEnter(box);
                form.Fans.ShouldBe("");
            }
            finally
            {
                window.Close();
            }
            return 0;
        });

    [Fact]
    public void Turning_the_behaviour_off_again_stops_enter_saving()
        => Sta.Run(() =>
        {
            var (form, box, window) = Screen(savesOnEnter: true);
            try
            {
                SettingsEntry.SetSavesOnEnter((DependencyObject)window.Content, false);
                box.Text = "3";
                PressEnter(box);
                form.Fans.ShouldBe("");
            }
            finally
            {
                window.Close();
            }
            return 0;
        });

    /// <summary>A panel with the behaviour, or without it, holding one box bound the way the service's boxes are: the
    /// source takes the text when the box is left.</summary>
    private static (Form Form, TextBox Box, Window Window) Screen(bool savesOnEnter)
    {
        var form = new Form();
        var box = new TextBox();
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(Form.Fans)) { Source = form, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        var panel = new StackPanel { Children = { box } };
        SettingsEntry.SetSavesOnEnter(panel, savesOnEnter);
        var window = new Window
        {
            Content = panel, Width = 300, Height = 200, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        return (form, box, window);
    }

    private static void PressEnter(TextBox box)
        => box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });

    private sealed class Form : ObservableObject
    {
        private string _fans = "";

        public string Fans { get => _fans; set => SetProperty(ref _fans, value); }
    }
}
