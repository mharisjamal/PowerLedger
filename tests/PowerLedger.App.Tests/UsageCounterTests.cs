using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public partial class UsageCounterTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
    private readonly FakeLink _link = new();
    private readonly FakeUiSettings _ui = new();
    private readonly FakeTimeProvider _clock = new(Start);

    private UsageCounter Model(CultureInfo? culture = null) => new(_link, _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, culture ?? English);

    [Fact]
    public async Task Counts_add_up_and_clear_once_the_service_takes_them()
    {
        _link.Connect(true);
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));
        var counter = Model();

        counter.CountAppOpen();
        counter.CountAppOpen();
        counter.CountPage("now");
        counter.CountPage("now");
        counter.CountPage("settings");
        counter.CountSetting("theme");
        counter.CountReportExported();
        counter.CountUpdateInstalled();

        await counter.FlushAsync();

        var sent = _link.Writes.OfType<UsageCounts>().Single();
        sent.AppOpens.ShouldBe(2);
        sent.Pages["now"].ShouldBe(2);
        sent.Pages["settings"].ShouldBe(1);
        sent.Settings["theme"].ShouldBe(1);
        sent.ReportsExported.ShouldBe(1);
        sent.UpdatesInstalled.ShouldBe(1);

        _link.Writes.Clear();
        await counter.FlushAsync();
        _link.Writes.OfType<UsageCounts>().Single().AppOpens.ShouldBe(0);   // cleared: nothing new since
    }

    [Fact]
    public async Task Nothing_is_sent_or_kept_while_usage_is_off()
    {
        _link.Connect(true);
        _link.Status = Statuses.WithSharing(Consent.Unanswered);   // usage off
        var counter = Model();
        counter.CountAppOpen();

        await counter.FlushAsync();

        _link.Writes.OfType<UsageCounts>().ShouldBeEmpty();

        // turning usage on afterwards sends nothing from before it was on: this flush only learns of the change, so it
        // drops what it holds rather than sending it, even though nothing has been counted since off was last seen
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));
        await counter.FlushAsync();
        _link.Writes.OfType<UsageCounts>().ShouldBeEmpty();

        // counting resumes normally now that usage is known to be on
        counter.CountAppOpen();
        await counter.FlushAsync();
        _link.Writes.OfType<UsageCounts>().Single().AppOpens.ShouldBe(1);
    }

    [Fact]
    public async Task Starting_reads_the_current_consent_so_a_later_flip_to_usage_on_is_recognised_as_a_change()
    {
        _link.Connect(true);
        _link.Status = Statuses.WithSharing(Consent.Unanswered);   // usage off when the App starts
        var counter = Model();
        counter.Start();   // reads the status once, off, without waiting for a flush

        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));   // usage turns on
        counter.CountAppOpen();
        await counter.FlushAsync();

        _link.Writes.OfType<UsageCounts>().ShouldBeEmpty();   // Start already knew it was off, so this flush's turn-on is caught
    }

    [Fact]
    public async Task A_consent_the_app_itself_just_sent_is_known_before_the_next_flush()
    {
        _link.Connect(true);
        _link.Status = Statuses.WithSharing(Consent.Unanswered);
        var counter = Model();
        counter.ConsentChanged(Consent.Unanswered);   // the dialog's own Allow none just saved this, off

        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));   // and now it's on
        counter.CountAppOpen();
        await counter.FlushAsync();

        _link.Writes.OfType<UsageCounts>().ShouldBeEmpty();   // ConsentChanged already knew it was off, so this flush's turn-on is caught
    }

    [Fact]
    public async Task A_consent_the_app_itself_just_turned_on_lets_the_very_next_flush_send()
    {
        _link.Connect(true);
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));
        var counter = Model();
        counter.ConsentChanged(new Consent(ConsentText.Version, false, true, false, false));   // Save just turned usage on
        counter.CountAppOpen();

        await counter.FlushAsync();

        _link.Writes.OfType<UsageCounts>().Single().AppOpens.ShouldBe(1);
    }

    [Fact]
    public void Start_before_the_link_connects_seeds_again_once_it_does()
    {
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));
        var counter = Model();

        counter.Start();   // not connected yet: the seed finds nothing
        _link.StatusReads.ShouldBe(1);

        _link.Connect(true);   // connects a moment later

        _link.StatusReads.ShouldBe(2);   // seeded again, rather than waiting up to fifteen minutes for the first flush
    }

    [Fact]
    public async Task A_consent_answered_before_the_seed_could_run_drops_what_was_counted_before_it()
    {
        var counter = Model();
        counter.Start();          // not connected yet: the seed can't learn the current consent
        counter.CountAppOpen();   // e.g. counted while the main window was already open but the pipe hadn't connected

        counter.ConsentChanged(new Consent(ConsentText.Version, false, true, false, false));   // the dialog's Allow ... Usage

        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));
        _link.Connect(true);

        await counter.FlushAsync();
        _link.Writes.OfType<UsageCounts>().Single().AppOpens.ShouldBe(0);   // the pre-consent open did not go out

        _link.Writes.Clear();
        counter.CountAppOpen();   // counted after consent is known
        await counter.FlushAsync();
        _link.Writes.OfType<UsageCounts>().Single().AppOpens.ShouldBe(1);
    }

    [Fact]
    public async Task Counts_are_kept_when_the_service_cannot_be_reached()
    {
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));
        var counter = Model();   // never connected: GetStatusAsync answers null
        counter.CountAppOpen();

        await counter.FlushAsync();

        _link.Writes.ShouldBeEmpty();   // never got far enough to send: no status, so nothing sent, but nothing cleared either

        _link.Connect(true);
        await counter.FlushAsync();
        _link.Writes.OfType<UsageCounts>().Single().AppOpens.ShouldBe(1);   // the earlier open was kept
    }

    [Fact]
    public async Task A_count_made_while_a_flush_is_in_flight_survives_it()
    {
        _link.Connect(true);
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));
        var gate = new TaskCompletionSource<ServiceStatus?>();
        _link.StatusGate = gate;
        var counter = Model();
        counter.CountAppOpen();

        var flush = counter.FlushAsync();   // took its snapshot (1 open), now waiting on the gated status read
        counter.CountAppOpen();             // a second open arrives while that flush is still on its way

        gate.SetResult(_link.Status);
        await flush;

        _link.Writes.OfType<UsageCounts>().Single().AppOpens.ShouldBe(1);   // only what the snapshot held

        _link.Writes.Clear();
        _link.StatusGate = null;
        await counter.FlushAsync();

        _link.Writes.OfType<UsageCounts>().Single().AppOpens.ShouldBe(1);   // the second open: not lost, not doubled
    }

    [Fact]
    public async Task The_day_the_counts_are_sent_under_is_the_day_they_were_made()
    {
        _link.Connect(true);
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));
        var counter = Model();
        counter.CountAppOpen();

        _clock.Advance(TimeSpan.FromDays(1));
        await counter.FlushAsync();
        _link.Writes.OfType<UsageCounts>().Single().Day.ShouldBe("2026-09-24");

        _link.Writes.Clear();
        counter.CountAppOpen();
        await counter.FlushAsync();
        _link.Writes.OfType<UsageCounts>().Single().Day.ShouldBe("2026-09-25");
    }

    [Fact]
    public void Days_since_first_run_counts_whole_local_days()
    {
        _ui.Current = _ui.Current with { FirstRunAt = Start - TimeSpan.FromDays(3) };
        _link.Connect(true);
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));
        var counter = Model();
        counter.CountAppOpen();

        _ = counter.FlushAsync();

        _link.Writes.OfType<UsageCounts>().Single().DaysSinceFirstRun.ShouldBe(3);
    }

    [Theory]
    [InlineData("System", "system")]
    [InlineData("Dark", "dark")]
    [InlineData("Light", "light")]
    public void Theme_is_sent_as_light_dark_or_system(string choice, string expected)
    {
        _ui.Current = _ui.Current with { Theme = Enum.Parse<ThemeChoice>(choice) };
        _link.Connect(true);
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));
        var counter = Model();
        counter.CountAppOpen();

        _ = counter.FlushAsync();

        _link.Writes.OfType<UsageCounts>().Single().Theme.ShouldBe(expected);
    }

    [Fact]
    public void An_empty_ui_culture_is_sent_as_en()
    {
        _link.Connect(true);
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, false, false));
        var counter = Model(CultureInfo.InvariantCulture);
        counter.CountAppOpen();

        _ = counter.FlushAsync();

        _link.Writes.OfType<UsageCounts>().Single().Language.ShouldBe("en");
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,39}$")]
    private static partial Regex NamePattern();

    [Theory]
    [InlineData("now")]
    [InlineData("breakdown")]
    [InlineData("report")]
    [InlineData("settings")]
    [InlineData("theme")]
    [InlineData("startWithWindows")]
    [InlineData("readMonitorBrightness")]
    [InlineData("tariff")]
    [InlineData("machine")]
    public void Every_page_and_setting_name_the_app_uses_is_a_valid_counted_name(string name)
        => NamePattern().IsMatch(name).ShouldBeTrue();
}
