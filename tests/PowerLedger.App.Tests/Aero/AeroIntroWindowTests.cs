using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S intro in the window: on the first Aero open after the move, the intro video plays in a glass dialog over the
/// frosted scrim before the new look's banner; Esc, Skip or the end and Close take it away and the banner shows. The
/// next open goes straight to the banner. A player that fails closes it quietly. Under reduced motion or reduced
/// transparency it waits on its poster with Play. Settings' Watch the Aero intro opens it again at any time. It is drawn
/// in both themes, on its poster.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public sealed class AeroIntroWindowTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-intro-window-{Guid.NewGuid():N}");
    private readonly IntroMedia _media;

    public AeroIntroWindowTests()
    {
        Directory.CreateDirectory(_folder);
        _media = new IntroMedia(Path.Combine(_folder, "aero-intro.mp4"), Path.Combine(_folder, "aero-intro.jpg"));
        File.WriteAllBytes(_media.Video, new byte[64]);
        File.WriteAllBytes(_media.Poster, Poster());
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>The shipped poster when the build carries it, else a 1280 x 720 gradient of the palette's violet and blue.</summary>
    private static byte[] Poster()
    {
        var shipped = IntroMedia.Installed.Poster;
        if (File.Exists(shipped)) return File.ReadAllBytes(shipped);
        return Sta.Run(() =>
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x74, 0x66, 0xD8), Color.FromRgb(0x2B, 0x6C, 0xD4), 30), null, new Rect(0, 0, 1280, 720));
            var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        });
    }

    /// <summary>Runs <paramref name="test"/> on a shown Aero window over <paramref name="shell"/> with an intro playing
    /// through <paramref name="player"/>, motion reduced or not, and closes it after.</summary>
    private void OnWindow(ShellViewModel shell, Func<FakeIntroPlayer> player, bool reduced, Action<AeroWindow, List<FakeIntroPlayer>> test, Theme theme = Theme.Dark)
        => UiHarness.OnUi(() =>
        {
            var players = new List<FakeIntroPlayer>();
            var intro = new AeroIntro(_media, _ =>
            {
                var made = player();
                players.Add(made);
                return made;
            }, shell.Settings);
            var window = AeroHost.Window(shell, theme, intro: intro);
            using var motion = AeroMotion.Force(reduced);   // after the window: its glass sets the override from Settings
            window.Width = 1440;
            window.Height = 900;
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(reduced ? 400 : 1200));
                test(window, players);
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    private static FakeIntroPlayer Player(bool throwOnPlay = false) => new() { View = new Border(), ThrowOnPlay = throwOnPlay };

    private static void Press(ButtonBase button)
    {
        if (button.Command is { } command) command.Execute(button.CommandParameter);
        else button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    private static void Key(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window)!;
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    /// <summary>Pumps until the dialog and the scrim have faded all the way in: an animation's clock moves only as frames
    /// render, which an off-screen window under load draws late, so a fixed pump can catch it part way (0.87 seen).</summary>
    private static void Settle(AeroWindow window)
    {
        var scrim = (FrameworkElement)window.FindName("Scrim");
        for (var i = 0; i < 100 && (window.Modal!.Opacity < 1 || scrim.Opacity < 1); i++) UiHarness.Pump(TimeSpan.FromMilliseconds(50));
        window.Modal!.Opacity.ShouldBe(1, "faded in");
    }

    private static FrameworkElement Banner(AeroWindow window) => UiHarness.Find<GlassPanel>(window, p => p.Name == "LookIntro")!;

    private static Button IntroButton(AeroWindow window, string name) => UiHarness.Find<Button>(window.Modal!, b => b.Name == name)!;

    [Fact]
    public void The_first_open_after_the_move_plays_the_intro_muted_before_the_banner_and_esc_takes_it_to_the_banner()
    {
        using var saver = new FakeSaver();
        var ui = AeroFixtures.Moved();
        var shell = AeroFixtures.Shell(saver, ui);
        OnWindow(shell, () => Player(), reduced: false, (window, players) =>
        {
            window.Modal.ShouldNotBeNull();
            AutomationProperties.GetName(window.Modal).ShouldBe("The Aero intro");
            window.IntroPlaying.ShouldBeTrue();
            players.Single().Plays.ShouldBe(1);
            players.Single().Muted.ShouldBeTrue();
            Banner(window).Visibility.ShouldBe(Visibility.Collapsed, "the banner waits for the video");
            ((FrameworkElement)window.FindName("Scrim")).Visibility.ShouldBe(Visibility.Visible);
            IntroButton(window, "IntroPlay").IsVisible.ShouldBeFalse();
            IntroButton(window, "IntroReplay").IsVisible.ShouldBeFalse();
            var skip = IntroButton(window, "IntroClose");
            skip.Content.ShouldBe("Skip");
            AutomationProperties.GetName(skip).ShouldBe("Skip the intro");
            var mute = IntroButton(window, "IntroMute");
            AutomationProperties.GetName(mute).ShouldBe("Turn the sound on");
            Press(mute);
            players.Single().Muted.ShouldBeFalse();
            AutomationProperties.GetName(mute).ShouldBe("Turn the sound off");

            Key(window, System.Windows.Input.Key.Escape);

            window.Modal.ShouldBeNull();
            window.IntroPlaying.ShouldBeFalse();
            players.Single().Stopped.ShouldBeTrue();
            players.Single().Disposed.ShouldBeTrue();
            Banner(window).Visibility.ShouldBe(Visibility.Visible);
            ui.Current.AeroIntroSeen.ShouldBeTrue();
        });

        OnWindow(shell, () => Player(), reduced: false, (window, players) =>
        {
            window.Modal.ShouldBeNull("the second open does not autoplay");
            players.ShouldBeEmpty();
            Banner(window).Visibility.ShouldBe(Visibility.Visible, "the banner still shows until retired");
        });
    }

    [Fact]
    public void At_the_end_it_offers_replay_and_close_and_close_goes_to_the_banner()
    {
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver, AeroFixtures.Moved());
        OnWindow(shell, () => Player(), reduced: false, (window, players) =>
        {
            players.Single().End();
            UiHarness.Pump(TimeSpan.FromMilliseconds(50));
            var replay = IntroButton(window, "IntroReplay");
            replay.IsVisible.ShouldBeTrue();
            IntroButton(window, "IntroClose").Content.ShouldBe("Close");
            FocusManager.GetFocusedElement(window).ShouldBe(replay, "the keyboard lands on Replay (the window's focus, which Windows gives the keyboard while it is active)");
            Press(replay);
            players.Single().Plays.ShouldBe(2);
            players.Single().Rewinds.ShouldBe(1);
            replay.IsVisible.ShouldBeFalse();
            players.Single().End();
            Press(IntroButton(window, "IntroClose"));
            window.Modal.ShouldBeNull();
            Banner(window).Visibility.ShouldBe(Visibility.Visible);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_player_that_fails_closes_it_quietly_and_the_banner_shows(bool throwsOnPlay)
    {
        using var saver = new FakeSaver();
        var ui = AeroFixtures.Moved();
        var shell = AeroFixtures.Shell(saver, ui);
        OnWindow(shell, () => Player(throwsOnPlay), reduced: false, (window, players) =>
        {
            if (!throwsOnPlay)
            {
                window.Modal.ShouldNotBeNull();
                players.Single().Fail();   // MediaElement's MediaFailed
            }
            window.Modal.ShouldBeNull();
            ((FrameworkElement)window.FindName("Scrim")).Visibility.ShouldBe(Visibility.Collapsed);
            Banner(window).Visibility.ShouldBe(Visibility.Visible);
            window.ToastText.ShouldBeNull("quietly");
            ui.Current.AeroIntroSeen.ShouldBeTrue();
        });
    }

    [Fact]
    public void A_missing_video_goes_straight_to_the_banner()
    {
        File.Delete(_media.Video);
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver, AeroFixtures.Moved());
        OnWindow(shell, () => Player(), reduced: false, (window, players) =>
        {
            window.Modal.ShouldBeNull();
            players.ShouldBeEmpty();
            Banner(window).Visibility.ShouldBe(Visibility.Visible);
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Under_reduced_motion_or_reduced_transparency_it_shows_the_poster_and_play(bool reducedMotion, bool reduceTransparency)
    {
        using var saver = new FakeSaver();
        var ui = AeroFixtures.Moved();
        ui.Current = ui.Current with { Glass = GlassSettings.Default with { ReduceTransparency = reduceTransparency } };
        var shell = AeroFixtures.Shell(saver, ui);
        OnWindow(shell, () => Player(), reduced: reducedMotion, (window, players) =>
        {
            window.Modal.ShouldNotBeNull();
            players.Single().Plays.ShouldBe(0, "nothing autoplays");
            var poster = UiHarness.Find<Image>(window.Modal, i => i.Name == "IntroPoster")!;
            poster.IsVisible.ShouldBeTrue();
            poster.Source.ShouldNotBeNull();
            players.Single().View!.IsVisible.ShouldBeFalse("the video waits behind its poster");
            var play = IntroButton(window, "IntroPlay");
            play.IsVisible.ShouldBeTrue();
            AutomationProperties.GetName(play).ShouldBe("Play the intro");
            FocusManager.GetFocusedElement(window).ShouldBe(play, "the keyboard lands on Play (the window's focus, which Windows gives the keyboard while it is active)");
            IntroButton(window, "IntroClose").Content.ShouldBe("Close");

            Press(play);

            players.Single().Plays.ShouldBe(1);
            play.IsVisible.ShouldBeFalse();
            players.Single().View!.IsVisible.ShouldBeTrue();
            Banner(window).Visibility.ShouldBe(Visibility.Collapsed);
        });
    }

    [Fact]
    public void Settings_watch_the_aero_intro_opens_it_again_from_its_button_and_counts_nothing()
    {
        using var saver = new FakeSaver();
        var ui = AeroFixtures.Moved(introduced: true);
        ui.Current = ui.Current with { AeroIntroSeen = true };
        var shell = AeroFixtures.Shell(saver, ui);
        OnWindow(shell, () => Player(), reduced: false, (window, players) =>
        {
            window.Modal.ShouldBeNull();
            shell.Page = Page.Settings;
            UiHarness.Pump(TimeSpan.FromMilliseconds(400));
            var watch = UiHarness.Find<Button>(window.PageHost, b => AutomationProperties.GetName(b) == "Watch the Aero intro")!;
            watch.Content.ShouldBe("Watch the Aero intro");

            Press(watch);
            UiHarness.Pump(TimeSpan.FromMilliseconds(100));

            window.Modal.ShouldNotBeNull();
            players.Single().Plays.ShouldBe(1);
            ui.Changes.ShouldBeEmpty("a manual play counts against nothing");
            Press(IntroButton(window, "IntroClose"));
            window.Modal.ShouldBeNull();
            Banner(window).Visibility.ShouldBe(Visibility.Collapsed, "the banner was retired already");

            Press(watch);
            window.Modal.ShouldNotBeNull("it opens every time");
            players.Count.ShouldBe(2);
        });
    }

    [Fact]
    public void The_intro_draws_on_its_poster_in_both_themes()
    {
        Directory.CreateDirectory(UiHarness.Folder);
        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            using var saver = new FakeSaver();
            var shell = AeroFixtures.Shell(saver, AeroFixtures.Moved());
            OnWindow(shell, () => Player(), reduced: true, (window, _) =>
            {
                window.Modal.ShouldNotBeNull();
                Settle(window);
                UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, $"aero-intro-{theme}.png");
            }, theme);
            new FileInfo(Path.Combine(UiHarness.Folder, $"aero-intro-{theme}.png")).Length.ShouldBeGreaterThan(20_000, theme.ToString());
        }
    }
}
