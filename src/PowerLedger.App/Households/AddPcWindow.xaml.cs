using System.Windows;

namespace PowerLedger.App;

/// <summary>Add a PC (households design §3, §4): browses this network, starts a code pairing, or joins one with a code,
/// live only while the window is open.</summary>
public partial class AddPcWindow : Window
{
    internal AddPcWindow(AddPcViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.Start();
        Closed += (_, _) => model.Dispose();
    }
}
