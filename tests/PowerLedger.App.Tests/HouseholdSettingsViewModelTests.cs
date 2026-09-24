using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class HouseholdSettingsViewModelTests
{
    private readonly FakeLink _link = new();

    private HouseholdSettingsViewModel Model() => new(_link, UiThreads.Inline);

    private static HouseholdStatus Status(bool discoverable) => new("hh1", "aaaa", "This-PC", ChassisKind.Desktop, discoverable, [], null);

    [Fact]
    public void It_starts_on_by_default()
    {
        Model().Discoverable.ShouldBeTrue();
    }

    [Fact]
    public void Apply_follows_the_services_discoverable_state()
    {
        var model = Model();
        model.Apply(Status(discoverable: false));
        model.Discoverable.ShouldBeFalse();

        model.Apply(Status(discoverable: true));
        model.Discoverable.ShouldBeTrue();
    }

    [Fact]
    public void Apply_with_no_household_leaves_the_tick_as_it_was()
    {
        var model = Model();
        model.Apply(Status(discoverable: false));

        model.Apply(null);

        model.Discoverable.ShouldBeFalse();
    }

    [Fact]
    public void Ticking_it_sends_the_change_at_once()
    {
        _link.Connect(true);
        var model = Model();

        model.Discoverable = false;

        _link.HouseholdRequests.Single().ShouldBe(("discoverable", false));
        model.Discoverable.ShouldBeFalse();
    }

    [Fact]
    public void A_refusal_puts_the_tick_back_and_shows_why()
    {
        _link.Connect(true);
        _link.HouseholdAnswer = new HouseholdOutcome(false, "The service isn't running, so nothing was changed.");
        var model = Model();

        model.Discoverable = false;

        model.Discoverable.ShouldBeTrue();
        model.Message.ShouldBe("The service isn't running, so nothing was changed.");
    }

    [Fact]
    public async Task A_tick_still_on_its_way_is_not_overwritten_by_a_status_refresh()
    {
        _link.Connect(true);
        _link.HouseholdGate = new TaskCompletionSource<HouseholdOutcome>();
        var model = Model();

        model.Discoverable = false;
        model.Busy.ShouldBeTrue();
        model.Apply(Status(discoverable: true));           // a stale read arrives while the tick is still in flight
        model.Discoverable.ShouldBeFalse();                 // the optimistic value holds

        _link.HouseholdGate.SetResult(new HouseholdOutcome(true, "Done."));
        await model.Pending;
        model.Busy.ShouldBeFalse();
    }
}
