using System.IO;
using System.Windows;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S intro: the Aero intro video's logic, over a fake player so no test needs Media Foundation. It is due once, on
/// the first Aero open after the move (while the new look's banner is still to show); an automatic start counts it as
/// seen, a manual one (Settings) never does; it starts muted and plays at once, or waits on its poster with Play under
/// reduced motion or reduced transparency; the end offers Replay; any failure ends it quietly.
/// </summary>
[Collection(AeroMotionScope.Name)]
public sealed class AeroIntroTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-intro-{Guid.NewGuid():N}");
    private readonly IntroMedia _media;

    public AeroIntroTests()
    {
        Directory.CreateDirectory(_folder);
        _media = new IntroMedia(Path.Combine(_folder, "aero-intro.mp4"), Path.Combine(_folder, "aero-intro.jpg"));
        File.WriteAllBytes(_media.Video, new byte[64]);
        File.WriteAllBytes(_media.Poster, TestImages.Png(16, 9));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static SettingsViewModel Settings(FakeUiSettings ui) => MidnightFixtures.SettingsScreen(ui);

    private AeroIntro Intro(SettingsViewModel settings, FakeIntroPlayer player) => new(_media, _ => player, settings);

    [Fact]
    public void It_is_due_on_the_first_open_after_the_move_and_an_automatic_start_counts_it_seen_so_the_next_open_does_not_autoplay()
    {
        using var motion = AeroMotion.Force(false);
        var ui = AeroFixtures.Moved();
        var settings = Settings(ui);
        var player = new FakeIntroPlayer();
        var intro = Intro(settings, player);

        intro.Due.ShouldBeTrue();
        intro.Start(manual: false);

        intro.State.ShouldBe(IntroState.Playing);
        player.Plays.ShouldBe(1, "it autoplays");
        player.Muted.ShouldBeTrue("it starts muted");
        intro.Muted.ShouldBeTrue();
        ui.Current.AeroIntroSeen.ShouldBeTrue();
        ui.Current.LookIntroduced.ShouldBeFalse("the banner shows after it");
        new AeroIntro(_media, _ => new FakeIntroPlayer(), Settings(ui)).Due.ShouldBeFalse("the second open does not autoplay");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void It_is_not_due_once_seen_or_once_the_banner_is_retired(bool seen, bool introduced)
    {
        var ui = AeroFixtures.Moved(introduced: introduced);
        ui.Current = ui.Current with { AeroIntroSeen = seen };
        Intro(Settings(ui), new FakeIntroPlayer()).Due.ShouldBeFalse();
    }

    [Fact]
    public void A_manual_start_plays_even_when_seen_and_counts_nothing()
    {
        using var motion = AeroMotion.Force(false);
        var ui = AeroFixtures.Moved(introduced: true);
        ui.Current = ui.Current with { AeroIntroSeen = true };
        var player = new FakeIntroPlayer();
        var intro = Intro(Settings(ui), player);

        intro.Start(manual: true);

        intro.State.ShouldBe(IntroState.Playing);
        player.Plays.ShouldBe(1);
        ui.Changes.ShouldBeEmpty("Settings' button saves nothing");
    }

    [Fact]
    public void Unseen_a_manual_start_still_leaves_the_automatic_one_due()
    {
        using var motion = AeroMotion.Force(false);
        var ui = AeroFixtures.Moved();
        var intro = Intro(Settings(ui), new FakeIntroPlayer());
        intro.Start(manual: true);
        intro.Close();
        ui.Current.AeroIntroSeen.ShouldBeFalse();
    }

    [Fact]
    public void The_speaker_toggles_the_sound_and_the_end_offers_replay_which_plays_from_the_start()
    {
        using var motion = AeroMotion.Force(false);
        var player = new FakeIntroPlayer();
        var intro = Intro(Settings(AeroFixtures.Moved()), player);
        intro.Start(manual: false);

        intro.ToggleMute();
        intro.Muted.ShouldBeFalse();
        player.Muted.ShouldBeFalse();
        intro.ToggleMute();
        player.Muted.ShouldBeTrue();

        player.End();
        intro.State.ShouldBe(IntroState.Ended);
        player.Plays.ShouldBe(1, "it plays once by itself");

        intro.Replay();
        intro.State.ShouldBe(IntroState.Playing);
        player.Rewinds.ShouldBe(1);
        player.Plays.ShouldBe(2);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Under_reduced_motion_or_reduced_transparency_it_waits_on_the_poster_for_play(bool reducedMotion, bool reduceTransparency)
    {
        using var motion = AeroMotion.Force(reducedMotion);
        var ui = AeroFixtures.Moved();
        ui.Current = ui.Current with { Glass = GlassSettings.Default with { ReduceTransparency = reduceTransparency } };
        var player = new FakeIntroPlayer();
        var intro = Intro(Settings(ui), player);

        intro.Start(manual: false);

        intro.State.ShouldBe(IntroState.Poster);
        player.Plays.ShouldBe(0, "nothing autoplays");
        ui.Current.AeroIntroSeen.ShouldBeTrue("shown is seen, played or not");
        intro.Play();
        intro.State.ShouldBe(IntroState.Playing);
        player.Plays.ShouldBe(1);
        player.Muted.ShouldBeTrue();
    }

    [Fact]
    public void Closing_stops_the_player_lets_it_go_and_says_so_once()
    {
        using var motion = AeroMotion.Force(false);
        var player = new FakeIntroPlayer();
        var intro = Intro(Settings(AeroFixtures.Moved()), player);
        var finished = 0;
        intro.Finished += () => finished++;
        intro.Start(manual: false);

        intro.Close();
        intro.Close();

        intro.State.ShouldBe(IntroState.Closed);
        intro.Failed.ShouldBeFalse();
        player.Stopped.ShouldBeTrue();
        player.Disposed.ShouldBeTrue();
        finished.ShouldBe(1);
        player.End();
        intro.State.ShouldBe(IntroState.Closed, "a closed intro hears its player no more");
    }

    /// <summary>The build carries the real video and poster beside the exe, where <see cref="IntroMedia.Installed"/>
    /// looks and the installer's recursive copy of the publish picks them up: an MP4 under 4 MB, a 1280 x 720 poster.</summary>
    [Fact]
    public void The_build_carries_the_video_and_its_poster_where_the_app_looks()
    {
        var video = new FileInfo(IntroMedia.Installed.Video);
        video.Exists.ShouldBeTrue(video.FullName);
        video.Length.ShouldBeInRange(500_000, 4L * 1024 * 1024);
        using (var stream = video.OpenRead())
        {
            var head = new byte[12];
            stream.ReadExactly(head);
            System.Text.Encoding.ASCII.GetString(head, 4, 4).ShouldBe("ftyp", "an MP4");
        }
        var poster = Sta.Run(() =>
        {
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(IntroMedia.Installed.Poster),
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            return (frame.PixelWidth, frame.PixelHeight);
        });
        poster.ShouldBe((1280, 720));
    }

    public static TheoryData<string> Failures => ["missing", "open throws", "play throws", "media failed"];

    /// <summary>A missing file, a player that can't be made or can't play (Windows N without Media Foundation), or the
    /// player's own MediaFailed: the intro ends quietly, as failed, and the window goes on to the banner.</summary>
    [Theory]
    [MemberData(nameof(Failures))]
    public void Any_failure_ends_it_quietly_as_failed(string failure)
    {
        using var motion = AeroMotion.Force(false);
        var ui = AeroFixtures.Moved();
        var player = new FakeIntroPlayer { ThrowOnPlay = failure == "play throws" };
        if (failure == "missing") File.Delete(_media.Video);
        var intro = new AeroIntro(_media, _ => failure == "open throws" ? throw new InvalidOperationException("Windows Media Player version 10 or later is required.") : player, Settings(ui));
        var finished = 0;
        intro.Finished += () => finished++;

        intro.Start(manual: false);
        if (failure == "media failed") player.Fail();

        intro.State.ShouldBe(IntroState.Closed);
        intro.Failed.ShouldBeTrue();
        finished.ShouldBe(1);
        ui.Current.AeroIntroSeen.ShouldBeTrue("a PC that can't play it isn't asked again");
        if (failure != "missing" && failure != "open throws") player.Disposed.ShouldBeTrue();
    }
}

/// <summary>An intro player that records what it was asked, and ends or fails when the test says.</summary>
internal sealed class FakeIntroPlayer : IIntroPlayer
{
    public event Action? Ended;

    public event Action? Failed;

    /// <summary>What the window shows for the video; none unless a window test gives one (a UIElement needs the UI thread).</summary>
    public UIElement? View { get; init; }

    public bool Muted { get; set; }

    public bool ThrowOnPlay { get; init; }

    public int Plays { get; private set; }

    public int Rewinds { get; private set; }

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    public void Play()
    {
        if (ThrowOnPlay) throw new System.Runtime.InteropServices.COMException("No Media Foundation");
        Plays++;
    }

    public void Rewind() => Rewinds++;

    public void Stop() => Stopped = true;

    public void Dispose() => Disposed = true;

    public void End() => Ended?.Invoke();

    public void Fail() => Failed?.Invoke();
}
