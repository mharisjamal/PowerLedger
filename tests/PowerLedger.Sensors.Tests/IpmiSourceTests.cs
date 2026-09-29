using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class IpmiSourceTests
{
    private TimeSpan _now = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_bmc_s_current_power_is_the_machine_s_input_wherever_windows_puts_the_completion_code(bool codeInFront)
    {
        var bmc = new FakeBmc { Answer = new(0, Reading(watts: 0x01A4, active: true, codeInFront)) };
        using var source = Source(bmc);

        source.Poll().ShouldBe(IpmiSource.ReadEvery);
        var draft = Tick(source);

        draft.SystemMeterW.ShouldBe(420);
        draft.SystemMeter.ShouldBe(SystemMeterKind.Bmc);
        draft.SystemMeterName.ShouldBe("BMC (DCMI)");
        source.Unavailable.ShouldBeNull();
        var (networkFunction, command, data) = bmc.Requests.ShouldHaveSingleItem();
        (networkFunction, command).ShouldBe((0x2C, 0x02));
        data.ShouldBe([0xDC, 0x01, 0x00, 0x00]);
    }

    [Fact]
    public void A_measurement_that_is_switched_off_gives_no_reading()
    {
        using var source = Source(new FakeBmc { Answer = new(0, Reading(watts: 300, active: false)) });

        source.Poll();

        Tick(source).SystemMeterW.ShouldBeNull();
        source.Unavailable.ShouldBe("the BMC's power measurement is switched off");
    }

    [Fact]
    public void A_bmc_without_dcmi_is_asked_again_only_rarely()
    {
        using var source = Source(new FakeBmc { Answer = new(0xC1, []) });

        source.Poll().ShouldBe(IpmiSource.UnsupportedWait);
        source.Unavailable.ShouldBe("the BMC doesn't report its power reading (no DCMI)");
    }

    [Fact]
    public void A_bmc_that_fails_is_asked_again_after_waits_that_double_to_a_minute()
    {
        var bmc = new FakeBmc { Fail = true };
        using var source = Source(bmc);

        var waits = Enumerable.Range(0, 6).Select(_ => source.Poll()).ToList();

        waits.ShouldBe([IpmiSource.ReadEvery, IpmiSource.ReadEvery * 2, IpmiSource.ReadEvery * 4, IpmiSource.ReadEvery * 8,
            IpmiSource.LongestBackoff, IpmiSource.LongestBackoff]);
        source.Unavailable.ShouldBe("asking the BMC failed: the BMC is busy");
        bmc.Fail = false;
        source.Poll().ShouldBe(IpmiSource.ReadEvery);
        Tick(source).SystemMeterW.ShouldBe(250);
    }

    [Fact]
    public void An_answer_goes_stale_and_a_platform_meter_s_reading_is_kept()
    {
        using var source = Source(new FakeBmc());
        source.Poll();

        var metered = new SampleDraft { SystemMeterW = 240, SystemMeter = SystemMeterKind.PowerMeter };
        source.Contribute(metered);
        metered.SystemMeter.ShouldBe(SystemMeterKind.PowerMeter);

        _now += IpmiSource.StaleAfter + TimeSpan.FromSeconds(1);
        Tick(source).SystemMeterW.ShouldBeNull();
    }

    [Fact]
    public void Without_a_bmc_the_source_is_unsupported_and_asks_nothing()
    {
        using var source = new IpmiSource(null, () => _now, runsItself: false);

        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no BMC: Windows' IPMI driver found none");
    }

    [Theory]
    [InlineData(new byte[] { 0xDC, 0x10 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x10 })]
    [InlineData(new byte[0])]
    public void A_short_or_foreign_answer_is_no_reading(byte[] data)
    {
        Dcmi.Watts(new BmcAnswer(0, data), out var why).ShouldBeNull();
        why.ShouldBe("the BMC's power reading couldn't be read");
    }

    [Fact]
    public void The_real_source_never_throws_on_a_machine_without_a_bmc()
    {
        using var source = new IpmiSource();

        Should.NotThrow(() => Tick(source));
        if (!source.Supported) source.Unavailable.ShouldNotBeNull();
    }

    private IpmiSource Source(FakeBmc bmc) => new(bmc, () => _now, runsItself: false);

    /// <summary>A DCMI Get Power Reading answer: the group extension, current, least, most and average watts, a timestamp,
    /// a reporting period and the state byte.</summary>
    private static byte[] Reading(int watts, bool active, bool codeInFront = false)
    {
        byte[] body =
        [
            0xDC, (byte)watts, (byte)(watts >> 8), 10, 0, 0x58, 0x02, 0x2C, 0x01, 1, 2, 3, 4, 0xE8, 0x03, 0, 0, (byte)(active ? 0x40 : 0),
        ];
        return codeInFront ? [0x00, .. body] : body;
    }

    private static SampleDraft Tick(ISensorSource source)
    {
        var draft = new SampleDraft();
        source.Contribute(draft);
        return draft;
    }

    private sealed class FakeBmc : IBmc
    {
        public BmcAnswer Answer { get; set; } = new(0, Reading(watts: 250, active: true));

        public bool Fail { get; set; }

        public List<(int, int, byte[])> Requests { get; } = [];

        public BmcAnswer Send(byte networkFunction, byte command, byte[] data)
        {
            Requests.Add((networkFunction, command, data));
            if (Fail) throw new InvalidOperationException("the BMC is busy.");
            return Answer;
        }
    }
}
