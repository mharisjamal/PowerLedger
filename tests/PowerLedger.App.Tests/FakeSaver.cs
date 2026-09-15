using System.IO;

namespace PowerLedger.App.Tests;

/// <summary>A Save dialog that answers with the suggested name in a folder of its own, or cancels.</summary>
internal sealed class FakeSaver : IFileSaver, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-saves-{Guid.NewGuid():N}");

    public bool Cancel { get; set; }

    public string? Suggested { get; private set; }

    /// <summary>Where the last answer pointed.</summary>
    public string Chosen => Path.Combine(_folder, Suggested ?? "nothing");

    public string? Ask(string name, string filter)
    {
        Suggested = name;
        Directory.CreateDirectory(_folder);
        return Cancel ? null : Chosen;
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
