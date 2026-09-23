using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ConsentViewModelTests
{
    private readonly FakeLink _link = new();
    private readonly List<Uri> _opened = [];
    private readonly List<string> _previews = [];

    private ConsentViewModel Model(Consent? current = null)
    {
        _link.Connect(true);
        return new ConsentViewModel(_link, UiThreads.Inline, current ?? Consent.Unanswered, _opened.Add, _previews.Add);
    }

    [Fact]
    public void Every_switch_is_off_to_begin_with()
    {
        var model = Model();

        model.Diagnostics.ShouldBeFalse();
        model.Usage.ShouldBeFalse();
        model.Power.ShouldBeFalse();
        model.Share.ShouldBeFalse();
        model.CanShare.ShouldBeFalse();
    }

    [Fact]
    public void An_answer_to_an_older_wording_of_the_choices_shows_as_all_off()
    {
        // Consent.Answered is false for any version but the current one, whatever was ticked under that older wording.
        var model = Model(new Consent(ConsentText.Version - 1, true, true, true, true));

        model.Diagnostics.ShouldBeFalse();
        model.Usage.ShouldBeFalse();
        model.Power.ShouldBeFalse();
        model.Share.ShouldBeFalse();
        model.CanShare.ShouldBeFalse();
    }

    [Fact]
    public void Share_is_greyed_until_power_and_turning_power_off_clears_it()
    {
        var model = Model();

        model.Power = true;
        model.CanShare.ShouldBeTrue();
        model.Share = true;

        model.Power = false;
        model.CanShare.ShouldBeFalse();
        model.Share.ShouldBeFalse();
    }

    [Fact]
    public void Allow_all_sends_every_switch_on_at_the_current_version()
    {
        var model = Model();

        model.AllowAll.Execute(null);

        _link.SharingRequests.Single().ShouldBe(new Consent(ConsentText.Version, true, true, true, true));
    }

    [Fact]
    public void Allow_none_sends_every_switch_off()
    {
        var model = Model(new Consent(ConsentText.Version, true, true, true, true));

        model.AllowNone.Execute(null);

        _link.SharingRequests.Single().ShouldBe(Consent.Unanswered with { Version = ConsentText.Version });
    }

    [Fact]
    public void Save_sends_what_is_ticked()
    {
        var model = Model();
        model.Diagnostics = true;
        model.Power = true;

        model.Save.Execute(null);

        _link.SharingRequests.Single().ShouldBe(new Consent(ConsentText.Version, true, false, true, false));
    }

    [Fact]
    public async Task An_error_keeps_the_dialog_open()
    {
        _link.SharingAnswer = new SharingOutcome(false, "Sharing detailed data needs Hardware and power turned on.");
        var model = Model();
        var closed = false;
        model.Closed += () => closed = true;

        await model.SendAsync(new Consent(ConsentText.Version, false, false, false, true));

        closed.ShouldBeFalse();
        model.Message.ShouldBe("Sharing detailed data needs Hardware and power turned on.");
    }

    [Fact]
    public async Task Success_closes_the_dialog()
    {
        var model = Model();
        var closed = false;
        model.Closed += () => closed = true;

        await model.SendAsync(new Consent(ConsentText.Version, true, true, true, true));

        closed.ShouldBeTrue();
    }

    [Fact]
    public async Task Success_reports_the_consent_that_was_applied()
    {
        var model = Model();
        Consent? applied = null;
        model.Applied += c => applied = c;

        await model.SendAsync(new Consent(ConsentText.Version, true, true, true, true));

        applied.ShouldBe(new Consent(ConsentText.Version, true, true, true, true));
    }

    [Fact]
    public async Task A_refusal_reports_nothing_applied()
    {
        _link.SharingAnswer = new SharingOutcome(false, "Sharing detailed data needs Hardware and power turned on.");
        var model = Model();
        var applied = false;
        model.Applied += _ => applied = true;

        await model.SendAsync(new Consent(ConsentText.Version, false, false, false, true));

        applied.ShouldBeFalse();
    }

    [Fact]
    public void Closing_without_a_command_sends_nothing()
    {
        Model();
        _link.SharingRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task See_what_would_be_sent_opens_the_previewed_file()
    {
        _link.SharingAnswer = new SharingOutcome(true, "Written.", @"C:\ProgramData\PowerLedger\Sent\preview.json");
        var model = Model();

        await model.PreviewAsync();

        _previews.ShouldBe(new[] { @"C:\ProgramData\PowerLedger\Sent\preview.json" });
    }

    [Fact]
    public void Open_privacy_policy_opens_the_page_in_the_browser()
    {
        var model = Model();
        model.OpenPrivacyPolicy.Execute(null);

        _opened.ShouldBe(new[] { ConsentViewModel.PrivacyPolicyUri });
    }

    /// <summary>The service can take seconds over a choice (finding 6: sharing requests are serialised, so a second one
    /// waits its turn); the buttons that would start another must show that and refuse a second press meanwhile. The
    /// completion is awaited in real time rather than assumed synchronous with <c>SetResult</c>, since a continuation is
    /// not guaranteed to run inline on the thread that completed the gate.</summary>
    [Fact]
    public async Task The_buttons_are_busy_while_a_choice_is_still_on_its_way_to_the_service()
    {
        var gate = new TaskCompletionSource<SharingOutcome>();
        _link.SharingGate = gate;
        var model = Model();

        model.Save.Execute(null);

        model.Busy.ShouldBeTrue();
        model.Save.CanExecute(null).ShouldBeFalse();
        model.AllowAll.CanExecute(null).ShouldBeFalse();
        model.AllowNone.CanExecute(null).ShouldBeFalse();
        model.SeeWhatWouldBeSent.CanExecute(null).ShouldBeFalse();

        gate.SetResult(new SharingOutcome(true, "Saved."));

        await WaitFor.True(() => !model.Busy);
        model.Save.CanExecute(null).ShouldBeTrue();
    }
}
