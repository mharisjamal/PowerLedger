using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ReleaseFeedTests
{
    private const string Downloads = GitHubReleaseFeed.Downloads;
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>GitHub's answer for a release, trimmed to what the App reads plus fields it ignores.</summary>
    internal static byte[] Answer(
        string tag = "v0.2.0", string? asset = null, long size = 100_297_944, string? url = null,
        string digest = $"\"sha256:{Sha}\"", bool draft = false, bool prerelease = false)
    {
        asset ??= $"PowerLedger-{tag.TrimStart('v')}-setup.exe";
        url ??= $"{Downloads}{tag}/{asset}";
        return Encoding.UTF8.GetBytes($$"""
            {
              "tag_name": "{{tag}}",
              "name": "PowerLedger {{tag}}",
              "html_url": "https://github.com/mharisjamal/PowerLedger/releases/tag/{{tag}}",
              "draft": {{(draft ? "true" : "false")}},
              "prerelease": {{(prerelease ? "true" : "false")}},
              "body": "Notes",
              "assets": [
                { "name": "notes.txt", "size": 12, "browser_download_url": "{{Downloads}}{{tag}}/notes.txt", "digest": null },
                { "name": "{{asset}}", "size": {{size}}, "browser_download_url": "{{url}}", "digest": {{digest}} }
              ]
            }
            """);
    }

    /// <summary>A release carrying the universal installer and one per architecture, as releases from 0.3.0 do.</summary>
    internal static byte[] AnswerWithArchitectures(string tag = "v0.3.0")
    {
        var version = tag.TrimStart('v');
        return Encoding.UTF8.GetBytes($$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/mharisjamal/PowerLedger/releases/tag/{{tag}}",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "PowerLedger-{{version}}-setup.exe", "size": 100000000,
                  "browser_download_url": "{{Downloads}}{{tag}}/PowerLedger-{{version}}-setup.exe", "digest": "sha256:{{Sha}}" },
                { "name": "PowerLedger-{{version}}-setup-x64.exe", "size": 58000000,
                  "browser_download_url": "{{Downloads}}{{tag}}/PowerLedger-{{version}}-setup-x64.exe", "digest": "sha256:{{Sha}}" },
                { "name": "PowerLedger-{{version}}-setup-arm64.exe", "size": 60000000,
                  "browser_download_url": "{{Downloads}}{{tag}}/PowerLedger-{{version}}-setup-arm64.exe", "digest": "sha256:{{Sha}}" },
                { "name": "PowerLedger-{{version}}-setup-x86.exe", "size": 52000000,
                  "browser_download_url": "{{Downloads}}{{tag}}/PowerLedger-{{version}}-setup-x86.exe", "digest": "sha256:{{Sha}}" }
              ]
            }
            """);
    }

    [Fact]
    public void A_release_with_its_installer_and_digest_is_trusted()
    {
        var release = GitHubReleaseFeed.Parse(Answer(), Downloads).ShouldNotBeNull();
        release.Version.ShouldBe(new Version(0, 2, 0));
        release.Name.ShouldBe("0.2.0");
        release.FileName.ShouldBe("PowerLedger-0.2.0-setup.exe");
        release.Size.ShouldBe(100_297_944);
        release.Installer.ShouldBe(new Uri($"{Downloads}v0.2.0/PowerLedger-0.2.0-setup.exe"));
        release.Page.ShouldBe(new Uri("https://github.com/mharisjamal/PowerLedger/releases/tag/v0.2.0"));
        Convert.ToHexStringLower(release.Sha256).ShouldBe(Sha);
    }

    [Fact]
    public void Drafts_and_pre_releases_are_not_offered()
    {
        GitHubReleaseFeed.Parse(Answer(draft: true), Downloads).ShouldBeNull();
        GitHubReleaseFeed.Parse(Answer(prerelease: true), Downloads).ShouldBeNull();
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("v0.2")]
    [InlineData("v0.2.0-beta")]
    [InlineData("v0.2.0.1")]
    [InlineData("v٠.٢.٠")]
    public void A_tag_that_is_not_a_version_is_refused(string tag)
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(tag: tag, asset: "PowerLedger-0.2.0-setup.exe"), Downloads))
            .Message.ShouldContain("isn't a version");

    [Fact]
    public void A_release_without_this_versions_installer_is_refused()
    {
        Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(asset: "PowerLedger-0.1.0-setup.exe"), Downloads))
            .Message.ShouldBe("Release 0.2.0 has no installer for this PC.");
        Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(asset: "powerledger-0.2.0-setup.exe"), Downloads));
    }

    [Theory]
    [InlineData(Architecture.X64, "PowerLedger-0.3.0-setup-x64.exe", 58000000L)]
    [InlineData(Architecture.Arm64, "PowerLedger-0.3.0-setup-arm64.exe", 60000000L)]
    [InlineData(Architecture.X86, "PowerLedger-0.3.0-setup-x86.exe", 52000000L)]
    public void The_installer_for_this_PC_comes_first(Architecture architecture, string name, long size)
    {
        var release = GitHubReleaseFeed.Parse(AnswerWithArchitectures(), Downloads, architecture).ShouldNotBeNull();
        release.FileName.ShouldBe(name);
        release.Size.ShouldBe(size);
        release.Installer.ShouldBe(new Uri($"{Downloads}v0.3.0/{name}"));
    }

    [Fact]
    public void A_release_with_only_the_universal_installer_still_updates_every_PC()
    {
        foreach (var architecture in new[] { Architecture.X64, Architecture.Arm64, Architecture.X86 })
        {
            GitHubReleaseFeed.Parse(Answer(), Downloads, architecture).ShouldNotBeNull().FileName.ShouldBe("PowerLedger-0.2.0-setup.exe");
        }
    }

    [Fact]
    public void A_release_with_no_installer_for_this_PC_is_refused()
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(asset: "PowerLedger-0.2.0-setup-x86.exe"), Downloads, Architecture.X64))
            .Message.ShouldBe("Release 0.2.0 has no installer for this PC.");

    [Theory]
    [InlineData("https://github.com/someone/PowerLedger/releases/download/v0.2.0/PowerLedger-0.2.0-setup.exe")]
    [InlineData("https://example.org/mharisjamal/PowerLedger/releases/download/v0.2.0/PowerLedger-0.2.0-setup.exe")]
    [InlineData("http://github.com/mharisjamal/PowerLedger/releases/download/v0.2.0/PowerLedger-0.2.0-setup.exe")]
    [InlineData("https://github.com/mharisjamal/PowerLedger/releases/download/../../../someone/x/PowerLedger-0.2.0-setup.exe")]
    [InlineData("not a url")]
    public void An_installer_kept_anywhere_else_is_refused(string url)
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(url: url), Downloads))
            .Message.ShouldContain("isn't where PowerLedger's releases are kept");

    [Theory]
    [InlineData("null")]
    [InlineData("\"md5:0123456789abcdef0123456789abcdef\"")]
    [InlineData("\"sha256:0123\"")]
    [InlineData("\"sha256:zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"")]
    public void An_installer_without_a_sha256_is_refused(string digest)
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(digest: digest), Downloads))
            .Message.ShouldContain("no SHA-256");

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(GitHubReleaseFeed.MaxInstaller + 1)]
    public void An_impossible_size_is_refused(long size)
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(size: size), Downloads))
            .Message.ShouldContain("impossible size");

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void An_answer_that_is_not_a_release_is_refused(string json)
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Encoding.UTF8.GetBytes(json), Downloads));

    [Fact]
    public async Task The_latest_release_is_asked_of_GitHub_as_its_API_wants()
    {
        var http = new FakeHttp();
        http.Reply(HttpStatusCode.OK, Answer());
        using var client = http.Client();

        var release = await GitHubReleaseFeed.For(client, null).LatestAsync(CancellationToken.None);

        release.ShouldNotBeNull().Version.ShouldBe(new Version(0, 2, 0));
        var request = http.Requests.Single();
        request.RequestUri.ShouldBe(new Uri("https://api.github.com/repos/mharisjamal/PowerLedger/releases/latest"));
        request.Headers.Accept.ToString().ShouldBe("application/vnd.github+json");
        request.Headers.GetValues("X-GitHub-Api-Version").Single().ShouldBe("2022-11-28");
    }

    [Fact]
    public async Task Nothing_published_is_no_release()
    {
        using var client = new FakeHttp().Client();   // answers 404
        (await GitHubReleaseFeed.For(client, null).LatestAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GitHub is limiting requests from this network; PowerLedger tries again later.")]
    [InlineData(HttpStatusCode.TooManyRequests, "GitHub is limiting requests from this network; PowerLedger tries again later.")]
    [InlineData(HttpStatusCode.InternalServerError, "GitHub answered 500.")]
    public async Task An_error_from_GitHub_is_said_plainly(HttpStatusCode status, string message)
    {
        var http = new FakeHttp();
        http.Reply(status, []);
        using var client = http.Client();
        (await Should.ThrowAsync<UpdateException>(GitHubReleaseFeed.For(client, null).LatestAsync(CancellationToken.None)))
            .Message.ShouldBe(message);
    }

    [Fact]
    public async Task No_network_and_no_answer_in_time_are_said_plainly()
    {
        var http = new FakeHttp { Answer = (_, _) => throw new HttpRequestException("No such host is known.") };
        using var client = http.Client();
        (await Should.ThrowAsync<UpdateException>(GitHubReleaseFeed.For(client, null).LatestAsync(CancellationToken.None)))
            .Message.ShouldBe("Couldn't reach GitHub.");

        http.Answer = async (_, cancel) =>
        {
            await Task.Delay(Timeout.Infinite, cancel);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var slow = new GitHubReleaseFeed(client, GitHubReleaseFeed.Latest, Downloads, TimeSpan.FromMilliseconds(50));
        (await Should.ThrowAsync<UpdateException>(slow.LatestAsync(CancellationToken.None))).Message.ShouldBe("GitHub didn't answer in time.");
    }

    [Fact]
    public async Task An_oversized_answer_is_refused()
    {
        var http = new FakeHttp();
        http.Reply(HttpStatusCode.OK, new byte[GitHubReleaseFeed.MaxAnswer + 1]);
        using var client = http.Client();
        (await Should.ThrowAsync<UpdateException>(GitHubReleaseFeed.For(client, null).LatestAsync(CancellationToken.None)))
            .Message.ShouldContain("too large");
    }

    [Fact]
    public async Task A_test_feed_answers_at_its_own_address_and_serves_installers_there()
    {
        var http = new FakeHttp();
        http.Reply(HttpStatusCode.OK, Answer(url: "http://127.0.0.1:8765/download/v0.2.0/PowerLedger-0.2.0-setup.exe"));
        using var client = http.Client();

        var release = await GitHubReleaseFeed.For(client, new Uri("http://127.0.0.1:8765/")).LatestAsync(CancellationToken.None);

        http.Requests.Single().RequestUri.ShouldBe(new Uri("http://127.0.0.1:8765/releases/latest"));
        release.ShouldNotBeNull().Installer.ShouldBe(new Uri("http://127.0.0.1:8765/download/v0.2.0/PowerLedger-0.2.0-setup.exe"));
    }

    [Fact]
    public void Requests_name_PowerLedger_and_its_version()
    {
        using var http = UpdateHttp.Create("0.2.0");
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldBe("PowerLedger/0.2.0");
    }
}
