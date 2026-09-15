namespace PowerLedger.App.Tests;

/// <summary>A feed that answers what the test sets, counting the questions.</summary>
internal sealed class FakeFeed : IReleaseFeed
{
    public Release? Latest { get; set; }

    public Exception? Failure { get; set; }

    public int Asked { get; private set; }

    public Task<Release?> LatestAsync(CancellationToken cancel)
    {
        Asked++;
        return Failure is { } failure ? Task.FromException<Release?>(failure) : Task.FromResult(Latest);
    }
}

/// <summary>A download that is done at once, or waits for <see cref="Gate"/>, or fails as the test says.</summary>
internal sealed class FakeDownloader : IUpdateDownloader
{
    public List<Release> Downloads { get; } = [];

    public List<Version> Cleaned { get; } = [];

    public Exception? Failure { get; set; }

    public Task? Gate { get; set; }

    public async Task<string> DownloadAsync(Release release, IProgress<double>? progress, CancellationToken cancel)
    {
        if (Failure is { } failure) throw failure;
        Downloads.Add(release);
        progress?.Report(0.5);
        if (Gate is { } gate) await gate;
        progress?.Report(1);
        return $@"C:\Updates\{release.FileName}";
    }

    public void Clean(Version running) => Cleaned.Add(running);
}

/// <summary>Setup as the test scripts it: an exit code, a wait the test ends, or a refusal.</summary>
internal sealed class FakeSetup : ISetupRunner
{
    public List<(string Installer, string Log)> Started { get; } = [];

    public Exception? Failure { get; set; }

    /// <summary>When setup ends; at once with exit code 2 unless the test says otherwise.</summary>
    public Task<int> Exit { get; set; } = Task.FromResult(2);

    public Task<int> RunAsync(string installer, long size, byte[] sha256, string log, CancellationToken cancel)
    {
        if (Failure is { } failure) return Task.FromException<int>(failure);
        Started.Add((installer, log));
        return Exit;
    }
}

/// <summary>Records what a progress report hears, as it hears it.</summary>
internal sealed class Fractions : IProgress<double>
{
    public List<double> Seen { get; } = [];

    public void Report(double value) => Seen.Add(value);
}
