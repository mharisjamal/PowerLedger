using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>The Settings screen's layout (spec §9); everything it shows comes from <see cref="SettingsViewModel"/>. Enter in
/// a box of the service's settings saves through <see cref="SettingsEntry"/>.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
