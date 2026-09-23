using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
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
    public async Task An_answer_to_an_older_wording_sends_nothing_and_a_new_answer_collects_from_its_own_moment()
    {
        // Every switch was turned on under the wording before this one; the ID, progress and a waiting day are from then.
        var older = new Consent(ConsentText.Version - 1, true, true, true, true);
        _h.Store.SaveConsent(new StoredConsent(older, Local(20, 10).ToUnixTimeMilliseconds(), Local(20, 10).ToUnixTimeMilliseconds()));
        _h.Store.Identity();
        _h.Store.CollectedTo = Local(23, 10).ToUnixTimeMilliseconds();
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-22")]);
        _h.Outbox.AddEvent("2026-09-23", OutboxEvents.Usage, OutboxEvents.MergeUsage(null, Usage("2026-09-23")));
        _h.CrashFile(Local(24, 9));
        _h.Readings(Local(24, 9), TimeSpan.FromMinutes(30));

        _h.Clock.SetUtcNow(Local(24, 9, 40));
        await _h.TickAsync();
        _h.Clock.SetUtcNow(Local(25, 1, 1));
        await _h.TickAsync();
        (await _h.Run(new SendNowCommand(1))).ShouldBe(new SharingReply(1, false, "Nothing is sent until you choose what to share."));
        await _h.Run(new ReportUsageCommand(2, Usage("2026-09-25")));

        _h.Client.Calls.ShouldBeEmpty();
        _h.Outbox.Days().ShouldBeEmpty();
        Directory.GetFiles(_h.Crashes).ShouldBeEmpty();
        _h.Board.Status.ShouldNotBeNull().Sharing.ShouldNotBeNull().DaysWaiting.ShouldBe(0);

        _h.Clock.SetUtcNow(Local(25, 9, 15));
        await _h.Consent(false, false, true);
        _h.Store.CollectedTo.ShouldBe(Local(25, 9, 15).ToUnixTimeMilliseconds());
        _h.Readings(Local(25, 9), TimeSpan.FromMinutes(30));                   // 09:00 to 09:30, half before the answer
        _h.Clock.SetUtcNow(Local(25, 9, 40));
        await _h.TickAsync();

        _h.Outbox.MinuteDays().ShouldBe(new[] { "2026-09-25" });
        _h.Outbox.Minutes("2026-09-25").Select(minute => minute.Minute).ShouldBe(Enumerable.Range(555, 15));
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
    public async Task Send_now_the_next_day_sends_the_minutes_read_since_the_answer_with_no_tick_between()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, true, true);
        _h.Readings(Local(24, 10), TimeSpan.FromMinutes(3));

        _h.Clock.SetUtcNow(Local(25, 10, 5));                                // the Sandbox moves its clock on a day
        var reply = await _h.Run(new SendNowCommand(1));

        (reply.Ok, reply.Message).ShouldBe((true, "Sent 1 day."));
        var report = _h.Client.Reports.ShouldHaveSingleItem();
        report.Day.ShouldBe("2026-09-24");
        report.Power.ShouldNotBeNull().Minutes.T.ShouldBe([600, 601, 602]);
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
    public async Task A_new_consent_change_that_gave_way_is_posted_right_after_whatever_back_off_an_earlier_one_left()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        _h.Client.Answer = _ => new SendOutcome.Unreachable("no network");
        await _h.Consent(true, false, false);
        _h.Store.ConsentBackoff.ShouldNotBeNull();                            // not again for an hour

        _h.Clock.SetUtcNow(Local(24, 10, 10));
        _h.Client.AnswerAsync = async (_, cancel) =>
        {
            _h.Commands.TryQueue(new PreviewCommand(3)).ShouldBeTrue();       // the App's next request, while it is posted
            await Task.Delay(Timeout.Infinite, cancel);
            return new SendOutcome.Accepted();
        };
        await _h.Consent(true, true, false);
        _h.Client.AnswerAsync = null;
        _h.Client.Answer = _ => new SendOutcome.Accepted();
        await _h.TakeAndRunAsync();
        await _h.TickAsync();                                                 // the run that starts again once it is answered

        _h.Store.ConsentPending.ShouldBeFalse();
        _h.Client.Calls.Last(call => call.Kind == "consent").Consent.ShouldBe(new Consent(ConsentText.Version, true, true, false, false));
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
    public async Task Delete_with_a_key_this_account_cannot_read_turns_everything_off_and_says_how_to_have_the_data_deleted()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, true, true);
        var id = _h.Store.InstallId.ShouldNotBeNull();
        new SettingsRepository(_h.Database.Db).Set(SharingStore.KeyKey, "not a key this account protected");   // copied from another PC

        var reply = await _h.Run(new DeleteMyDataCommand(10));

        reply.Ok.ShouldBeFalse();
        reply.Message.ShouldBe("This PC's key can't be read, so the server can't be asked to delete what it holds. Every switch is "
            + $"now off. To have it deleted, write to the address in the privacy policy, quoting the install ID {id}.");
        _h.Client.Calls.Where(call => call.Kind == "delete").ShouldBeEmpty();
        _h.Store.InstallId.ShouldBeNull();
        _h.Store.Consent.ShouldBe(new Consent(ConsentText.Version, false, false, false, false));
    }

    [Fact]
    public async Task Delete_with_a_key_this_account_cannot_read_quotes_the_id_an_earlier_unreadable_key_left_too()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, true, true);
        var first = _h.Store.InstallId.ShouldNotBeNull();
        var settings = new SettingsRepository(_h.Database.Db);
        settings.Set(SharingStore.KeyKey, "not a key this account protected");
        await _h.Consent(true, true, false);                                  // a new ID
        var second = _h.Store.InstallId.ShouldNotBeNull();
        settings.Set(SharingStore.KeyKey, "not one either");

        var reply = await _h.Run(new DeleteMyDataCommand(10));

        reply.ShouldBe(new SharingReply(10, false, "This PC's key can't be read, so the server can't be asked to delete what it holds. "
            + $"Every switch is now off. To have it deleted, write to the address in the privacy policy, quoting the install IDs {second} and {first}."));
    }

    [Fact]
    public async Task A_key_that_cannot_be_read_gives_way_to_a_new_id_that_starts_clean_and_the_old_one_is_kept_to_quote()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(true, true, true, share: true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-23")]);
        _h.Clock.SetUtcNow(Local(24, 1, 1));
        await _h.TickAsync();                                                 // sent with the parts
        var old = _h.Store.InstallId.ShouldNotBeNull();
        new SettingsRepository(_h.Database.Db).Set(SharingStore.KeyKey, "not a key this account protected");   // copied from another PC

        _h.Clock.SetUtcNow(Local(24, 10));
        (await _h.Consent(true, true, true)).Ok.ShouldBeTrue();              // Share my detailed data turned off

        var id = _h.Store.InstallId.ShouldNotBeNull();
        (id == old, _h.Store.PreviousId).ShouldBe((false, old));
        _h.Client.Calls.Where(call => call.Kind == "consent").Select(call => call.InstallId).ShouldBe(new[] { old, id });
        _h.Board.Status.ShouldNotBeNull().Sharing.ShouldNotBeNull().Problem.ShouldBe(Unheard(old));   // the server keeps its share on

        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-24")]);
        _h.Clock.SetUtcNow(Local(25, 1, 1));
        await _h.TickAsync();
        var report = _h.Client.Reports.Last();
        (report.InstallId, report.Power.ShouldNotBeNull().Hardware is not null).ShouldBe((id, true));   // the new ID's first upload

        (await _h.Run(new DeleteMyDataCommand(3))).ShouldBe(new SharingReply(3, false,
            $"Your data has been deleted from the server. What was sent under the install ID {old}, whose key this PC couldn't read, "
            + "can't be deleted from here: to have it deleted, write to the address in the privacy policy, quoting that ID."));
        _h.Client.Calls.Where(call => call.Kind == "delete").Select(call => call.InstallId).ShouldBe(new[] { id });
        (await _h.Run(new DeleteMyDataCommand(4))).ShouldBe(new SharingReply(4, false,
            $"Every switch is now off. What was sent under the install ID {old}, whose key this PC couldn't read, "
            + "can't be deleted from here: to have it deleted, write to the address in the privacy policy, quoting that ID."));
    }

    [Fact]
    public async Task A_consent_change_the_server_cant_be_told_of_as_the_key_cannot_be_read_is_said_so_not_taken_as_heard()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, true, true, share: true);
        var id = _h.Store.InstallId.ShouldNotBeNull();
        new SettingsRepository(_h.Database.Db).Set(SharingStore.KeyKey, "not a key this account protected");

        (await _h.Consent(false, false, false)).Ok.ShouldBeTrue();          // everything withdrawn, share with it

        _h.Client.Calls.Where(call => call.Kind == "consent").Select(call => call.InstallId).ShouldBe(new[] { id });
        (_h.Store.ConsentPending, _h.Store.InstallId).ShouldBe((false, id));
        _h.Store.Problem.ShouldBe(new SendProblem(Unheard(id), Rejected: false, Lasts: true));
        var sharing = _h.Board.Status.ShouldNotBeNull().Sharing.ShouldNotBeNull();
        (sharing.Problem, sharing.ProblemLasts).ShouldBe((Unheard(id), true));   // it won't clear by trying again
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
        crash.ShouldNotBeNull().Message.ShouldBe("Could not open <path>");
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
        _h.CrashFile(Local(24, 10, 2), @"Access to 'C:\Users\alice\AppData' was denied for DESKTOP-TEST");
        _h.CrashFile(Local(24, 9, 58));                                      // from before the answer

        _h.Clock.SetUtcNow(Local(24, 10, 5));
        await _h.TickAsync();

        var crash = SharingJson.Read(_h.Outbox.Events("2026-09-24", OutboxEvents.Crash).ShouldHaveSingleItem(), SharingJson.Default.CrashReport);
        crash.ShouldNotBeNull().Message.ShouldBe("Access to '<path>' was denied for <machine>");
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
    public async Task While_the_loop_cant_write_its_readings_nothing_is_collected_so_the_ones_it_holds_are_once_written()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(false, false, true);
        _h.Readings(Local(24, 10), TimeSpan.FromMinutes(10));                  // written before the disk filled up
        _h.Board.Publish(SharingFakes.Status() with { WriteProblem = "Writes are failing (disk full); 600 readings are held in memory." });
        _h.Clock.SetUtcNow(Local(24, 10, 20));
        await _h.TickAsync();
        _h.Outbox.MinuteDays().ShouldBeEmpty();

        _h.Readings(Local(24, 10, 10), TimeSpan.FromMinutes(10));              // the ones held, written once there was room
        _h.Board.Publish(SharingFakes.Status());
        _h.Clock.SetUtcNow(Local(24, 10, 25));
        await _h.TickAsync();

        _h.Outbox.Minutes("2026-09-24").Select(minute => minute.Minute).ShouldBe(Enumerable.Range(600, 20));
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
    public async Task A_day_built_before_the_time_zone_changed_goes_with_the_offset_its_minutes_count_from()
    {
        // Built at home in UTC+2, sent after the laptop started up in UTC-4.
        _h.Clock.SetUtcNow(Local(24, 9));
        await _h.Consent(false, false, true);
        _h.Readings(Local(24, 10), TimeSpan.FromMinutes(30));
        _h.Clock.SetUtcNow(Local(24, 10, 40));
        await _h.TickAsync();

        _h.Clock.SetLocalTimeZone(West);
        _h.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 25, 1, 1, 0, TimeSpan.FromHours(-4)));   // tonight's minute where it is now
        await _h.TickAsync();

        var report = _h.Client.Reports.ShouldHaveSingleItem();
        (report.Day, report.UtcOffsetMinutes).ShouldBe(("2026-09-24", 120));
        Decoded(report).ShouldBe(Starts(Local(24, 10), 30));
    }

    [Fact]
    public async Task A_day_built_partly_in_each_of_two_time_zones_sends_every_minute_at_its_own_time()
    {
        _h.Clock.SetUtcNow(Local(24, 9));
        await _h.Consent(false, false, true);
        _h.Readings(Local(24, 10), TimeSpan.FromMinutes(30));
        _h.Clock.SetUtcNow(Local(24, 10, 40));
        await _h.TickAsync();                                                   // 10:00 to 10:30 in UTC+2: minutes 600 to 629

        _h.Clock.SetLocalTimeZone(West);                                        // the service starts up again in UTC-4
        var west = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(-4));
        _h.Readings(west, TimeSpan.FromMinutes(30));
        _h.Clock.SetUtcNow(west.AddMinutes(40));
        await _h.TickAsync();                                                   // the same day there, and the same minutes

        _h.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 25, 1, 1, 0, TimeSpan.FromHours(-4)));
        await _h.TickAsync();

        var report = _h.Client.Reports.ShouldHaveSingleItem();
        report.Day.ShouldBe("2026-09-24");
        Decoded(report).ShouldBe(Starts(Local(24, 10), 30).Concat(Starts(west, 30)).ToArray());
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
    public async Task Send_now_running_out_of_the_apps_time_is_no_fault_of_the_servers_so_it_is_no_problem_and_no_back_off()
    {
        _h.Clock.SetUtcNow(Local(24, 9));
        await _h.Consent(false, false, true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-23")]);
        _h.Client.AnswerAsync = async (call, cancel) =>
        {
            if (call.Kind == "report") await Task.Delay(Timeout.Infinite, cancel);   // slower than the App waits
            return new SendOutcome.Accepted();
        };
        var send = new SendNowCommand(2);
        _h.Commands.TryQueue(send).ShouldBeTrue();

        var handling = _h.TakeAndRunAsync();
        _h.Clock.Advance(SharingWorker.AppWait);

        (await send.Reply.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(
            new SharingReply(2, false, "Couldn't send: the server didn't answer in time. Will try again."));
        await handling;
        (_h.Store.Problem, _h.Store.Backoff).ShouldBe((null, null));
    }

    [Fact]
    public async Task Send_now_with_no_time_left_to_ask_the_server_leaves_the_upload_it_missed_due()
    {
        _h.Clock.SetUtcNow(Local(24, 9));                                     // the PC was off at tonight's minute
        await _h.Consent(false, false, true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-23")]);
        var send = new SendNowCommand(2);
        _h.Commands.TryQueue(send).ShouldBeTrue();
        _h.Clock.Advance(SharingWorker.AppWait);                              // all the App waits, behind another request

        await _h.TakeAndRunAsync();

        (await send.Reply).ShouldBe(new SharingReply(2, true, "Nothing went in time; the rest will go at the next chance."));
        (_h.Client.Reports.Count(), _h.Store.LastRun, _h.Store.Problem, _h.Store.Backoff).ShouldBe((0, null, null, null));
        await _h.TickAsync();
        _h.Client.Reports.ShouldHaveSingleItem().Day.ShouldBe("2026-09-23");
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
    public async Task How_sharing_stands_is_published_before_the_first_runs_requests_go()
    {
        // A consent change the server hasn't heard, so the first run starts with a request that takes its time.
        _h.Clock.SetUtcNow(Local(24, 10));
        var consent = new Consent(ConsentText.Version, true, false, false, false);
        _h.Store.SaveConsent(new StoredConsent(consent, Local(24, 9).ToUnixTimeMilliseconds(), Local(24, 9).ToUnixTimeMilliseconds()));
        _h.Store.Identity();
        _h.Store.ConsentPending = true;
        var calling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<SendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Client.AnswerAsync = (_, cancel) =>
        {
            calling.TrySetResult();
            return answer.Task.WaitAsync(cancel);
        };

        await _h.Worker.StartAsync(CancellationToken.None);
        try
        {
            await calling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _h.Board.Status.ShouldNotBeNull().Sharing.ShouldNotBeNull().Consent.ShouldBe(consent);
        }
        finally
        {
            answer.TrySetResult(new SendOutcome.Accepted());
            await _h.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_state_that_cannot_be_published_neither_fails_a_request_nor_stops_the_worker()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        new SettingsRepository(_h.Database.Db).Set(SharingStore.LastSentKey, $"{{\"atMs\":{long.MaxValue},\"bytes\":1}}");

        (await _h.Consent(true, false, false)).Ok.ShouldBeTrue();

        await _h.Worker.StartAsync(CancellationToken.None);
        try
        {
            foreach (var id in new long[] { 2, 3 })
            {
                var preview = new PreviewCommand(id);
                _h.Commands.TryQueue(preview).ShouldBeTrue();
                (await preview.Reply.WaitAsync(TimeSpan.FromSeconds(5))).Ok.ShouldBeTrue();
            }
            _h.Worker.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await _h.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_upload_under_way_gives_way_to_a_request_the_app_waits_on_and_goes_on_once_it_is_answered()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(false, false, true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-23")]);
        var uploading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Client.AnswerAsync = async (call, cancel) =>
        {
            if (call.Kind == "report" && uploading.TrySetResult()) await Task.Delay(Timeout.Infinite, cancel);   // the first takes for ever
            return new SendOutcome.Accepted();
        };
        _h.Clock.SetUtcNow(Local(24, 1, 1));

        await _h.Running(async () =>
        {
            await uploading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var preview = new PreviewCommand(2);
            _h.Commands.TryQueue(preview).ShouldBeTrue();

            (await preview.Reply.WaitAsync(TimeSpan.FromSeconds(5))).Ok.ShouldBeTrue();
            await WaitFor.True(_h.AllSent);
        });

        _h.Client.Reports.Select(report => report.Day).ShouldBe(new[] { "2026-09-23", "2026-09-23" });   // gave way, then went
        (_h.Store.Backoff, _h.Store.Problem).ShouldBe((null, null));
    }

    [Fact]
    public async Task A_run_that_gave_way_starts_again_only_once_the_request_it_gave_way_to_is_answered()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(false, false, true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-23")]);
        WakeLate(_h.Commands, TimeSpan.FromMilliseconds(20));
        var uploads = new SemaphoreSlim(0);
        _h.Client.AnswerAsync = async (call, cancel) =>
        {
            if (call.Kind == "report")
            {
                uploads.Release();
                await Task.Delay(Timeout.Infinite, cancel);                     // every upload takes until it gives way
            }
            return new SendOutcome.Accepted();
        };
        _h.Clock.SetUtcNow(Local(24, 1, 1));

        await _h.Running(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                (await uploads.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
                var preview = new PreviewCommand(i);
                _h.Commands.TryQueue(preview).ShouldBeTrue();
                (await preview.Reply.WaitAsync(TimeSpan.FromSeconds(5))).Ok.ShouldBeTrue();
            }
        });

        _h.RunsStartedWhileTheAppWaited.ShouldBe(0);
    }

    [Fact]
    public async Task A_consent_change_being_posted_gives_way_to_the_apps_next_request_and_is_posted_after_it()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        var posting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Client.AnswerAsync = async (call, cancel) =>
        {
            if (call.Kind == "consent" && posting.TrySetResult()) await Task.Delay(Timeout.Infinite, cancel);   // the first takes for ever
            return new SendOutcome.Accepted();
        };

        await _h.Running(async () =>
        {
            var consent = new SetConsentCommand(1, new Consent(ConsentText.Version, true, false, false, false));
            _h.Commands.TryQueue(consent).ShouldBeTrue();
            (await consent.Reply.WaitAsync(TimeSpan.FromSeconds(5))).Ok.ShouldBeTrue();
            await posting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var preview = new PreviewCommand(2);
            _h.Commands.TryQueue(preview).ShouldBeTrue();

            (await preview.Reply.WaitAsync(TimeSpan.FromSeconds(5))).Ok.ShouldBeTrue();
            await WaitFor.True(() => !_h.Store.ConsentPending);
        });

        _h.Client.Calls.Count(call => call.Kind == "consent").ShouldBe(2);
        _h.Store.ConsentBackoff.ShouldBeNull();
    }

    [Fact]
    public async Task A_switch_turned_off_during_an_upload_is_off_before_the_next_day_is_built()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(true, false, true);
        foreach (var day in new[] { "2026-09-21", "2026-09-22", "2026-09-23" })
        {
            _h.Outbox.InsertMinutes([SharingFakes.Minute(600, day)]);
            _h.Outbox.MergeEvent(day, OutboxEvents.Sources, json => OutboxEvents.MergeSources(json, new Dictionary<string, SourceDay> { ["battery"] = new(1, "No answer.") }));
        }
        var uploading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Client.AnswerAsync = async (call, cancel) =>
        {
            if (call.Kind == "report" && uploading.TrySetResult()) await Task.Delay(TimeSpan.FromMilliseconds(300), cancel);
            return new SendOutcome.Accepted();
        };
        _h.Clock.SetUtcNow(Local(24, 1, 1));

        await _h.Running(async () =>
        {
            await uploading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var withdraw = new SetConsentCommand(2, new Consent(ConsentText.Version, true, false, false, false));
            _h.Commands.TryQueue(withdraw).ShouldBeTrue();

            (await withdraw.Reply.WaitAsync(TimeSpan.FromSeconds(5))).Ok.ShouldBeTrue();
            await WaitFor.True(_h.AllSent);
        });

        // Only the upload under way when Hardware and power went off carried its minutes.
        _h.Client.Reports.First().Power.ShouldNotBeNull();
        _h.Client.Reports.Skip(1).Select(report => (report.Day, report.Consent.Power, report.Power is null)).ShouldBe(new[]
        {
            ("2026-09-21", false, true), ("2026-09-22", false, true), ("2026-09-23", false, true),
        });
    }

    [Fact]
    public async Task Send_now_stops_before_the_next_day_when_the_app_turns_a_switch_off()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));
        await _h.Consent(false, true, true);
        foreach (var day in new[] { "2026-09-21", "2026-09-22", "2026-09-23" }) _h.Outbox.InsertMinutes([SharingFakes.Minute(600, day)]);
        var uploading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<SendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Client.AnswerAsync = (call, cancel) =>
        {
            if (call.Kind != "report") return Task.FromResult<SendOutcome>(new SendOutcome.Accepted());
            uploading.TrySetResult();
            return answer.Task.WaitAsync(cancel);
        };

        await _h.Running(async () =>
        {
            var send = new SendNowCommand(2);
            _h.Commands.TryQueue(send).ShouldBeTrue();
            await uploading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var withdraw = new SetConsentCommand(3, new Consent(ConsentText.Version, false, true, false, false));
            _h.Commands.TryQueue(withdraw).ShouldBeTrue();
            answer.SetResult(new SendOutcome.Accepted());                       // the day under way goes

            (await send.Reply.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(new SharingReply(2, true, "Sent 1 day."));
            (await withdraw.Reply.WaitAsync(TimeSpan.FromSeconds(5))).Ok.ShouldBeTrue();
        });

        _h.Client.Reports.Select(report => report.Day).ShouldBe(new[] { "2026-09-21" });
        _h.Outbox.MinuteDays().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_the_pipe_gave_up_on_is_never_carried_out()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, true, true);
        var id = _h.Store.InstallId.ShouldNotBeNull();
        var consent = new SetConsentCommand(2, new Consent(ConsentText.Version, false, false, false, false));
        var delete = new DeleteMyDataCommand(3);

        foreach (SharingCommand command in new SharingCommand[] { consent, delete })
        {
            _h.Commands.TryQueue(command).ShouldBeTrue();
            command.TryGiveUp(PipeHandler.NoAnswer).ShouldBeTrue();           // as the pipe does at its time limit
        }
        await _h.Running(async () =>
        {
            var preview = new PreviewCommand(4);                                // queued after, so answered after
            _h.Commands.TryQueue(preview).ShouldBeTrue();
            (await preview.Reply.WaitAsync(TimeSpan.FromSeconds(5))).Ok.ShouldBeTrue();
        });

        (await consent.Reply).ShouldBe(new SharingReply(2, false, PipeHandler.NoAnswer));
        (await delete.Reply).ShouldBe(new SharingReply(3, false, PipeHandler.NoAnswer));
        _h.Store.Consent.ShouldBe(new Consent(ConsentText.Version, true, true, true, false));
        _h.Store.InstallId.ShouldBe(id);
        _h.Client.Calls.ShouldNotContain(call => call.Kind == "delete");
    }

    [Fact]
    public async Task A_request_queued_behind_a_slow_one_has_its_answer_within_the_apps_wait_of_being_queued()
    {
        _h.Clock.SetUtcNow(Local(24, 0, 30));                                  // before tonight's minute: the worker's own run sends nothing
        await _h.Consent(false, false, true);
        _h.Outbox.InsertMinutes([SharingFakes.Minute(600, "2026-09-23")]);
        var uploading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Client.AnswerAsync = async (call, cancel) =>
        {
            if (call.Kind == "report")
            {
                uploading.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancel);
            }
            return new SendOutcome.Accepted();
        };

        await _h.Running(async () =>
        {
            var send = new SendNowCommand(2);
            _h.Commands.TryQueue(send).ShouldBeTrue();
            await uploading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var delete = new DeleteMyDataCommand(3);
            _h.Commands.TryQueue(delete).ShouldBeTrue();                       // while Send now's upload is under way

            _h.Clock.Advance(SharingWorker.AppWait);                           // the upload takes all the time the App waits

            (await send.Reply.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(
                new SharingReply(2, false, "Couldn't send: the server didn't answer in time. Will try again."));
            (await delete.Reply.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(
                new SharingReply(3, false, "Couldn't delete your data: the service was busy with another request. Nothing was changed."));
        });

        _h.Client.Calls.ShouldNotContain(call => call.Kind == "delete");
        _h.Store.InstallId.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_clock_set_while_a_request_waits_neither_spends_nor_stretches_the_apps_wait()
    {
        _h.Clock.SetUtcNow(Local(24, 10));
        await _h.Consent(true, true, true);
        var delete = new DeleteMyDataCommand(2);
        _h.Commands.TryQueue(delete).ShouldBeTrue();
        _h.Wall.Step = TimeSpan.FromHours(1);                                  // Windows sets the clock an hour on

        await _h.TakeAndRunAsync();

        (await delete.Reply).ShouldBe(new SharingReply(2, true, "Your data has been deleted from the server."));

        await _h.Consent(true, true, true);
        _h.Client.AnswerAsync = async (_, cancel) =>
        {
            await Task.Delay(Timeout.Infinite, cancel);                        // the server never answers
            return new SendOutcome.Accepted();
        };
        var late = new DeleteMyDataCommand(3);
        _h.Commands.TryQueue(late).ShouldBeTrue();
        _h.Clock.Advance(TimeSpan.FromSeconds(7));                             // seven seconds behind another request
        _h.Wall.Step -= TimeSpan.FromHours(2);                                 // and the clock set back meanwhile

        var handling = _h.TakeAndRunAsync();
        _h.Clock.Advance(TimeSpan.FromSeconds(1));                             // the App's eight seconds are up

        (await late.Reply.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(
            new SharingReply(3, false, "Couldn't delete your data: the server didn't answer in time. Nothing was changed."));
        await handling;
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

    /// <summary>
    /// Has the inbox say it holds a request only <paramref name="lag"/> after it does, as when the pool thread that says so
    /// runs late on a busy PC. The inbox's channel is its own, so it is swapped in by reflection.
    /// </summary>
    private static void WakeLate(SharingCommands commands, TimeSpan lag)
    {
        var channel = typeof(SharingCommands).GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance).ShouldNotBeNull();
        channel.SetValue(commands, new LateChannel((Channel<SharingCommand>)channel.GetValue(commands)!, lag));
    }

    /// <summary>What the status says of a consent change the server can't be told of, as the key for <paramref name="id"/>
    /// can't be read.</summary>
    private static string Unheard(string id) =>
        $"this PC's key can't be read, so the server wasn't told of your choices for install ID {id}. To have them applied to what "
        + "was sent under it, write to the address in the privacy policy, quoting that ID";

    /// <summary>UTC-4, where a laptop from the harness's UTC+2 starts up after travelling west.</summary>
    private static readonly TimeZoneInfo West = TimeZoneInfo.CreateCustomTimeZone("PL-4", TimeSpan.FromHours(-4), "PL-4", "PL-4");

    /// <summary>Each minute's UTC start as a reader of the report works it out: the day's midnight, less the header's offset,
    /// plus the minute's index.</summary>
    private static DateTimeOffset[] Decoded(ReportV1 report)
    {
        var midnight = new DateTimeOffset(LocalDays.Parse(report.Day).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return [.. report.Power.ShouldNotBeNull().Minutes.T.Select(index => midnight.AddMinutes(index - report.UtcOffsetMinutes))];
    }

    /// <summary>The UTC starts of <paramref name="count"/> minutes from <paramref name="from"/>.</summary>
    private static DateTimeOffset[] Starts(DateTimeOffset from, int count) =>
        [.. Enumerable.Range(0, count).Select(i => from.AddMinutes(i).ToUniversalTime())];

    /// <summary>The worker wired to a temp database, a fake server, a fake clock in a UTC+2 zone and a board the loop would
    /// have published to; its ticks and commands are run by the test, one at a time. The worker and its inbox see the clock
    /// through <see cref="Wall"/>, whose time can be set apart from its timestamps and timers.</summary>
    private sealed class Harness : IDisposable
    {
        private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("PL+2", TimeSpan.FromHours(2), "PL+2", "PL+2");

        private int _runsStartedWhileTheAppWaited;

        public Harness()
        {
            Wall = new SetClock(Clock);
            Commands = new SharingCommands(Wall);
            Clock.SetLocalTimeZone(Zone);
            Board.Publish(SharingFakes.Settings);
            Board.Publish(SharingFakes.Status());
            Board.Publish(SharingFakes.Facts);
            Board.PublishDiscreteGpu(true);
            Directory.CreateDirectory(Sent);
            Directory.CreateDirectory(Crashes);
            var environment = new SharingEnvironment(Sent, Crashes, Names, () => SharingFakes.Host, () => SendMinute);
            Worker = new SharingWorker(Database.Db, Board, Commands, Client, environment, Wall, NullLogger<SharingWorker>.Instance);
        }

        public TestDatabase Database { get; } = new();

        public FakeTimeProvider Clock { get; } = new(Local(24, 0));

        public SetClock Wall { get; }

        public StatusBoard Board { get; } = new();

        public SharingCommands Commands { get; }

        public FakeSharingClient Client { get; } = new();

        public SharingWorker Worker { get; }

        /// <summary>How many runs have started while a request the App waits on was queued: a run asks for the names to scrub
        /// first thing. (A request's handling asks after taking it, and a run's upload before it goes.)</summary>
        public int RunsStartedWhileTheAppWaited => Volatile.Read(ref _runsStartedWhileTheAppWaited);

        public string Sent => Path.Combine(Database.Folder, "Sent");

        public string Crashes => Path.Combine(Database.Folder, "Crashes");

        public SharingStore Store => new(new SettingsRepository(Database.Db));

        public OutboxRepository Outbox => new(Database.Db);

        public Task TickAsync() => Worker.TickAsync(CancellationToken.None);

        /// <summary>Starts the worker, which runs at once, lets <paramref name="test"/> talk to it through its inbox, and stops it.</summary>
        public async Task Running(Func<Task> test)
        {
            await Worker.StartAsync(CancellationToken.None);
            try
            {
                await test();
            }
            finally
            {
                await Worker.StopAsync(CancellationToken.None);
            }
        }

        /// <summary>No day before today waits: the run has sent them all.</summary>
        public bool AllSent() => Outbox.Days().All(day => string.CompareOrdinal(day, LocalDays.Text(LocalDays.Of(Clock.GetUtcNow(), Zone))) >= 0);

        public async Task<SharingReply> Run(SharingCommand command)
        {
            await Worker.HandleAsync(command, CancellationToken.None);
            return await command.Reply;
        }

        /// <summary>Takes the next request from the inbox and carries it out, as the running worker does.</summary>
        public Task TakeAndRunAsync()
        {
            Commands.TryTake(out var command).ShouldBeTrue();
            return Worker.HandleAsync(command, CancellationToken.None);
        }

        public Task<SharingReply> Consent(bool diagnostics, bool usage, bool power, bool share = false) =>
            Run(new SetConsentCommand(1, new Consent(ConsentText.Version, diagnostics, usage, power, share)));

        /// <summary>A reading a second from <paramref name="from"/> for <paramref name="span"/>, as the loop writes them.</summary>
        public void Readings(DateTimeOffset from, TimeSpan span) =>
            new RawSampleRepository(Database.Db).InsertBatch([.. Enumerable.Range(0, (int)span.TotalSeconds).Select(s => PowerLedger.Service.Tests.Readings.At(from.AddSeconds(s)))]);

        /// <summary>A crash file as the service writes one when it crashes.</summary>
        public void CrashFile(DateTimeOffset at, string message = "Boom.") =>
            ServiceCrashes.TryWrite(Crashes, new InvalidOperationException(message), at, "0.6.0").ShouldNotBeNull();

        private ScrubNames Names()
        {
            if (Commands.AppWaiting) Interlocked.Increment(ref _runsStartedWhileTheAppWaited);
            return new ScrubNames("alice", "DESKTOP-TEST", "CONTOSO");
        }

        public void Dispose() => Database.Dispose();
    }
}

/// <summary>A data server that answers as a test says, keeping every request.</summary>
internal sealed class FakeSharingClient : ISharingClient
{
    private readonly List<SharingCall> _calls = [];

    /// <summary>A copy of the requests so far, safe to read while the worker runs.</summary>
    public List<SharingCall> Calls
    {
        get
        {
            lock (_calls) return [.. _calls];
        }
    }

    public Func<SharingCall, SendOutcome> Answer { get; set; } = _ => new SendOutcome.Accepted();

    /// <summary>When set, answers in its own time instead of <see cref="Answer"/>, given the request's token.</summary>
    public Func<SharingCall, CancellationToken, Task<SendOutcome>>? AnswerAsync { get; set; }

    public IEnumerable<ReportV1> Reports => Calls.Where(call => call.Report is not null).Select(call => call.Report!);

    public Task<SendOutcome> SendReportAsync(byte[] gzipBody, string key, CancellationToken cancel = default) =>
        Record(new SharingCall("report", key, null, null, ReportJson.Read(Gunzip(gzipBody)), gzipBody), cancel);

    public Task<SendOutcome> SendConsentAsync(string installId, string key, Consent consent, CancellationToken cancel = default) =>
        Record(new SharingCall("consent", key, installId, consent, null, []), cancel);

    public Task<SendOutcome> DeleteAsync(string installId, string key, CancellationToken cancel = default) =>
        Record(new SharingCall("delete", key, installId, null, null, []), cancel);

    private Task<SendOutcome> Record(SharingCall call, CancellationToken cancel)
    {
        lock (_calls) _calls.Add(call);
        return AnswerAsync is { } later ? later(call, cancel) : Task.FromResult(Answer(call));
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

/// <summary>A fake clock whose time can be set by <see cref="Step"/>, as Windows sets the PC's clock, while its timestamps
/// and timers go on as before, as the system's do. (<see cref="FakeTimeProvider"/>'s timestamps follow its time.)</summary>
internal sealed class SetClock(FakeTimeProvider inner) : TimeProvider
{
    public TimeSpan Step { get; set; }

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Step;

    public override long GetTimestamp() => inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(callback, state, dueTime, period);
}

/// <summary>A channel whose reader says it has something to read only a set time after it has.</summary>
internal sealed class LateChannel : Channel<SharingCommand>
{
    public LateChannel(Channel<SharingCommand> inner, TimeSpan lag)
    {
        Reader = new LateReader(inner.Reader, lag);
        Writer = inner.Writer;
    }

    private sealed class LateReader(ChannelReader<SharingCommand> inner, TimeSpan lag) : ChannelReader<SharingCommand>
    {
        public override bool TryRead([MaybeNullWhen(false)] out SharingCommand item) => inner.TryRead(out item);

        public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancel = default)
        {
            var more = await inner.WaitToReadAsync(cancel).ConfigureAwait(false);
            await Task.Delay(lag, cancel).ConfigureAwait(false);
            return more;
        }
    }
}
