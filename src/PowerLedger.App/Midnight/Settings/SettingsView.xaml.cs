using System.Windows.Controls;

namespace PowerLedger.App.Midnight;

/// <summary>Midnight's Settings page (Plan O task M2-4): each section a card, over <see cref="SettingsViewModel"/>. Enter in
/// a box of the service's settings saves through <see cref="SettingsEntry"/>, as it does in Classic.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
