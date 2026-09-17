using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>
/// How the source treats the device around the reading itself: the owner's tick, the maker's own program, how often it
/// asks, how long an answer counts for, and what it does when things go wrong.
/// </summary>
public class PsuSourceTests
{
    [Fact]
    public void A_supply_that_answers_fills_the_watts_and_the_name()
    {
        var device = FakePsus.Corsair(watts: 140);
        using var source = new PsuSource(new FakeHidPort(device), static () => true, static () => [], static () => TimeSpan.Zero);
        var draft = new SampleDraft();

        source.Contribute(draft);

        draft.PsuWallW.ShouldBe(140);
        draft.PsuName.ShouldBe("Corsair RM1000i");
        source.Supported.ShouldBeTrue();
        source.Unavailable.ShouldBeNull();
    }

    [Fact]
    public void A_machine_with_no_supply_keeps_looking_for_a_minute_and_then_says_it_has_none()
    {
        var now = TimeSpan.Zero;
        var device = FakePsus.Corsair(watts: 140);
        device.Present = false;
        var port = new FakeHidPort(device);
        using var source = new PsuSource(port, static () => true, static () => [], () => now);

        source.Supported.ShouldBeTrue();
        source.Unavailable.ShouldBe("looking for a power supply on USB");
        source.Contribute(new SampleDraft());

        now += TimeSpan.FromSeconds(61);
        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.PsuName.ShouldBeNull();
        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no Corsair HXi or RMi, NZXT E series or Thermaltake DPS G power supply on USB");
    }

    [Fact]
    public void A_supply_windows_lists_only_once_the_machine_has_settled_is_still_found()
    {
        var now = TimeSpan.Zero;
        var device = FakePsus.Corsair(watts: 140);
        device.Present = false;
        using var source = new PsuSource(new FakeHidPort(device), static () => true, static () => [], () => now);
        source.Contribute(new SampleDraft());

        device.Present = true;
        now += TimeSpan.FromSeconds(11);
        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.PsuWallW.ShouldBe(140);
    }

    [Fact]
    public void The_tick_turned_off_sends_nothing_to_the_device_and_still_names_it()
    {
        var reading = true;
        var now = TimeSpan.Zero;
        var device = FakePsus.Corsair(watts: 140);
        using var source = new PsuSource(new FakeHidPort(device), () => reading, static () => [], () => now);
        source.Contribute(new SampleDraft());
        var written = device.Written.Count;

        reading = false;
        now += TimeSpan.FromSeconds(3);
        var draft = new SampleDraft();
        source.Contribute(draft);

        device.Written.Count.ShouldBe(written);
        device.Closes.ShouldBe(1);                       // the handle is given up, so the maker's program has it to itself
        draft.PsuWallW.ShouldBeNull();
        draft.PsuName.ShouldBe("Corsair RM1000i");
        source.Unavailable.ShouldBe("reading the Corsair RM1000i is turned off");
    }

    [Fact]
    public void A_tick_that_was_never_turned_on_leaves_the_device_alone_altogether()
    {
        var device = FakePsus.Corsair(watts: 140);
        var port = new FakeHidPort(device);
        using var source = new PsuSource(port, static () => false, static () => [], static () => TimeSpan.Zero);
        var draft = new SampleDraft();

        source.Contribute(draft);

        device.Written.ShouldBeEmpty();
        device.Opens.ShouldBe(0);
        port.Opens.ShouldBe(0);
        draft.PsuName.ShouldBe("Corsair RM1000i");
    }

    [Fact]
    public void A_supply_is_left_alone_while_its_makers_program_is_running()
    {
        var programs = new List<string> { "explorer", "iCUE" };
        var now = TimeSpan.Zero;
        var device = FakePsus.Corsair(watts: 140);
        using var source = new PsuSource(new FakeHidPort(device), static () => true, () => programs, () => now);

        var draft = new SampleDraft();
        source.Contribute(draft);

        device.Written.ShouldBeEmpty();
        draft.PsuWallW.ShouldBeNull();
        draft.PsuName.ShouldBe("Corsair RM1000i");
        source.Unavailable.ShouldBe("the Corsair RM1000i is left to iCUE, which is running");

        programs.Remove("iCUE");
        now += TimeSpan.FromSeconds(2);
        draft = new SampleDraft();
        source.Contribute(draft);

        draft.PsuWallW.ShouldBe(140);
        source.Unavailable.ShouldBeNull();
    }

    [Fact]
    public void Another_makers_program_is_no_reason_to_leave_a_supply_alone()
    {
        var device = FakePsus.Corsair(watts: 140);
        using var source = new PsuSource(
            new FakeHidPort(device), static () => true, static () => ["NZXT CAM", "TT RGB Plus"], static () => TimeSpan.Zero);
        var draft = new SampleDraft();

        source.Contribute(draft);

        draft.PsuWallW.ShouldBe(140);
    }

    [Fact]
    public void The_device_is_asked_at_most_every_two_seconds_and_the_last_answer_stands_between_times()
    {
        var now = TimeSpan.Zero;
        var device = FakePsus.Corsair(watts: 140);
        using var source = new PsuSource(new FakeHidPort(device), static () => true, static () => [], () => now);
        source.Contribute(new SampleDraft());
        var written = device.Written.Count;

        now += TimeSpan.FromSeconds(1);
        var draft = new SampleDraft();
        source.Contribute(draft);

        device.Written.Count.ShouldBe(written);
        draft.PsuWallW.ShouldBe(140);

        now += TimeSpan.FromSeconds(1);
        source.Contribute(new SampleDraft());

        device.Written.Count.ShouldBeGreaterThan(written);
    }

    [Fact]
    public void An_answer_older_than_ten_seconds_counts_as_none()
    {
        var now = TimeSpan.Zero;
        var device = FakePsus.Corsair(watts: 140);
        using var source = new PsuSource(new FakeHidPort(device), static () => true, static () => [], () => now);
        source.Contribute(new SampleDraft());
        device.Deaf = true;

        now += TimeSpan.FromSeconds(9);
        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.PsuWallW.ShouldBe(140);

        now += TimeSpan.FromSeconds(2);
        draft = new SampleDraft();
        source.Contribute(draft);

        draft.PsuWallW.ShouldBeNull();
        draft.PsuName.ShouldBe("Corsair RM1000i");
        source.Unavailable.ShouldBe("the Corsair RM1000i did not answer");
    }

    [Fact]
    public void A_supply_that_stops_answering_is_tried_again_less_and_less_often()
    {
        var now = TimeSpan.Zero;
        var device = FakePsus.Corsair(watts: 140);
        device.Deaf = true;
        using var source = new PsuSource(new FakeHidPort(device), static () => true, static () => [], () => now);

        source.Contribute(new SampleDraft());
        device.Written.Count.ShouldBe(1);                // the handshake, which went unanswered
        device.Closes.ShouldBe(1);                       // and the handle given up, so the next try starts afresh

        now += TimeSpan.FromSeconds(1);
        source.Contribute(new SampleDraft());
        device.Written.Count.ShouldBe(1);

        now += TimeSpan.FromSeconds(1);                  // two seconds after the first try
        source.Contribute(new SampleDraft());
        device.Written.Count.ShouldBe(2);

        now += TimeSpan.FromSeconds(2);                  // four would be too soon for the second failure
        source.Contribute(new SampleDraft());
        device.Written.Count.ShouldBe(2);

        now += TimeSpan.FromSeconds(2);
        source.Contribute(new SampleDraft());
        device.Written.Count.ShouldBe(3);
    }

    [Fact]
    public void A_supply_that_answers_again_is_read_again_at_once()
    {
        var now = TimeSpan.Zero;
        var device = FakePsus.Corsair(watts: 140);
        device.Deaf = true;
        using var source = new PsuSource(new FakeHidPort(device), static () => true, static () => [], () => now);
        source.Contribute(new SampleDraft());
        now += TimeSpan.FromSeconds(2);
        source.Contribute(new SampleDraft());

        device.Deaf = false;
        now += TimeSpan.FromSeconds(4);
        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.PsuWallW.ShouldBe(140);

        now += TimeSpan.FromSeconds(2);
        draft = new SampleDraft();
        source.Contribute(draft);

        draft.PsuWallW.ShouldBe(140);
        source.Unavailable.ShouldBeNull();
    }

    [Fact]
    public void Windows_refusing_the_handle_costs_the_reading_and_nothing_else()
    {
        var device = FakePsus.Corsair(watts: 140);
        var port = new FakeHidPort(device) { RefusesToOpen = true };
        using var source = new PsuSource(port, static () => true, static () => [], static () => TimeSpan.Zero);
        var draft = new SampleDraft();

        source.Contribute(draft);

        draft.PsuWallW.ShouldBeNull();
        draft.PsuName.ShouldBe("Corsair RM1000i");
        source.Unavailable.ShouldBe("the Corsair RM1000i would not open");
    }

    [Fact]
    public void Nothing_the_hid_layer_or_the_lists_around_it_throw_escapes_a_tick()
    {
        var device = FakePsus.Corsair(watts: 140);

        Should.NotThrow(() => Contribute(new FakeHidPort(device) { FindThrows = new IOException("no devices") }));
        Should.NotThrow(() => Contribute(new FakeHidPort(device) { OpenThrows = new UnauthorizedAccessException() }));
        Should.NotThrow(() => Contribute(new FakeHidPort(FakeThatThrowsOnRead())));
        Should.NotThrow(() => Contribute(new FakeHidPort(device), programs: () => throw new InvalidOperationException()));
        Should.NotThrow(() => Contribute(new FakeHidPort(device), reading: () => throw new InvalidOperationException()));

        var draft = new SampleDraft();
        var source = new PsuSource(
            new FakeHidPort(FakeThatThrowsOnRead()), static () => true, static () => [], static () => TimeSpan.Zero);
        source.Contribute(draft);
        draft.PsuWallW.ShouldBeNull();
        source.Unavailable.ShouldNotBeNull();
        Should.NotThrow(source.Dispose);
        Should.NotThrow(source.Dispose);
    }

    [Fact]
    public void A_write_windows_will_not_take_leaves_the_reading_empty()
    {
        var device = FakePsus.Corsair(watts: 140);
        device.RefusesWrites = true;
        using var source = new PsuSource(new FakeHidPort(device), static () => true, static () => [], static () => TimeSpan.Zero);
        var draft = new SampleDraft();

        source.Contribute(draft);

        draft.PsuWallW.ShouldBeNull();
        device.Written.Count.ShouldBe(1);                // it gave up after the write was refused
    }

    [Fact]
    public void A_supply_that_has_been_unplugged_is_let_go_of_and_stops_being_named()
    {
        var now = TimeSpan.Zero;
        var device = FakePsus.Corsair(watts: 140);
        using var source = new PsuSource(new FakeHidPort(device), static () => true, static () => [], () => now);

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.PsuName.ShouldBe("Corsair RM1000i");

        // The case is opened and the supply taken out: Windows stops listing it and it answers nothing.
        device.Present = false;
        device.Deaf = true;
        draft = Ticks(source, at => now = at, from: 2, to: 120);

        draft.PsuName.ShouldBeNull();
        draft.PsuWallW.ShouldBeNull();
        source.Unavailable.ShouldBe(PsuModels.NoneFound);
    }

    [Fact]
    public void A_different_supply_put_in_afterwards_is_found_and_read()
    {
        var now = TimeSpan.Zero;
        var corsair = FakePsus.Corsair(watts: 140);
        var nzxt = FakePsus.Nzxt(
            watts: [92, 0, 12, 0.5, 0.25], volts: [12, 12, 12, 5, 3.25], amps: [7.75, 0, 1, 0.125, 0.0625]);
        nzxt.Present = false;
        using var source = new PsuSource(new FakeHidPort(corsair, nzxt), static () => true, static () => [], () => now);

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.PsuWallW.ShouldBe(140);

        corsair.Present = false;
        corsair.Deaf = true;
        nzxt.Present = true;
        draft = Ticks(source, at => now = at, from: 2, to: 300, until: d => d.PsuName == "NZXT E500");

        draft.PsuName.ShouldBe("NZXT E500");
        draft.PsuOutputW.ShouldBe(104.75);
        draft.PsuWallW.ShouldBeNull();
        source.Supported.ShouldBeTrue();
        source.Unavailable.ShouldBeNull();
    }

    [Fact]
    public void A_supply_that_is_merely_quiet_is_kept_because_windows_still_lists_it()
    {
        // Silence on its own is no reason to let a supply go: it may be busy, or its bridge may be resetting. Only a
        // supply Windows has stopped listing has really been taken out.
        var now = TimeSpan.Zero;
        var device = FakePsus.Corsair(watts: 140);
        using var source = new PsuSource(new FakeHidPort(device), static () => true, static () => [], () => now);
        source.Contribute(new SampleDraft());

        device.Deaf = true;
        var draft = Ticks(source, at => now = at, from: 2, to: 300);

        draft.PsuName.ShouldBe("Corsair RM1000i");
        source.Unavailable.ShouldBe("the Corsair RM1000i did not answer");
    }

    [Fact]
    public void Disposing_the_source_closes_the_device()
    {
        var device = FakePsus.Corsair(watts: 140);
        var source = new PsuSource(new FakeHidPort(device), static () => true, static () => [], static () => TimeSpan.Zero);
        source.Contribute(new SampleDraft());

        source.Dispose();

        device.Opens.ShouldBe(1);
        device.Closes.ShouldBe(1);
    }

    /// <summary>One tick a second from <paramref name="from"/> to <paramref name="to"/>, stopping as soon as
    /// <paramref name="until"/> is happy; the last draft filled.</summary>
    private static SampleDraft Ticks(
        PsuSource source, Action<TimeSpan> clock, int from, int to, Func<SampleDraft, bool>? until = null)
    {
        var draft = new SampleDraft();
        for (var second = from; second <= to; second++)
        {
            clock(TimeSpan.FromSeconds(second));
            draft = new SampleDraft();
            source.Contribute(draft);
            if (until?.Invoke(draft) == true) break;
        }

        return draft;
    }

    private static FakeHidDevice FakeThatThrowsOnRead()
    {
        var device = FakePsus.Corsair(watts: 140);
        device.ReadThrows = new IOException("the device was unplugged");
        return device;
    }

    private static void Contribute(FakeHidPort port, Func<bool>? reading = null, Func<IReadOnlyCollection<string>>? programs = null)
    {
        using var source = new PsuSource(
            port, reading ?? (static () => true), programs ?? (static () => []), static () => TimeSpan.Zero);
        source.Contribute(new SampleDraft());
    }
}
