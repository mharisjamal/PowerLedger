using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;
using static PowerLedger.App.Tests.MidnightHost;
using static PowerLedger.App.Tests.UiHarness;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan O task M2-5: the shared dialogs keep their layouts and take Midnight's colours through the palette alone. Each
/// is drawn under both Midnight palettes to a PNG, and every solid colour its own elements paint with is one the
/// palette defines, so no hardcoded brush survives the switch.
/// </summary>
[Trait("Category", "UI")]
public class MidnightDialogRenderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Colours no palette names that a dialog may still paint with: white on the brand's amber tile is the mark itself.</summary>
    private static readonly HashSet<Color> Allowed = [Colors.White, Colors.Transparent];

    private static readonly string[] Dialogs =
    [
        "consent", "join-prompt", "join-prompt-code", "approve-prompt", "confirm-join", "add-pc-confirm", "recovery-code", "sent",
        "feedback", "whats-new",
    ];

    [Fact]
    public void The_shared_dialogs_draw_in_both_midnight_palettes_with_no_colour_outside_them()
    {
        Directory.CreateDirectory(Folder);
        var sentFolder = Path.Combine(Path.GetTempPath(), "powerledger-renders-midnight-sent");
        Directory.CreateDirectory(sentFolder);
        File.WriteAllBytes(Path.Combine(sentFolder, "2026-09-07.json.gz"), new byte[8_192]);
        File.WriteAllBytes(Path.Combine(sentFolder, "2026-09-06.json.gz"), new byte[9_400]);

        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                var colours = PaletteColours(theme);
                foreach (var (name, open) in Windows(sentFolder))
                {
                    var window = Dressed(open(), theme);   // on the window, not the application (review 11)
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -20000;
                    window.Top = 0;
                    window.ShowInTaskbar = false;
                    window.ShowActivated = false;
                    window.Show();
                    try
                    {
                        Pump(TimeSpan.FromMilliseconds(300));
                        var strays = PaintedColours(window).Where(p => !colours.Contains(p.Colour) && !Allowed.Contains(p.Colour)).Distinct().ToList();
                        strays.ShouldBeEmpty($"{name} on {theme} paints outside the palette: {string.Join("; ", strays.Select(s => $"{s.Where} {s.Colour}"))}");
                        Render(window, (int)window.ActualWidth, (int)window.ActualHeight, $"midnight-{name}-{theme}.png");
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
        });

        foreach (var name in Dialogs)
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                new FileInfo(Path.Combine(Folder, $"midnight-{name}-{theme}.png")).Length.ShouldBeGreaterThan(5_000, $"{name} on {theme}");
            }
        }
    }

    /// <summary>The dialogs as the Classic render tests set them up, in the order of <see cref="Dialogs"/>.</summary>
    private static IEnumerable<(string Name, Func<Window> Open)> Windows(string sentFolder)
    {
        yield return ("consent", () =>
        {
            var link = Connected();
            return new ConsentDialog(new ConsentViewModel(link, UiThreads.Inline, _ => { })) { Width = 640 };
        });
        yield return ("join-prompt", () =>
        {
            var link = new FakeLink
            {
                Status = Statuses.Running() with { Household = new HouseholdStatus("hh1", "aaaa", "This-PC", ChassisKind.Desktop, true, [], null) },
            };
            link.Connect(true);
            var notice = new HouseholdNotice(
                NoticeKind.JoinPrompt, "p1", "Join Desktop-7's household? Joining leaves the household this PC is in now.", "Desktop-7", "482 913",
                Now.AddMinutes(2));
            return new JoinPromptWindow(new JoinPromptViewModel(link, UiThreads.Inline, new FakeTimeProvider(Now), notice)) { MaxHeight = 420 };
        });
        yield return ("join-prompt-code", () =>
        {
            var notice = new HouseholdNotice(NoticeKind.JoinPrompt, "p1", "Join the household of the PC that made this code?", null, null, Now.AddMinutes(2));
            return new JoinPromptWindow(new JoinPromptViewModel(Connected(), UiThreads.Inline, new FakeTimeProvider(Now), notice)) { MaxHeight = 420 };
        });
        yield return ("approve-prompt", () =>
        {
            var notice = new HouseholdNotice(
                NoticeKind.ApprovePrompt, "p2", "A PC signed in as you asks to join your household. Approve it?", null, "482 913", Now.AddMinutes(2));
            var model = new ApprovePromptViewModel(Connected(), UiThreads.Inline, new FakeTimeProvider(Now), notice, requestChanged: true);
            return new ApprovePromptWindow(model) { MaxHeight = 420 };
        });
        yield return ("confirm-join", () =>
        {
            var notice = new HouseholdNotice(
                NoticeKind.ConfirmJoin, "p3", "Does your other PC show 482 913? Approve it there too.", null, "482 913", Now.AddMinutes(2));
            return new ConfirmJoinWindow(new ConfirmJoinViewModel(Connected(), UiThreads.Inline, new FakeTimeProvider(Now), notice)) { MaxHeight = 420 };
        });
        yield return ("add-pc-confirm", () =>
        {
            var link = Connected();
            var model = new AddPcViewModel(link, UiThreads.Inline, new FakeTimeProvider(Now));
            link.PushNotice(new HouseholdNotice(NoticeKind.ConfirmCode, "confirm-1", "Does Laptop-2 show 482 913?", "Laptop-2", "482 913", Now.AddMinutes(2)));
            return new AddPcWindow(model) { MaxHeight = 420 };
        });
        yield return ("recovery-code", () =>
        {
            var notice = new HouseholdNotice(
                NoticeKind.RecoveryCode, "recovery-1", "Here's your recovery code.", null, null, null, "K7QM-2XHD-9PW4-R8TA-VMNP-3QWE");
            return new RecoveryCodeWindow(new RecoveryCodeViewModel(Connected(), notice, new FakeSaver(), _ => { })) { MaxHeight = 420 };
        });
        yield return ("sent", () => new SentWindow(new SentViewModel(Connected(), UiThreads.Inline, sentFolder, English, _ => { })));
        yield return ("feedback", () =>
        {
            var sender = new FeedbackSender(new FakeHttp().Client(), Path.Combine(Path.GetTempPath(), "pl-feedback-midnight-render-tests"), new FakeTimeProvider(Now));
            return new SendFeedbackWindow(new FeedbackViewModel(sender, UiThreads.Inline, () => null), null, new FakeImagePicker()) { MaxHeight = 560 };
        });
        // Review 3: Midnight's update card opens this window now, so it is drawn under Midnight's palette too.
        yield return ("whats-new", () =>
        {
            var points = WhatsNew.Releases.Single(release => release.Version == "0.8.0").Points;
            return new WhatsNewWindow(new WhatsNewViewModel("What's new in 0.8.0", points, new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }))) { MaxHeight = 420 };
        });
    }

    private static FakeLink Connected()
    {
        var link = new FakeLink();
        link.Connect(true);
        return link;
    }
}
