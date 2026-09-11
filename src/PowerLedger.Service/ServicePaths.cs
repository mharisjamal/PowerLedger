using Microsoft.Extensions.Configuration;

namespace PowerLedger.Service;

/// <summary>Where the service keeps its files (spec §7): one folder holding the database, its backup and the logs.</summary>
internal sealed record ServicePaths(string DataDirectory)
{
    /// <summary>Configuration key, "--data" on the command line, that moves everything elsewhere for development.</summary>
    public const string DataDirectoryKey = "data";

    public string Database => Path.Combine(DataDirectory, "power.db");

    public string Logs => Path.Combine(DataDirectory, "logs");

    /// <summary>C:\ProgramData\PowerLedger, unless configuration names another folder.</summary>
    public static ServicePaths From(IConfiguration configuration)
    {
        var configured = configuration[DataDirectoryKey];
        return new ServicePaths(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerLedger")
            : Path.GetFullPath(configured));
    }
}
