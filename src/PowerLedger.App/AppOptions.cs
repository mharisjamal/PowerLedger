using System.IO;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <param name="PipeName">The service's pipe; --pipe names a development service's.</param>
/// <param name="DataFolder">Where the service keeps power.db; --data names a development run's folder.</param>
/// <param name="StartInTray">--tray: start with only the tray icon, as the Run entry does.</param>
internal sealed record AppOptions(string PipeName, string DataFolder, bool StartInTray)
{
    public static string DefaultDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerLedger");

    public string DatabasePath => Path.Combine(DataFolder, "power.db");

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var pipe = PipeProtocol.PipeName;
        var data = DefaultDataFolder;
        var tray = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Count:
                    pipe = args[++i];
                    break;
                case "--data" when i + 1 < args.Count:
                    data = args[++i];
                    break;
                case "--tray":
                    tray = true;
                    break;
            }
        }
        return new AppOptions(pipe, data, tray);
    }
}
