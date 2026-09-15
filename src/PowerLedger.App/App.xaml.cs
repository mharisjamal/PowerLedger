using System.Windows;

namespace PowerLedger.App;

/// <summary>The tray App (spec §9). Task 13 composes it; until then it starts and stops.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Shutdown();
    }
}
