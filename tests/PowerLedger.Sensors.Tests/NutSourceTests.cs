using PowerLedger.Contracts;
using PowerLedger.Sensors;
using PowerLedger.Sensors.Nut;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class NutSourceTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);
    private TimeSpan _now = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task A_ups_served_elsewhere_gives_its_real_output_power()
    {
        await using var server = new FakeNutServer().Serve("myups", ("ups.realpower", "212"), ("ups.load", "40"), ("ups.realpower.nominal", "900"));
        using var source = Source(Target(server));

        Tick(source);                                      // takes up the settings
        (await source.PollAsync()).ShouldBe(NutSource.ReadEvery);
        var draft = Tick(source);

        draft.UpsOutputW.ShouldBe(212);
        draft.UpsSource.ShouldBe(UpsPowerSource.ActivePower);
        draft.UpsName.ShouldBe($"myups on 127.0.0.1:{server.Port}");
        source.Unavailable.ShouldBeNull();
        server.Commands.ShouldBe(["LIST VAR myups"]);
    }

    [Theory]
    [InlineData(new[] { "ups.realpower", "300", "ups.power", "500", "output.powerfactor", "0.9" }, 300.0, UpsPowerSource.ActivePower)]
    [InlineData(new[] { "ups.power", "500", "output.powerfactor", "0.9", "ups.load", "30", "ups.realpower.nominal", "900" }, 450.0, UpsPowerSource.ApparentPower)]
    [InlineData(new[] { "ups.power", "500", "ups.load", "30", "ups.realpower.nominal", "900" }, 270.0, UpsPowerSource.LoadOfRatedWatts)]
    [InlineData(new[] { "ups.power", "500", "ups.load", "30", "ups.power.nominal", "1500" }, 400.0, UpsPowerSource.ApparentPowerAssumedFactor)]
    [InlineData(new[] { "ups.load", "30", "ups.power.nominal", "1500" }, 360.0, UpsPowerSource.LoadOfRatedVoltAmps)]
    [InlineData(new[] { "ups.load", "30", "ups.power.nominal", "1500", "output.powerfactor", "0.6" }, 270.0, UpsPowerSource.LoadOfRatedVoltAmps)]
    [InlineData(new[] { "ups.realpower", "0", "ups.load", "0", "ups.realpower.nominal", "900" }, null, UpsPowerSource.None)]
    [InlineData(new[] { "ups.realpower", "n/a", "battery.charge", "100" }, null, UpsPowerSource.None)]
    public void The_most_exact_figure_the_ups_offers_is_taken(string[] pairs, double? watts, UpsPowerSource source)
    {
        var variables = new Dictionary<string, string>();
        for (var i = 0; i < pairs.Length; i += 2) variables[pairs[i]] = pairs[i + 1];

        var (found, how) = NutProtocol.Power(variables);

        how.ShouldBe(source);
        if (watts is null) found.ShouldBeNull();
        else found.ShouldNotBeNull().ShouldBe(watts.Value, 1e-9);
    }

    [Fact]
    public void Arguments_are_quoted_and_replies_unquoted_the_way_upsd_does_it()
    {
        NutProtocol.Quote("pa ss\"w\\rd").ShouldBe("\"pa ss\\\"w\\\\rd\"");
        NutProtocol.Words("VAR myups ups.mfr \"American \\\"Power\\\" Conversion\"")
            .ShouldBe(["VAR", "myups", "ups.mfr", "American \"Power\" Conversion"]);
        NutProtocol.Words("ERR ACCESS-DENIED").ShouldBe(["ERR", "ACCESS-DENIED"]);
        NutProtocol.Words("VAR myups ups.id \"\"").ShouldBe(["VAR", "myups", "ups.id", ""]);
    }

    [Fact]
    public async Task A_username_and_password_are_said_once_per_connection_and_login_is_never_sent()
    {
        await using var server = new FakeNutServer { Wants = ("monitor", "sec ret") }.Serve("myups", ("ups.realpower", "150"));
        using var source = Source(Target(server, user: "monitor", password: "sec ret"));
        Tick(source);

        await source.PollAsync();
        await source.PollAsync();

        Tick(source).UpsOutputW.ShouldBe(150);
        server.Commands.ShouldBe(["USERNAME \"monitor\"", "PASSWORD \"sec ret\"", "LIST VAR myups", "LIST VAR myups"]);
        server.Connections.ShouldBe(1);
    }

    [Fact]
    public async Task A_password_the_server_turns_down_is_said_and_tried_again_later()
    {
        await using var server = new FakeNutServer { Wants = ("monitor", "right") }.Serve("myups", ("ups.realpower", "150"));
        using var source = Source(Target(server, user: "monitor", password: "wrong"));
        Tick(source);

        var wait = await source.PollAsync();

        source.Unavailable.ShouldBe("127.0.0.1 turned down the username or password");
        wait.ShouldBe(NutSource.ReadEvery);
        (await source.PollAsync()).ShouldBe(NutSource.ReadEvery * 2);     // the second failure in a row waits twice as long
        Tick(source).UpsOutputW.ShouldBeNull();
    }

    [Fact]
    public async Task A_ups_the_server_does_not_serve_is_named()
    {
        await using var server = new FakeNutServer().Serve("other", ("ups.realpower", "150"));
        using var source = Source(Target(server));
        Tick(source);

        await source.PollAsync();

        source.Unavailable.ShouldBe("127.0.0.1 serves no UPS called myups");
    }

    [Fact]
    public async Task A_server_that_stops_answering_is_given_up_on_in_time_and_the_waits_double_to_a_minute()
    {
        await using var server = new FakeNutServer { Silent = true }.Serve("myups", ("ups.realpower", "150"));
        using var source = Source(Target(server));
        Tick(source);

        var waits = new List<TimeSpan>();
        for (var i = 0; i < 6; i++) waits.Add(await source.PollAsync());

        source.Unavailable.ShouldBe("127.0.0.1 did not answer in time");
        waits.ShouldBe([NutSource.ReadEvery, NutSource.ReadEvery * 2, NutSource.ReadEvery * 4, NutSource.ReadEvery * 8,
            NutSource.LongestBackoff, NutSource.LongestBackoff]);
        server.Connections.ShouldBe(6);                    // each failure lets the connection go and the next makes a new one
    }

    [Fact]
    public async Task A_server_that_closed_the_connection_is_reconnected_to()
    {
        await using var server = new FakeNutServer { DropAfterList = true }.Serve("myups", ("ups.realpower", "150"));
        using var source = Source(Target(server));
        Tick(source);

        await source.PollAsync();
        server.Upses["myups"]["ups.realpower"] = "160";
        (await source.PollAsync()).ShouldBe(NutSource.ReadEvery);           // found closed: let go
        (await source.PollAsync()).ShouldBe(NutSource.ReadEvery);           // and connected afresh

        Tick(source).UpsOutputW.ShouldBe(160);
        server.Connections.ShouldBe(2);
    }

    [Fact]
    public async Task A_host_nothing_listens_on_can_t_be_reached()
    {
        int port;
        await using (var closed = new FakeNutServer()) port = closed.Port;
        using var source = Source(new NutTarget("127.0.0.1", port, "myups"));
        Tick(source);

        await source.PollAsync();

        source.Unavailable.ShouldNotBeNull().ShouldStartWith("127.0.0.1 can't be reached");
    }

    [Fact]
    public async Task Stale_data_keeps_the_connection_and_the_last_answer_until_it_is_too_old()
    {
        await using var server = new FakeNutServer().Serve("myups", ("ups.realpower", "150"));
        using var source = Source(Target(server));
        Tick(source);
        await source.PollAsync();

        server.Stale = true;
        (await source.PollAsync()).ShouldBe(NutSource.ReadEvery);
        source.Unavailable.ShouldBe("the server has no fresh data from myups");
        _now += NutSource.StaleAfter;
        Tick(source).UpsOutputW.ShouldBe(150);
        _now += TimeSpan.FromSeconds(1);
        Tick(source).UpsOutputW.ShouldBeNull();
        server.Connections.ShouldBe(1);
    }

    [Fact]
    public async Task A_ups_on_usb_that_gave_watts_wins()
    {
        await using var server = new FakeNutServer().Serve("myups", ("ups.realpower", "150"));
        using var source = Source(Target(server));
        Tick(source);
        await source.PollAsync();

        var draft = new SampleDraft { UpsOutputW = 90, UpsSource = UpsPowerSource.LoadOfRatedWatts, UpsName = "APC Back-UPS" };
        source.Contribute(draft);

        (draft.UpsOutputW, draft.UpsSource, draft.UpsName).ShouldBe((90.0, UpsPowerSource.LoadOfRatedWatts, "APC Back-UPS"));
    }

    [Fact]
    public async Task Changed_settings_start_over_and_none_stop_reading()
    {
        await using var server = new FakeNutServer().Serve("myups", ("ups.realpower", "150")).Serve("second", ("ups.realpower", "75"));
        NutTarget? target = Target(server);
        using var source = new NutSource(() => target, () => _now, runsItself: false, Short);
        Tick(source);
        await source.PollAsync();

        target = Target(server) with { Ups = "second" };
        Tick(source).UpsOutputW.ShouldBeNull();           // a new session has read nothing yet
        source.Unavailable.ShouldBe($"second on 127.0.0.1:{server.Port} not read yet");
        await source.PollAsync();
        Tick(source).UpsOutputW.ShouldBe(75);

        target = null;
        Tick(source).UpsOutputW.ShouldBeNull();
        source.Unavailable.ShouldBe("no UPS on another computer is set up");
    }

    [Fact]
    public async Task The_source_polls_on_a_thread_of_its_own_when_left_to_itself()
    {
        await using var server = new FakeNutServer().Serve("myups", ("ups.realpower", "150"));
        using var source = new NutSource(() => Target(server, cached: true), () => _now, runsItself: true, Short);

        Tick(source);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Tick(source).UpsOutputW is null && DateTime.UtcNow < deadline) await Task.Delay(20);

        Tick(source).UpsOutputW.ShouldBe(150);
    }

    private NutTarget? _cached;

    private NutTarget Target(FakeNutServer server, string? user = null, string? password = null, bool cached = false)
    {
        if (cached && _cached is not null) return _cached;
        var target = new NutTarget("127.0.0.1", server.Port, "myups", user, password is null ? null : () => password);
        if (cached) _cached = target;
        return target;
    }

    private NutSource Source(NutTarget target) => new(() => target, () => _now, runsItself: false, Short);

    private static SampleDraft Tick(ISensorSource source)
    {
        var draft = new SampleDraft();
        source.Contribute(draft);
        return draft;
    }
}
