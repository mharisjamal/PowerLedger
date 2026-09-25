using Microsoft.Extensions.Configuration;

namespace PowerLedger.Service;

/// <summary>Where the service keeps its files (spec §7): one folder holding the database, its backup and the logs.</summary>
internal sealed record ServicePaths(string DataDirectory)
{
    /// <summary>Configuration key, "--data" on the command line, that moves everything elsewhere for development.</summary>
    public const string DataDirectoryKey = "data";

    public string Database => Path.Combine(DataDirectory, "power.db");

    public string Logs => Path.Combine(DataDirectory, "logs");

    /// <summary>Copies of what data sharing sent, the newest 30, and the preview of the next upload, for the App to show.</summary>
    public string Sent => Path.Combine(DataDirectory, "Sent");

    /// <summary>The service's crash files, until the sharing worker records or deletes them.</summary>
    public string Crashes => Path.Combine(DataDirectory, "Crashes");

    /// <summary>Downloaded installers, setup's logs and the relaunch note, for SYSTEM and Administrators alone (Plan Q §4).</summary>
    public string Updates => Path.Combine(DataDirectory, "Updates");

    /// <summary>C:\ProgramData\PowerLedger, unless configuration names another folder.</summary>
    public static ServicePaths From(IConfiguration configuration)
    {
        var configured = configuration[DataDirectoryKey];
        return new ServicePaths(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerLedger")
            : Path.GetFullPath(configured));
    }
}
