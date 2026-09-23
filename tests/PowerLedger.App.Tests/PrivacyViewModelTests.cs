using System.Globalization;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class PrivacyViewModelTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeLink _link = new();
    private readonly List<string> _opened = [];
    private readonly List<Uri> _pages = [];
    private readonly List<string> _copied = [];

    private PrivacyViewModel Model()
    {
        _link.Connect(true);
        return new PrivacyViewModel(_link, UiThreads.Inline, TimeZoneInfo.Utc, English, () => _opened.Add("sent"), _pages.Add, _copied.Add);
    }

    [Theory]
    [InlineData(0, false, false, false, false, null, false, null, null, 0, "You haven't chosen yet.")]
    [InlineData(1, false, false, false, false, null, false, null, null, 0, "Nothing is sent.")]
    [InlineData(1, true, false, false, false, null, false, null, null, 0, "Nothing sent yet.")]
    [InlineData(1, true, false, false, false, null, false, null, null, 3, "Nothing sent yet. · 3 days waiting")]
    [InlineData(1, true, false, false, false, "the server is unreachable", false, null, null, 0, "Couldn't send: the server is unreachable. Will try again.")]
    [InlineData(1, true, false, false, false, "the day was too old", true, null, null, 0, "Rejected by the server: the day was too old")]
    public void The_status_line_follows_where_sharing_stands(
        int version, bool diagnostics, bool usage, bool power, bool share, string? problem, bool rejected,
        int? lastSentDay, long? lastSentBytes, int daysWaiting, string expected)
    {
        var consent = new Consent(version, diagnostics, usage, power, share);
        var lastSentAt = lastSentDay is { } day ? new DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero) : (DateTimeOffset?)null;
        var sharing = new SharingStatus(consent, null, lastSentAt, lastSentBytes, problem, rejected, daysWaiting);

        PrivacyViewModel.StatusLine(sharing, TimeZoneInfo.Utc, English).ShouldBe(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_problem_that_lasts_is_shown_as_it_is_whatever_the_switches(bool anyOn)
    {
        var sharing = new SharingStatus(new Consent(ConsentText.Version, anyOn, false, false, false), null, null, null,
            "this PC's key can't be read, so the server wasn't told of your choices", false, 0, ProblemLasts: true);

        PrivacyViewModel.StatusLine(sharing, TimeZoneInfo.Utc, English)
            .ShouldBe("This PC's key can't be read, so the server wasn't told of your choices.");
    }

    [Fact]
    public void A_last_sent_time_is_shown_in_the_users_culture_with_its_size_rounded_up()
    {
        var sharing = new SharingStatus(
            new Consent(ConsentText.Version, true, false, false, false), null,
            new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero), 41_000, null, false, 0);

        PrivacyViewModel.StatusLine(sharing, TimeZoneInfo.Utc, English).ShouldBe("Last sent 24 Sep 2026 · 41 KB");
    }

    [Fact]
    public void Install_id_shows_none_yet_before_one_exists()
    {
        var model = Model();
        model.Apply(Statuses.WithSharing(Consent.Unanswered).Sharing);
        model.InstallId.ShouldBe("None yet");

        model.Apply(Statuses.WithSharing(Consent.Unanswered, installId: "abc-123").Sharing);
        model.InstallId.ShouldBe("abc-123");
    }

    [Fact]
    public void An_answer_to_an_older_wording_shows_as_all_off_and_a_tick_sends_fresh_not_the_old_switches()
    {
        // Consent.Answered is false for any version but the current one, whatever was ticked under that older wording.
        var model = Model();
        model.Apply(Statuses.WithSharing(new Consent(ConsentText.Version - 1, true, true, true, true)).Sharing);

        model.Diagnostics.ShouldBeFalse();
        model.Usage.ShouldBeFalse();
        model.Power.ShouldBeFalse();
        model.Share.ShouldBeFalse();

        model.Diagnostics = true;

        _link.SharingRequests.Single().ShouldBe(new Consent(ConsentText.Version, true, false, false, false));
    }

    [Fact]
    public void A_tick_sends_the_whole_consent_at_once()
    {
        var model = Model();
        model.Apply(Statuses.WithSharing(Consent.Unanswered).Sharing);

        model.Diagnostics = true;

        _link.SharingRequests.Single().ShouldBe(new Consent(ConsentText.Version, true, false, false, false));
        model.Diagnostics.ShouldBeTrue();
    }

    [Fact]
    public void Turning_power_off_clears_share_in_the_same_request()
    {
        var model = Model();
        model.Apply(Statuses.WithSharing(new Consent(ConsentText.Version, false, false, true, true)).Sharing);
        model.Power.ShouldBeTrue();
        model.Share.ShouldBeTrue();

        model.Power = false;

        _link.SharingRequests.Single().ShouldBe(new Consent(ConsentText.Version, false, false, false, false));
        model.Power.ShouldBeFalse();
        model.Share.ShouldBeFalse();
    }

    [Fact]
    public void A_refusal_shows_the_message_and_the_tick_reverts()
    {
        _link.SharingAnswer = new SharingOutcome(false, "Sharing detailed data needs Hardware and power turned on.");
        var model = Model();
        model.Apply(Statuses.WithSharing(Consent.Unanswered).Sharing);

        model.Share = true;

        model.Share.ShouldBeFalse();
        model.Message.ShouldBe("Sharing detailed data needs Hardware and power turned on.");
    }

    [Fact]
    public void A_status_refresh_does_not_undo_a_tick_in_flight()
    {
        var gate = new TaskCompletionSource<SharingOutcome>();
        _link.SharingGate = gate;
        var model = Model();
        model.Apply(Statuses.WithSharing(Consent.Unanswered).Sharing);

        model.Diagnostics = true;   // sent, but not yet answered
        model.Apply(Statuses.WithSharing(Consent.Unanswered).Sharing);   // a refresh arrives, still showing the old consent

        model.Diagnostics.ShouldBeTrue();   // not undone

        gate.SetResult(new SharingOutcome(true, "Saved."));
        model.Diagnostics.ShouldBeTrue();
    }

    [Fact]
    public void A_successful_tick_reports_the_consent_that_was_applied()
    {
        var model = Model();
        model.Apply(Statuses.WithSharing(Consent.Unanswered).Sharing);
        Consent? applied = null;
        model.Applied += c => applied = c;

        model.Diagnostics = true;

        applied.ShouldBe(new Consent(ConsentText.Version, true, false, false, false));
    }

    [Fact]
    public void A_refusal_reports_nothing_applied()
    {
        _link.SharingAnswer = new SharingOutcome(false, "Sharing detailed data needs Hardware and power turned on.");
        var model = Model();
        model.Apply(Statuses.WithSharing(Consent.Unanswered).Sharing);
        var applied = false;
        model.Applied += _ => applied = true;

        model.Share = true;   // needs Power too, so the service refuses it

        applied.ShouldBeFalse();
    }

    [Fact]
    public void Delete_asks_first_and_sends_only_on_delete()
    {
        var model = Model();
        model.Apply(Statuses.WithSharing(new Consent(ConsentText.Version, true, true, true, true)).Sharing);

        model.DeleteMyData.Execute(null);
        model.ConfirmingDelete.ShouldBeTrue();
        _link.SharingRequests.ShouldBeEmpty();

        model.ConfirmDelete.Execute(null);

        _link.SharingRequests.Single().ShouldBe("delete");
        model.ConfirmingDelete.ShouldBeFalse();
    }

    [Fact]
    public void Cancelling_delete_sends_nothing()
    {
        var model = Model();
        model.DeleteMyData.Execute(null);
        model.CancelDelete.Execute(null);

        model.ConfirmingDelete.ShouldBeFalse();
        _link.SharingRequests.ShouldBeEmpty();
    }

    /// <summary>The service can take seconds over a delete (finding 6: sharing requests are serialised, so a second one
    /// waits its turn); Delete must show that and refuse a second press meanwhile.</summary>
    [Fact]
    public async Task Confirm_delete_is_busy_while_on_its_way_to_the_service()
    {
        var gate = new TaskCompletionSource<SharingOutcome>();
        _link.SharingGate = gate;
        var model = Model();
        model.Apply(Statuses.WithSharing(new Consent(ConsentText.Version, true, true, true, true)).Sharing);
        model.DeleteMyData.Execute(null);

        model.ConfirmDelete.Execute(null);

        model.Busy.ShouldBeTrue();
        model.ConfirmDelete.CanExecute(null).ShouldBeFalse();

        gate.SetResult(new SharingOutcome(true, "Your data has been deleted from the server."));

        await WaitFor.True(() => !model.Busy);
        model.ConfirmDelete.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void The_buttons_open_sent_the_browser_and_the_clipboard()
    {
        var model = Model();
        model.Apply(Statuses.WithSharing(Consent.Unanswered, installId: "abc-123").Sharing);

        model.OpenSent.Execute(null);
        model.OpenPrivacyPolicy.Execute(null);
        model.CopyInstallId.Execute(null);

        _opened.ShouldBe(new[] { "sent" });
        _pages.ShouldBe(new[] { ConsentViewModel.PrivacyPolicyUri });
        _copied.ShouldBe(new[] { "abc-123" });
    }
}
