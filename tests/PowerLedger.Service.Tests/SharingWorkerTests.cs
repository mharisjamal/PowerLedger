using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Service.Sharing;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>The collector and uploader (data-sharing design §4), with a fake server and a fake clock in a UTC+2 zone.</summary>
public sealed class SharingWorkerTests : IDisposable
{
    private const int SendMinute = 60;                       // 01:00 local
    private readonly Harness _h = new();

    /// <summary>A local time on a day of September 2026, in the harness's UTC+2 zone.</summary>
    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2026, 9, day, hour, minute, 0, TimeSpan.FromHours(2));

    [Fact]
    public async Task Nothing_is_sent_and_nothing_kept_before_the_user_answers_or_with_every_switch_off()
    {
        _h.Readings(Local(24, 10), TimeSpan.FromMinutes(10));
        _h.CrashFile(Local(24, 10));
        _h.Outbox.AddEvent("2026-09-23", OutboxEvents.Usage, "{}");

        _h.Clock.SetUtcNow(Local(25, 2));
        await _h.TickAsync();

        _h.Client.Calls.ShouldBeEmpty();
        _h.Outbox.Days().ShouldBeEmpty();
        Directory.GetFiles(_h.Crashes).ShouldBeEmpty();

        (await _h.Consent(false, false, false)).Ok.ShouldBeTrue();          // Allow none
        _h.Clock.Advance(TimeSpan.FromDays(1));
        await _h.TickAsync();
        (await _h.Run(new SendNowCommand(2))).Ok.ShouldBeFalse();

        _h.Client.Calls.ShouldBeEmpty();
        _h.Store.InstallId.ShouldBeNull();
        _h.Outbox.Days().ShouldBeEmpty();
    }

    [Fact]
    public async Task Tonights_minute_sends_yesterday_from_the_moment_the_user_said_yes()
    {
        _h.Readings(Local(24, 9, 30), TimeSpan.FromMinutes(60));            // 09:30 to 10:30, half of it before the consent
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(false, false, true);
        _h.Clock.SetUtcNow(Local(24, 10, 40));
        await _h.TickAsync();
        _h.Clock.SetUtcNow(Local(25, 0, 59));
        await _h.TickAsync();
        _h.Client.Reports.ShouldBeEmpty();                                   // not before the minute

        _h.Clock.SetUtcNow(Local(25, 1, 1));
        await _h.TickAsync();

        var report = _h.Client.Reports.ShouldHaveSingleItem();
        (report.Day, report.UtcOffsetMinutes, report.InstallId).ShouldBe(("2026-09-24", 120, _h.Store.InstallId));
        report.Power.ShouldNotBeNull().Minutes.T.ShouldBe(Enumerable.Range(600, 30).ToArray());
        report.Diagnostics.ShouldBeNull();
        report.Usage.ShouldBeNull();
        ReportSchema.Problems(ReportJson.Write(report)).ShouldBeNull();
        _h.Outbox.Days().ShouldBeEmpty();
        var copy = Path.Combine(_h.Sent, "2026-09-24.json.gz");
        File.ReadAllBytes(copy).ShouldBe(_h.Client.Calls.Last().Body);
        _h.Store.LastSent.ShouldBe(new LastSent(Local(25, 1, 1).ToUnixTimeMilliseconds(), new FileInfo(copy).Length));
    }

    [Fact]
    public async Task A_missed_night_is_caught_up_at_the_next_tick()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, false, true);
        _h.Readings(Local(24, 10), TimeSpan.FromMinutes(20));
        _h.Clock.SetUtcNow(Local(24, 22));
        await _h.TickAsync();

        _h.Clock.SetUtcNow(Local(25, 9));                                    // off from 22:00 until 09:00
        await _h.TickAsync();

        var report = _h.Client.Reports.ShouldHaveSingleItem();
        report.Day.ShouldBe("2026-09-24");
        report.Diagnostics.ShouldNotBeNull();
        report.Power.ShouldNotBeNull().Minutes.T.Length.ShouldBe(20);
    }

    [Fact]
    public async Task At_most_seven_days_go_in_one_run_oldest_first()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(false, false, true);
        foreach (var day in Enumerable.Range(15, 9)) _h.Outbox.InsertMinutes([SharingFakes.Minute(600, $"2026-09-{day}")]);

        _h.Clock.SetUtcNow(Local(24, 1, 1));
        await _h.TickAsync();

        _h.Client.Reports.Select(report => report.Day).ShouldBe(Enumerable.Range(15, 7).Select(day => $"2026-09-{day}"));
        _h.Outbox.Days().ShouldBe(new[] { "2026-09-22", "2026-09-23" });

        var reply = await _h.Run(new SendNowCommand(3));
        (reply.Ok, reply.Message).ShouldBe((true, "Sent 2 days."));
        _h.Outbox.Days().ShouldBeEmpty();
        Directory.GetFiles(_h.Sent, "*.json.gz").Length.ShouldBe(9);
    }

    [Fact]
    public async Task Failed_runs_back_off_one_two_four_eight_sixteen_then_twenty_four_hours_and_a_success_ends_it()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(false, false, true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-23")]);
        _h.Client.Answer = _ => new SendOutcome.Unreachable("the server couldn't be reached");

        var steps = new List<double>();
        var now = Local(24, 1, 1);
        for (var run = 0; run < 7; run++)
        {
            _h.Clock.SetUtcNow(now);
            await _h.TickAsync();
            var backoff = _h.Store.Backoff.ShouldNotBeNull();
            steps.Add(TimeSpan.FromMilliseconds(backoff.NextMs - now.ToUnixTimeMilliseconds()).TotalHours);
            _h.Clock.Advance(TimeSpan.FromMinutes(5));
            await _h.TickAsync();                                             // not again until the back-off has passed
            now = DateTimeOffset.FromUnixTimeMilliseconds(backoff.NextMs);
        }

        steps.ShouldBe(new double[] { 1, 2, 4, 8, 16, 24, 24 });
        _h.Client.Reports.Count().ShouldBe(7);
        _h.Store.Problem.ShouldBe(new SendProblem("the server couldn't be reached", Rejected: false));

        _h.Client.Answer = _ => new SendOutcome.Accepted();
        _h.Clock.SetUtcNow(now);
        await _h.TickAsync();
        (_h.Store.Backoff, _h.Store.Problem).ShouldBe((null, null));
        _h.Outbox.MinuteDays().ShouldNotContain("2026-09-23");
    }

    [Fact]
    public async Task A_day_more_than_fourteen_days_old_is_dropped_unsent()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 10));
        await _h.Consent(false, false, true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-09"), SharingFakes.Minute(600, "2026-09-10")]);
        _h.Client.Answer = _ => new SendOutcome.Unreachable("no network");

        _h.Clock.SetUtcNow(Local(24, 0, 20));                                // before tonight's minute, so nothing goes
        await _h.TickAsync();

        _h.Outbox.Days().ShouldBe(new[] { "2026-09-10" });
        _h.Client.Reports.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rejected_day_is_dropped_and_the_run_moves_on()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(false, false, true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-22"), SharingFakes.Minute(600, "2026-09-23")]);
        _h.Client.Answer = call => call.Report?.Day == "2026-09-23" ? new SendOutcome.Rejected("the minutes must rise") : new SendOutcome.Accepted();

        _h.Clock.SetUtcNow(Local(24, 1, 1));
        await _h.TickAsync();

        _h.Client.Reports.Select(report => report.Day).ShouldBe(new[] { "2026-09-22", "2026-09-23" });
        _h.Outbox.Days().ShouldBeEmpty();
        _h.Store.Problem.ShouldBe(new SendProblem("the minutes must rise", Rejected: true));
        _h.Store.Backoff.ShouldBeNull();
        File.Exists(Path.Combine(_h.Sent, "2026-09-23.json.gz")).ShouldBeFalse();
    }

    [Fact]
    public async Task When_the_server_has_deleted_the_install_everything_is_forgotten()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(true, true, true, share: true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-22"), SharingFakes.Minute(600, "2026-09-23")]);
        File.WriteAllText(Path.Combine(_h.Sent, "2026-09-01.json.gz"), "old");
        _h.Client.Answer = _ => new SendOutcome.Gone();

        _h.Clock.SetUtcNow(Local(24, 1, 1));
        await _h.TickAsync();

        _h.Client.Reports.Count().ShouldBe(1);
        _h.Store.Consent.ShouldBe(new Consent(ConsentText.Version, false, false, false, false));
        (_h.Store.InstallId, _h.Store.Key, _h.Store.CollectedTo).ShouldBe((null, null, null));
        _h.Outbox.Days().ShouldBeEmpty();
        Directory.GetFiles(_h.Sent).ShouldBeEmpty();
    }

    [Fact]
    public async Task Turning_a_switch_off_deletes_its_unsent_data_and_tells_the_server()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, true, true);
        _h.Readings(Local(24, 10), TimeSpan.FromMinutes(10));
        await _h.Run(new ReportUsageCommand(5, Usage("2026-09-24")));
        await _h.Run(new ReportCrashCommand(6, Crash(Local(24, 10, 5))));
        _h.Clock.SetUtcNow(Local(24, 10, 20));
        await _h.TickAsync();
        _h.Outbox.MinuteDays().ShouldBe(new[] { "2026-09-24" });
        _h.Outbox.Events("2026-09-24", OutboxEvents.Crash).Count.ShouldBe(1);
        _h.Outbox.Events("2026-09-24", OutboxEvents.Sources).Count.ShouldBe(1);
        _h.Outbox.Events("2026-09-24", OutboxEvents.Usage).Count.ShouldBe(1);

        await _h.Consent(true, true, false);
        _h.Outbox.MinuteDays().ShouldBeEmpty();
        _h.Store.CollectedTo.ShouldBeNull();

        await _h.Consent(false, true, false);
        _h.Outbox.Events("2026-09-24", OutboxEvents.Crash).ShouldBeEmpty();
        _h.Outbox.Events("2026-09-24", OutboxEvents.Sources).ShouldBeEmpty();

        await _h.Consent(false, false, false);
        _h.Outbox.Days().ShouldBeEmpty();

        _h.Client.Calls.Where(call => call.Kind == "consent").Select(call => call.Consent).ShouldBe(new Consent?[]
        {
            new(ConsentText.Version, true, true, true, false),
            new(ConsentText.Version, true, true, false, false),
            new(ConsentText.Version, false, true, false, false),
            new(ConsentText.Version, false, false, false, false),
        });
        _h.Store.ConsentPending.ShouldBeFalse();
        _h.Store.InstallId.ShouldNotBeNull();                               // an answer of none keeps the ID; a delete forgets it
    }

    [Fact]
    public async Task A_consent_change_the_server_didnt_hear_is_posted_again_once_the_back_off_has_passed()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        _h.Client.Answer = _ => new SendOutcome.Unreachable("no network");
        (await _h.Consent(true, false, false)).Ok.ShouldBeTrue();
        _h.Store.ConsentPending.ShouldBeTrue();

        _h.Client.Answer = _ => new SendOutcome.Accepted();
        _h.Clock.SetUtcNow(Local(24, 10, 30));
        await _h.TickAsync();
        _h.Store.ConsentPending.ShouldBeTrue();                              // waiting out the back-off

        _h.Clock.SetUtcNow(Local(24, 11, 1));
        await _h.TickAsync();
        _h.Store.ConsentPending.ShouldBeFalse();
        _h.Client.Calls.Count(call => call.Kind == "consent").ShouldBe(2);
    }

    [Fact]
    public async Task A_consent_change_waiting_to_be_posted_backs_off_on_its_own_so_the_reports_keep_their_steps()
    {
        var reports = new List<DateTimeOffset>();
        var consents = new List<DateTimeOffset>();
        _h.Client.Answer = call =>
        {
            (call.Kind == "report" ? reports : consents).Add(_h.Clock.GetUtcNow());
            return new SendOutcome.Unreachable("no network");
        };
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(false, false, true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-23")]);

        for (var now = Local(24, 1, 1); now < Local(24, 17); now = now.AddMinutes(5))
        {
            _h.Clock.SetUtcNow(now);
            await _h.TickAsync();
        }

        reports.ShouldBe(new[] { Local(24, 1, 1), Local(24, 2, 1), Local(24, 4, 1), Local(24, 8, 1), Local(24, 16, 1) });
        consents.ShouldBe(new[] { Local(24, 0, 30), Local(24, 1, 31), Local(24, 3, 31), Local(24, 7, 31), Local(24, 15, 31) });

        _h.Client.Answer = _ => new SendOutcome.Accepted();
        _h.Clock.SetUtcNow(Local(25, 8, 1));
        await _h.TickAsync();
        (_h.Store.ConsentPending, _h.Store.ConsentBackoff, _h.Store.Backoff).ShouldBe((false, null, null));
    }

    [Fact]
    public async Task The_hardware_goes_with_the_first_upload_and_again_only_after_a_change()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(false, false, true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-22"), SharingFakes.Minute(600, "2026-09-23")]);
        _h.Clock.SetUtcNow(Local(24, 1, 1));
        await _h.TickAsync();

        _h.Client.Reports.Select(report => report.Power!.Hardware is not null).ShouldBe(new[] { true, false });

        _h.Board.Publish(SharingFakes.Settings with { Profile = SharingFakes.Settings.Profile with { FanCount = 5 } });
        _h.Clock.SetUtcNow(Local(25, 1, 1));
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-24")]);
        await _h.TickAsync();
        _h.Clock.SetUtcNow(Local(26, 1, 1));
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-25")]);
        await _h.TickAsync();

        _h.Client.Reports.Select(report => report.Power!.Hardware?.Profile.FanCount).ShouldBe(new int?[] { 3, null, 5, null });
    }

    [Fact]
    public async Task The_preview_holds_every_section_from_the_last_hour_and_passes_the_schema()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        _h.Readings(Local(24, 9, 30), TimeSpan.FromMinutes(30));

        var reply = await _h.Run(new PreviewCommand(7));

        reply.Ok.ShouldBeTrue();
        reply.Path.ShouldBe(Path.Combine(_h.Sent, "preview.json"));
        var json = File.ReadAllBytes(reply.Path!);
        Encoding.UTF8.GetString(json).ShouldContain("\n  \"installId\"");        // indented, for reading
        ReportSchema.Problems(json).ShouldBeNull();
        var report = ReportJson.Read(json);
        report.InstallId.ShouldBe(Guid.Empty.ToString("D"));                  // no ID yet
        report.Consent.ShouldBe(new ConsentDto(ConsentText.Version, true, true, true, true));
        report.Diagnostics.ShouldNotBeNull();
        report.Usage.ShouldNotBeNull();
        report.Power.ShouldNotBeNull().Hardware.ShouldNotBeNull();
        report.Power.Minutes.T.ShouldBe(Enumerable.Range(570, 30).ToArray());
        _h.Client.Calls.ShouldBeEmpty();
        _h.Outbox.Days().ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_forgets_only_once_the_server_has_deleted()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, true, true);
        var id = _h.Store.InstallId.ShouldNotBeNull();
        _h.Readings(Local(24, 10), TimeSpan.FromMinutes(10));
        _h.Clock.SetUtcNow(Local(24, 10, 20));
        await _h.TickAsync();
        _h.Client.Answer = call => call.Kind == "delete" ? new SendOutcome.Unreachable("no network") : new SendOutcome.Accepted();

        var failed = await _h.Run(new DeleteMyDataCommand(8));

        (failed.Ok, failed.Message).ShouldBe((false, "Couldn't delete your data: no network. Nothing was changed."));
        _h.Store.InstallId.ShouldBe(id);
        _h.Store.Consent.Power.ShouldBeTrue();
        _h.Outbox.Days().ShouldNotBeEmpty();

        _h.Client.Answer = _ => new SendOutcome.Accepted();
        var deleted = await _h.Run(new DeleteMyDataCommand(9));

        (deleted.Ok, deleted.Message).ShouldBe((true, "Your data has been deleted from the server."));
        _h.Client.Calls.Where(call => call.Kind == "delete").Select(call => call.InstallId).ShouldBe(new[] { id, id });
        _h.Store.InstallId.ShouldBeNull();
        _h.Store.Consent.ShouldBe(new Consent(ConsentText.Version, false, false, false, false));
        _h.Outbox.Days().ShouldBeEmpty();
    }

    [Fact]
    public async Task Usage_and_crashes_are_kept_only_while_their_switch_is_on_and_a_crash_only_from_after_the_answer()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(false, false, true);
        (await _h.Run(new ReportUsageCommand(1, Usage("2026-09-24")))).Ok.ShouldBeTrue();
        (await _h.Run(new ReportCrashCommand(2, Crash(Local(24, 10, 1))))).Ok.ShouldBeTrue();
        _h.Outbox.EventDays().ShouldBeEmpty();

        _h.Clock.SetUtcNow(Local(24, 11));
        await _h.Consent(true, true, true);
        await _h.Run(new ReportUsageCommand(3, Usage("2026-09-24")));
        await _h.Run(new ReportUsageCommand(4, Usage("2026-09-24")));
        await _h.Run(new ReportCrashCommand(5, Crash(Local(24, 10, 59))));    // from before the answer
        await _h.Run(new ReportCrashCommand(6, Crash(Local(24, 11, 1)) with { Message = @"Could not open C:\Users\alice\notes.txt" }));

        OutboxEvents.ParseUsage(_h.Outbox.Events("2026-09-24", OutboxEvents.Usage).ShouldHaveSingleItem()).ShouldNotBeNull().AppOpens.ShouldBe(4);
        var crash = SharingJson.Read(_h.Outbox.Events("2026-09-24", OutboxEvents.Crash).ShouldHaveSingleItem(), SharingJson.Default.CrashReport);
        crash.ShouldNotBeNull().Message.ShouldBe(@"Could not open %USERPROFILE%\notes.txt");
    }

    [Fact]
    public async Task At_most_twenty_crashes_are_kept_a_day()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, false, false);
        for (var i = 0; i < 25; i++) await _h.Run(new ReportCrashCommand(i, Crash(Local(24, 10, 1))));

        _h.Outbox.CountEvents("2026-09-24", OutboxEvents.Crash).ShouldBe(SharingWorker.MaxCrashesPerDay);
    }

    [Fact]
    public async Task The_services_own_crash_files_are_recorded_scrubbed_and_deleted_and_deleted_unread_while_diagnostics_is_off()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, false, false);
        _h.CrashFile(Local(24, 10, 2), @"Access to C:\Users\alice\AppData was denied for DESKTOP-TEST");
        _h.CrashFile(Local(24, 9, 58));                                      // from before the answer

        _h.Clock.SetUtcNow(Local(24, 10, 5));
        await _h.TickAsync();

        var crash = SharingJson.Read(_h.Outbox.Events("2026-09-24", OutboxEvents.Crash).ShouldHaveSingleItem(), SharingJson.Default.CrashReport);
        crash.ShouldNotBeNull().Message.ShouldBe(@"Access to %USERPROFILE%\AppData was denied for <machine>");
        Directory.GetFiles(_h.Crashes).ShouldBeEmpty();

        await _h.Consent(false, true, false);
        _h.CrashFile(Local(24, 10, 6));
        _h.Clock.SetUtcNow(Local(24, 10, 10));
        await _h.TickAsync();
        Directory.GetFiles(_h.Crashes).ShouldBeEmpty();
        _h.Outbox.Events("2026-09-24", OutboxEvents.Crash).ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_sources_failures_are_added_to_the_day_counting_again_from_zero_when_the_sensors_were_rebuilt()
    {
        _h.Board.Publish(SharingFakes.Status(sources: [new SourceStatus("battery", true, null, 3, "Before the answer.")]));
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, false, false);

        _h.Board.Publish(SharingFakes.Status(sources: [new SourceStatus("battery", true, null, 5, @"Failed for C:\Users\alice")]));
        await _h.TickAsync();
        _h.Board.Publish(SharingFakes.Status(sources: [new SourceStatus("battery", true, null, 1, "After a rebuild.")]));
        await _h.TickAsync();

        OutboxEvents.Read(_h.Outbox, "2026-09-24").Sources.ShouldBe(new Dictionary<string, SourceDay>
        {
            ["battery"] = new(3, "After a rebuild."),                          // 2 after the answer, then 1 after the rebuild
        });
    }

    [Fact]
    public async Task Usage_counted_for_a_day_already_sent_is_kept_under_today()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(false, true, false);
        await _h.Run(new ReportUsageCommand(1, Usage("2026-09-23")));
        _h.Clock.SetUtcNow(Local(24, 1, 1));
        await _h.TickAsync();
        _h.Client.Reports.ShouldHaveSingleItem().Day.ShouldBe("2026-09-23");

        await _h.Run(new ReportUsageCommand(2, Usage("2026-09-23")));       // an App that slept through midnight

        _h.Outbox.EventDays().ShouldBe(new[] { "2026-09-24" });
    }

    [Fact]
    public async Task A_day_whose_last_minutes_are_not_collected_yet_waits()
    {
        _h.Clock.SetUtcNow(Local(24, 23, 50));
        await _h.Consent(false, false, true);
        _h.Readings(Local(24, 23, 50), TimeSpan.FromMinutes(10));

        _h.Clock.SetUtcNow(Local(25, 0, 1));
        (await _h.Run(new SendNowCommand(1))).Message.ShouldBe("Nothing is waiting to be sent.");

        _h.Clock.SetUtcNow(Local(25, 0, 3));
        (await _h.Run(new SendNowCommand(2))).Message.ShouldBe("Sent 1 day.");
        _h.Client.Reports.ShouldHaveSingleItem().Power.ShouldNotBeNull().Minutes.T.ShouldBe(Enumerable.Range(1430, 10).ToArray());
    }

    [Fact]
    public async Task Send_now_says_what_happened()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(false, false, true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-23")]);
        _h.Client.Answer = call => call.Kind == "report" ? new SendOutcome.Refused("too many requests today") : new SendOutcome.Accepted();

        var refused = await _h.Run(new SendNowCommand(1));
        (refused.Ok, refused.Message).ShouldBe((false, "Couldn't send: too many requests today. Will try again."));
        _h.Store.Backoff.ShouldNotBeNull().Failures.ShouldBe(1);

        _h.Client.Answer = _ => new SendOutcome.Rejected("the minutes must rise");
        var rejected = await _h.Run(new SendNowCommand(2));
        (rejected.Ok, rejected.Message).ShouldBe((false, "Rejected by the server: the minutes must rise."));
        _h.Outbox.Days().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_status_says_what_the_user_chose_and_how_sending_is_going()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.TickAsync();
        _h.Board.Status.ShouldNotBeNull().Sharing.ShouldBe(new SharingStatus(Consent.Unanswered, null, null, null, null, false, 0));

        await _h.Consent(true, false, true);
        var consented = _h.Board.Status.ShouldNotBeNull().Sharing.ShouldNotBeNull();
        (consented.Consent, consented.InstallId, consented.LastSentAt, consented.Problem, consented.DaysWaiting)
            .ShouldBe((new Consent(ConsentText.Version, true, false, true, false), _h.Store.InstallId, null, null, 0));

        _h.Readings(Local(24, 10), TimeSpan.FromMinutes(5));
        _h.Client.Answer = call => call.Kind == "report" ? new SendOutcome.Unreachable("no network") : new SendOutcome.Accepted();
        _h.Clock.SetUtcNow(Local(25, 1, 1));
        await _h.TickAsync();
        var failing = _h.Board.Status.ShouldNotBeNull().Sharing.ShouldNotBeNull();
        (failing.Problem, failing.Rejected, failing.DaysWaiting, failing.LastSentAt).ShouldBe(("no network", false, 1, null));

        _h.Client.Answer = _ => new SendOutcome.Accepted();
        (await _h.Run(new SendNowCommand(2))).Ok.ShouldBeTrue();
        var sent = _h.Board.Status.ShouldNotBeNull().Sharing.ShouldNotBeNull();
        (sent.LastSentAt, sent.LastSentBytes, sent.Problem, sent.DaysWaiting)
            .ShouldBe((Local(25, 1, 1), new FileInfo(Path.Combine(_h.Sent, "2026-09-24.json.gz")).Length, null, 0));
    }

    [Fact]
    public async Task Running_it_ticks_at_start_and_answers_commands()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Worker.StartAsync(CancellationToken.None);
        try
        {
            _h.Commands.TryQueue(new SetConsentCommand(1, new Consent(ConsentText.Version, false, true, false, false))).ShouldBeTrue();
            var preview = new PreviewCommand(2);
            _h.Commands.TryQueue(preview).ShouldBeTrue();
            (await preview.Reply.WaitAsync(TimeSpan.FromSeconds(5))).Ok.ShouldBeTrue();
            _h.Store.Consent.Usage.ShouldBeTrue();
        }
        finally
        {
            await _h.Worker.StopAsync(CancellationToken.None);
        }
        var late = new PreviewCommand(3);
        _h.Commands.TryQueue(late).ShouldBeFalse();
        (await late.Reply).ShouldBe(new SharingReply(3, false, "The service is stopping."));
    }

    public void Dispose() => _h.Dispose();

    private static UsageCounts Usage(string day) => new(day, 2, new Dictionary<string, int> { ["now"] = 1 }, new Dictionary<string, int>(), 0, 0, 3, "dark", "en-US");

    private static CrashReport Crash(DateTimeOffset at) =>
        new(at, "app", "0.6.0", ["System.InvalidOperationException"], "Collection was modified.", "   at X()");

    /// <summary>The worker wired to a temp database, a fake server, a fake clock in a UTC+2 zone and a board the loop would
    /// have published to; its ticks and commands are run by the test, one at a time.</summary>
    private sealed class Harness : IDisposable
    {
        private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("PL+2", TimeSpan.FromHours(2), "PL+2", "PL+2");

        public Harness()
        {
            Clock.SetLocalTimeZone(Zone);
            Board.Publish(SharingFakes.Settings);
            Board.Publish(SharingFakes.Status());
            Board.Publish(SharingFakes.Facts);
            Board.PublishDiscreteGpu(true);
            Directory.CreateDirectory(Sent);
            Directory.CreateDirectory(Crashes);
            var environment = new SharingEnvironment(Sent, Crashes, () => new ScrubNames("alice", "DESKTOP-TEST", "CONTOSO"), () => SharingFakes.Host,
                () => SendMinute);
            Worker = new SharingWorker(Database.Db, Board, Commands, Client, environment, Clock, NullLogger<SharingWorker>.Instance);
        }

        public TestDatabase Database { get; } = new();

        public FakeTimeProvider Clock { get; } = new(Local(24, 0));

        public StatusBoard Board { get; } = new();

        public SharingCommands Commands { get; } = new();

        public FakeSharingClient Client { get; } = new();

        public SharingWorker Worker { get; }

        public string Sent => Path.Combine(Database.Folder, "Sent");

        public string Crashes => Path.Combine(Database.Folder, "Crashes");

        public SharingStore Store => new(new SettingsRepository(Database.Db));

        public OutboxRepository Outbox => new(Database.Db);

        public Task TickAsync() => Worker.TickAsync(CancellationToken.None);

        public async Task<SharingReply> Run(SharingCommand command)
        {
            await Worker.HandleAsync(command, CancellationToken.None);
            return await command.Reply;
        }

        public Task<SharingReply> Consent(bool diagnostics, bool usage, bool power, bool share = false) =>
            Run(new SetConsentCommand(1, new Consent(ConsentText.Version, diagnostics, usage, power, share)));

        /// <summary>A reading a second from <paramref name="from"/> for <paramref name="span"/>, as the loop writes them.</summary>
        public void Readings(DateTimeOffset from, TimeSpan span) =>
            new RawSampleRepository(Database.Db).InsertBatch([.. Enumerable.Range(0, (int)span.TotalSeconds).Select(s => PowerLedger.Service.Tests.Readings.At(from.AddSeconds(s)))]);

        /// <summary>A crash file as the service writes one when it crashes.</summary>
        public void CrashFile(DateTimeOffset at, string message = "Boom.") =>
            ServiceCrashes.TryWrite(Crashes, new InvalidOperationException(message), at, "0.6.0").ShouldNotBeNull();

        public void Dispose() => Database.Dispose();
    }
}

/// <summary>A data server that answers as a test says, keeping every request.</summary>
internal sealed class FakeSharingClient : ISharingClient
{
    public List<SharingCall> Calls { get; } = [];

    public Func<SharingCall, SendOutcome> Answer { get; set; } = _ => new SendOutcome.Accepted();

    public IEnumerable<ReportV1> Reports => Calls.Where(call => call.Report is not null).Select(call => call.Report!);

    public Task<SendOutcome> SendReportAsync(byte[] gzipBody, string key, CancellationToken cancel = default) =>
        Record(new SharingCall("report", key, null, null, ReportJson.Read(Gunzip(gzipBody)), gzipBody));

    public Task<SendOutcome> SendConsentAsync(string installId, string key, Consent consent, CancellationToken cancel = default) =>
        Record(new SharingCall("consent", key, installId, consent, null, []));

    public Task<SendOutcome> DeleteAsync(string installId, string key, CancellationToken cancel = default) =>
        Record(new SharingCall("delete", key, installId, null, null, []));

    private Task<SendOutcome> Record(SharingCall call)
    {
        Calls.Add(call);
        return Task.FromResult(Answer(call));
    }

    private static byte[] Gunzip(byte[] body)
    {
        using var unpacked = new MemoryStream();
        using (var gzip = new GZipStream(new MemoryStream(body), CompressionMode.Decompress)) gzip.CopyTo(unpacked);
        return unpacked.ToArray();
    }
}

/// <param name="Kind"><c>report</c>, <c>consent</c> or <c>delete</c>.</param>
internal sealed record SharingCall(string Kind, string Key, string? InstallId, Consent? Consent, ReportV1? Report, byte[] Body);
