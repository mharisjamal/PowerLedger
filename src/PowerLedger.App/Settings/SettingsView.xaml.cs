using System.Windows.Controls;
using System.Windows.Input;

namespace PowerLedger.App;

/// <summary>The Settings screen's layout (spec §9); everything it shows comes from <see cref="SettingsViewModel"/>.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Enter in a box of the service's settings gives what was typed to the form, as leaving the box does, so it is
    /// saved.</summary>
    private void TakeTyped(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
