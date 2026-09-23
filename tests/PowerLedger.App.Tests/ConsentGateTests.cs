using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ConsentGateTests
{
    private readonly FakeLink _link = new();
    private readonly List<Consent> _opened = [];

    private ConsentGate Gate() => new(_link, UiThreads.Inline, _opened.Add);

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
}
