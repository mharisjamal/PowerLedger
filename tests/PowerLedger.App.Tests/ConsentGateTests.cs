using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ConsentGateTests
{
    private readonly FakeLink _link = new();
    private readonly List<Consent> _opened = [];
    private readonly FakeTimeProvider _clock = new();

    private ConsentGate Gate() => new(_link, UiThreads.Inline, _clock, _opened.Add);

    [Fact]
    public void It_opens_when_the_consent_is_unanswered()
    {
        _link.Status = Statuses.WithSharing(Consent.Unanswered);
        _link.Connect(true);

        Gate().CheckOnce();

        _opened.ShouldBe(new[] { Consent.Unanswered });
    }

    [Fact]
    public void It_does_not_open_when_already_answered()
    {
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, true, false, false, false));
        _link.Connect(true);

        Gate().CheckOnce();

        _opened.ShouldBeEmpty();
    }

    [Fact]
    public void It_does_not_open_against_an_older_service_that_sends_no_sharing_status()
    {
        _link.Status = Statuses.Running();   // Sharing is null: an older service
        _link.Connect(true);

        Gate().CheckOnce();

        _opened.ShouldBeEmpty();
    }

    [Fact]
    public void It_checks_at_most_once_a_session()
    {
        _link.Status = Statuses.WithSharing(Consent.Unanswered);
        _link.Connect(true);
        var gate = Gate();

        gate.CheckOnce();
        _link.StatusReads.ShouldBe(1);
        gate.CheckOnce();

        _link.StatusReads.ShouldBe(1);
        _opened.Count.ShouldBe(1);
    }

    /// <summary>After an update the installer starts the service and opens the App at once, so the first check can land
    /// before the pipe has even connected. That must not use up the session's one check.</summary>
    [Fact]
    public void It_retries_once_the_link_connects_after_being_unavailable_at_the_first_check()
    {
        _link.Status = Statuses.WithSharing(Consent.Unanswered);
        // not connected yet: GetStatusAsync answers null until Connect
        var gate = Gate();

        gate.CheckOnce();
        _opened.ShouldBeEmpty();

        _link.Connect(true);   // the pipe connects a moment later

        _opened.ShouldBe(new[] { Consent.Unanswered });
    }

    /// <summary>A status with no Sharing while already connected — the service still starting — must retry too, ten
    /// seconds later, not just on the next connection.</summary>
    [Fact]
    public void It_retries_every_ten_seconds_while_the_service_has_not_published_sharing_yet()
    {
        _link.Status = Statuses.Running();   // connected, but no Sharing yet
        _link.Connect(true);
        var gate = Gate();

        gate.CheckOnce();
        _opened.ShouldBeEmpty();

        _link.Status = Statuses.WithSharing(Consent.Unanswered);   // the service finishes starting
        _clock.Advance(ConsentGate.RetryEvery);

        _opened.ShouldBe(new[] { Consent.Unanswered });
    }

    /// <summary>A timer tick and a connection event can both start a run while a status read is still on its way; without
    /// an atomic claim on "done", both would come back unanswered and both open the dialog, the second showing all off
    /// and overwriting the first answer. Once the gated read completes, the two runs' continuations are not guaranteed to
    /// finish on the calling thread or within the same instant (one can be resumed on a different thread-pool thread), so
    /// this waits in real time for whichever run wins rather than asserting immediately.</summary>
    [Fact]
    public async Task Overlapping_runs_open_the_dialog_only_once()
    {
        _link.Status = Statuses.WithSharing(Consent.Unanswered);
        var statusGate = new TaskCompletionSource<ServiceStatus?>();
        _link.StatusGate = statusGate;
        _link.Connect(true);
        var gate = Gate();

        gate.CheckOnce();                           // the first run: awaiting the gated status read
        _link.StatusReads.ShouldBe(1);
        _clock.Advance(ConsentGate.RetryEvery);      // the periodic timer fires a second, overlapping run
        _link.StatusReads.ShouldBe(2);

        statusGate.SetResult(_link.Status);          // both runs' status reads complete together

        await WaitFor.True(() => _opened.Count > 0);
        _opened.Count.ShouldBe(1);
    }

    /// <summary>The App can exit while a run is still awaiting the service's answer; that run must not open a dialog
    /// afterwards, owned by a window that may already be gone. The real delay gives a broken fix every chance to open it
    /// anyway before this checks.</summary>
    [Fact]
    public async Task A_run_already_in_flight_does_not_open_after_dispose()
    {
        _link.Status = Statuses.WithSharing(Consent.Unanswered);
        var statusGate = new TaskCompletionSource<ServiceStatus?>();
        _link.StatusGate = statusGate;
        _link.Connect(true);
        var gate = Gate();

        gate.CheckOnce();   // the run started, awaiting the gated status read
        gate.Dispose();     // the App is exiting before the service answered

        statusGate.SetResult(_link.Status);

        await Task.Delay(200);
        _opened.ShouldBeEmpty();
    }

    [Fact]
    public void A_second_check_once_done_reads_the_status_no_further_times()
    {
        _link.Status = Statuses.Running();
        _link.Connect(true);
        var gate = Gate();
        gate.CheckOnce();
        _link.Status = Statuses.WithSharing(Consent.Unanswered);
        _clock.Advance(ConsentGate.RetryEvery);
        _link.StatusReads.ShouldBe(2);

        gate.CheckOnce();

        _link.StatusReads.ShouldBe(2);
    }
}
