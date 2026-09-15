using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class UpdateDownloaderTests : IDisposable
{
    private static readonly byte[] Installer = [.. Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7))];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-updates-{Guid.NewGuid():N}");
    private readonly FakeHttp _http = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>A release whose installer is <paramref name="file"/>.</summary>
    internal static Release ReleaseOf(byte[] file, string version = "0.2.0") => new(
        Version.Parse(version), GitHubReleaseFeed.PageOf(Version.Parse(version)),
        new Uri($"{GitHubReleaseFeed.Downloads}v{version}/PowerLedger-{version}-setup.exe"), $"PowerLedger-{version}-setup.exe",
        file.Length, SHA256.HashData(file));

    private UpdateDownloader Downloader(TimeSpan? stall = null) => new(_http.Client(), _folder, stall);

    private string[] Files()
        => Directory.Exists(_folder) ? [.. Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)] : [];

    [Fact]
    public async Task The_installer_is_downloaded_checked_and_kept_under_its_own_name()
    {
        _http.Reply(HttpStatusCode.OK, Installer);
        var fractions = new Fractions();

        var path = await Downloader().DownloadAsync(ReleaseOf(Installer), fractions, CancellationToken.None);

        path.ShouldBe(Path.Combine(_folder, "PowerLedger-0.2.0-setup.exe"));
        File.ReadAllBytes(path).ShouldBe(Installer);
        Files().ShouldBe(new[] { "PowerLedger-0.2.0-setup.exe" });
        fractions.Seen.ShouldBeInOrder();
        fractions.Seen[^1].ShouldBe(1.0);
        _http.Requests.Single().RequestUri.ShouldBe(new Uri($"{GitHubReleaseFeed.Downloads}v0.2.0/PowerLedger-0.2.0-setup.exe"));
    }

    [Fact]
    public async Task A_checked_copy_already_there_is_used_without_downloading_again()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "PowerLedger-0.2.0-setup.exe"), Installer);

        var path = await Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None);

        path.ShouldBe(Path.Combine(_folder, "PowerLedger-0.2.0-setup.exe"));
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_copy_that_does_not_match_is_downloaded_again()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "PowerLedger-0.2.0-setup.exe"), Installer[..1000]);
        _http.Reply(HttpStatusCode.OK, Installer);

        var path = await Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None);

        File.ReadAllBytes(path).ShouldBe(Installer);
        _http.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_checksum_is_deleted()
    {
        var tampered = (byte[])Installer.Clone();
        tampered[1234] ^= 0xFF;
        _http.Reply(HttpStatusCode.OK, tampered);

        (await Should.ThrowAsync<UpdateException>(Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("PowerLedger-0.2.0-setup.exe didn't match GitHub's checksum, so it was deleted.");
        Files().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_of_the_wrong_size_is_refused_and_deleted()
    {
        byte[] longer = [.. Installer, 1, 2, 3];
        _http.Reply(HttpStatusCode.OK, longer);   // the length is known up front
        (await Should.ThrowAsync<UpdateException>(Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("PowerLedger-0.2.0-setup.exe isn't the size GitHub lists, so it was deleted.");

        _http.Reply(new Unsized(longer));          // found out only as it arrives
        (await Should.ThrowAsync<UpdateException>(Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("PowerLedger-0.2.0-setup.exe isn't the size GitHub lists, so it was deleted.");
        Files().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_cut_short_is_deleted()
    {
        _http.Reply(new Unsized(Installer[..5000]));
        (await Should.ThrowAsync<UpdateException>(Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("PowerLedger-0.2.0-setup.exe arrived incomplete; PowerLedger tries again later.");
        Files().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_that_stalls_is_abandoned()
    {
        _http.Reply(new StreamContent(new Stalling()));
        (await Should.ThrowAsync<UpdateException>(Downloader(TimeSpan.FromMilliseconds(100)).DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("The download stalled; PowerLedger tries again later.");
        Files().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_error_answer_downloads_nothing()
    {
        _http.Reply(HttpStatusCode.NotFound, []);
        (await Should.ThrowAsync<UpdateException>(Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("GitHub answered 404 for PowerLedger-0.2.0-setup.exe.");
        Files().ShouldBeEmpty();
    }

    [Fact]
    public void Starting_a_version_clears_what_it_no_longer_needs()
    {
        Directory.CreateDirectory(_folder);
        foreach (var name in new[]
        {
            "PowerLedger-0.1.0-setup.exe", "PowerLedger-0.1.0-setup.log", "PowerLedger-0.2.0-setup.exe", "PowerLedger-0.2.0-setup.log",
            "PowerLedger-0.3.0-setup.exe", "PowerLedger-0.3.0-setup.exe.partial", "notes.txt",
        })
        {
            File.WriteAllText(Path.Combine(_folder, name), name);
        }

        Downloader().Clean(new Version(0, 2, 0));

        Files().ShouldBe(new[] { "PowerLedger-0.2.0-setup.log", "PowerLedger-0.3.0-setup.exe", "notes.txt" });
    }

    [Fact]
    public void Cleaning_a_folder_that_is_not_there_does_nothing() => Downloader().Clean(new Version(0, 2, 0));
}
