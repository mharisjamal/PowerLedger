using System.IO;

namespace PowerLedger.App.Tests;

/// <summary>A Save dialog that answers with the suggested name in a folder of its own, or cancels.</summary>
internal sealed class FakeSaver : IFileSaver, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-saves-{Guid.NewGuid():N}");

    public bool Cancel { get; set; }

    /// <summary>When set, points into a folder that is never created, so a write there fails with a real
    /// <see cref="IOException"/> — for testing a guard against one, rather than mocking the failure.</summary>
    public bool MissingFolder { get; set; }

    public string? Suggested { get; private set; }

    private string TargetFolder => MissingFolder ? Path.Combine(_folder, "does-not-exist") : _folder;

    /// <summary>Where the last answer pointed.</summary>
    public string Chosen => Path.Combine(TargetFolder, Suggested ?? "nothing");

    public string? Ask(string name, string filter)
    {
        Suggested = name;
        if (!MissingFolder) Directory.CreateDirectory(_folder);
        return Cancel ? null : Chosen;
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
