using System.Runtime.InteropServices;
using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class ArcSourceTests
{
    /// <summary>An Arc A770 as Level Zero describes it: a GPU with neither integrated flag set.</summary>
    private static readonly SysmanDevice ArcA770 = new(SysmanDevice.GpuType, DiscreteGpu.IntelVendor, 0x56A0, CoreFlags: 0, ExtendedFlags: 0);

    /// <summary>The development laptop's Iris Xe as its driver described it: integrated in both sets of flags.</summary>
    private static readonly SysmanDevice IrisXe = new(SysmanDevice.GpuType, DiscreteGpu.IntelVendor, 0x9A49, CoreFlags: 1, ExtendedFlags: 1);

    private readonly FakeSysman _sysman = new();
    private bool _switchedOff;

    private ArcSource Open(bool includeIntegrated = false) => new(_sysman, _ => () => _switchedOff, includeIntegrated);

    private static SampleDraft Tick(ArcSource source)
    {
        var draft = new SampleDraft();
        source.Contribute(draft);
        return draft;
    }

    [Fact]
    public void A_card_with_a_card_domain_is_measured_as_the_whole_board()
    {
        var domains = _sysman.Add(ArcA770, PowerDomain.Package, PowerDomain.Card);
        var (package, card) = (domains[0], domains[1]);
        using var source = Open();

        source.Supported.ShouldBeTrue();
        source.Unavailable.ShouldBeNull();
        Tick(source);
        package.Draw(watts: 150, seconds: 1);
        card.Draw(watts: 190, seconds: 1);

        var draft = Tick(source);
        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuScope.ShouldBe(GpuPowerScope.Board);
        draft.DGpuW.ShouldNotBeNull().ShouldBe(190, 1e-9);
        package.Reads.ShouldBe(0);
    }

    [Fact]
    public void A_card_without_a_card_domain_is_measured_by_its_package_and_says_so()
    {
        var package = _sysman.Add(ArcA770, PowerDomain.Memory, PowerDomain.Package)[1];
        using var source = Open();

        Tick(source);
        package.Draw(watts: 120, seconds: 1);

        var draft = Tick(source);
        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuScope.ShouldBe(GpuPowerScope.Package);     // the model estimates the rest of the card
        draft.DGpuW.ShouldNotBeNull().ShouldBe(120, 1e-9);
    }

    [Fact]
    public void Integrated_graphics_are_skipped_because_the_processor_package_already_counts_them()
    {
        _sysman.Add(IrisXe, PowerDomain.Package);
        _sysman.Add(ArcA770 with { DeviceId = 0x7D55, CoreFlags = 1 }, PowerDomain.Package);       // flagged in the core properties only
        _sysman.Add(ArcA770 with { DeviceId = 0x7DD5, ExtendedFlags = 1 }, PowerDomain.Package);   // flagged in the extended properties only
        _sysman.Add(ArcA770 with { Type = 5, DeviceId = 0x7D1D }, PowerDomain.Package);            // a neural processor, not a GPU
        using var source = Open();

        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no Intel Arc discrete GPU");
        var draft = Tick(source);
        draft.DGpuPresent.ShouldBeFalse();
        draft.DGpuW.ShouldBeNull();
        _sysman.Domains.ShouldAllBe(domain => domain.Reads == 0);

        // A card listed after them is the one read.
        var card = _sysman.Add(ArcA770, PowerDomain.Card)[0];
        using var withCard = Open();
        withCard.Supported.ShouldBeTrue();
        Tick(withCard);
        card.Draw(watts: 60, seconds: 2);
        Tick(withCard).DGpuW.ShouldNotBeNull().ShouldBe(60, 1e-9);
        _sysman.Domains.Where(domain => domain != card).ShouldAllBe(domain => domain.Reads == 0);
    }

    [Fact]
    public void Integrated_graphics_are_read_only_when_a_test_asks_for_them()
    {
        var package = _sysman.Add(IrisXe, PowerDomain.Package)[0];
        using var source = Open(includeIntegrated: true);

        source.Supported.ShouldBeTrue();
        Tick(source);
        package.Draw(watts: 6.8, seconds: 2);

        var draft = Tick(source);
        draft.DGpuScope.ShouldBe(GpuPowerScope.Package);
        draft.DGpuW.ShouldNotBeNull().ShouldBe(6.8, 1e-9);
    }

    [Fact]
    public void Watts_are_the_microjoules_used_over_the_microseconds_between_two_readings()
    {
        var card = _sysman.Add(ArcA770, PowerDomain.Card)[0];
        card.Microjoules = 126_749_099_356;
        card.Microseconds = 175_789_238_876;
        using var source = Open();

        var first = Tick(source);
        first.DGpuPresent.ShouldBeTrue();
        first.DGpuW.ShouldBeNull();         // one reading has no interval, so the model estimates this tick from the load

        card.Microjoules += 45_000_000;     // 45 J over 1.5 s
        card.Microseconds += 1_500_000;
        Tick(source).DGpuW.ShouldNotBeNull().ShouldBe(30, 1e-9);

        card.Microseconds += 1_000_000;     // a second in which nothing was drawn
        Tick(source).DGpuW.ShouldBe(0);

        card.Draw(watts: 212.5, seconds: 0.25);
        Tick(source).DGpuW.ShouldNotBeNull().ShouldBe(212.5, 1e-9);
        card.Reads.ShouldBe(4);
    }

    [Fact]
    public void A_counter_that_went_back_was_reset_or_wrapped_and_measuring_starts_again_from_it()
    {
        var card = _sysman.Add(ArcA770, PowerDomain.Card)[0];
        card.Microjoules = 262_143_000_000;         // just short of a 32-bit counter of 1/16384 J
        card.Microseconds = 3_000_000;
        using var source = Open();
        Tick(source);

        card.Microjoules = 1_500_000;               // wrapped
        card.Microseconds += 1_000_000;
        Tick(source).DGpuW.ShouldBeNull();

        card.Draw(watts: 12, seconds: 1);
        Tick(source).DGpuW.ShouldNotBeNull().ShouldBe(12, 1e-9);

        card.Microjoules += 5_000_000;              // the driver restarted, and its clock with it
        card.Microseconds = 40_000;
        Tick(source).DGpuW.ShouldBeNull();

        card.Draw(watts: 7, seconds: 2);
        Tick(source).DGpuW.ShouldNotBeNull().ShouldBe(7, 1e-9);
    }

    [Fact]
    public void A_counter_that_stands_still_reads_zero_only_while_windows_has_the_card_switched_off()
    {
        var card = _sysman.Add(ArcA770, PowerDomain.Card)[0];
        card.Microjoules = 900_000_000;
        card.Microseconds = 10_000_000;
        SysmanDevice? lookedUp = null;
        using var source = new ArcSource(_sysman, device =>
        {
            lookedUp = device;
            return () => _switchedOff;
        });
        lookedUp.ShouldBe(ArcA770);

        Tick(source);
        Tick(source).DGpuW.ShouldBeNull();          // no new reading from a card that is on says nothing about its draw

        _switchedOff = true;
        var off = Tick(source);
        off.DGpuPresent.ShouldBeTrue();
        off.DGpuW.ShouldBe(0);

        _switchedOff = false;
        card.Draw(watts: 35, seconds: 3);
        Tick(source).DGpuW.ShouldNotBeNull().ShouldBe(35, 1e-9);
    }

    [Fact]
    public void Without_the_level_zero_library_the_source_says_so_and_claims_nothing()
    {
        _sysman.Add(ArcA770, PowerDomain.Card);
        _sysman.Throw = new DllNotFoundException("Unable to load DLL 'ze_loader.dll' or one of its dependencies");

        var missing = Should.NotThrow(() => Open());
        missing.Supported.ShouldBeFalse();
        missing.Unavailable.ShouldBe("no Level Zero library installed");
        var draft = Should.NotThrow(() => Tick(missing));
        draft.DGpuPresent.ShouldBeFalse();
        draft.DGpuW.ShouldBeNull();

        _sysman.Throw = new EntryPointNotFoundException("Unable to find an entry point named 'zesInit' in DLL 'ze_loader.dll'");
        Should.NotThrow(() => Open()).Unavailable.ShouldBe("the Level Zero library is too old");
    }

    [Fact]
    public void Level_zero_that_fails_or_throws_while_the_source_looks_for_a_card_leaves_it_unsupported_without_throwing()
    {
        var card = _sysman.Add(ArcA770, PowerDomain.Card)[0];

        _sysman.InitResult = FakeSysman.Uninitialised;
        Should.NotThrow(() => Open()).Unavailable.ShouldBe("Level Zero would not start");
        _sysman.InitResult = 0;

        _sysman.Throw = new SEHException("External component has thrown an exception.");
        var thrown = Should.NotThrow(() => Open());
        thrown.Supported.ShouldBeFalse();
        thrown.Unavailable.ShouldBe("External component has thrown an exception.");
        _sysman.Throw = null;

        _sysman.Find(ArcA770).PropertiesResult = FakeSysman.DeviceLost;
        Should.NotThrow(() => Open()).Unavailable.ShouldBe("no Intel Arc discrete GPU");
        _sysman.Find(ArcA770).PropertiesResult = 0;

        _sysman.Find(ArcA770).DomainsResult = FakeSysman.Unsupported;
        Should.NotThrow(() => Open()).Unavailable.ShouldBe("the Intel Arc GPU reports no card or package power");
        _sysman.Find(ArcA770).DomainsResult = 0;

        card.PropertiesResult = FakeSysman.Unsupported;
        Should.NotThrow(() => Open()).Unavailable.ShouldBe("the Intel Arc GPU reports no card or package power");
        card.PropertiesResult = 0;

        var looking = Should.NotThrow(() => new ArcSource(_sysman, _ => throw new InvalidOperationException("WMI is not running")));
        looking.Unavailable.ShouldBe("WMI is not running");
        card.Reads.ShouldBe(0);
    }

    [Fact]
    public void A_reading_that_fails_or_throws_has_no_watts_and_the_next_good_one_measures_from_the_last_good_one()
    {
        var card = _sysman.Add(ArcA770, PowerDomain.Card)[0];
        using var source = Open();
        Tick(source);

        card.Draw(watts: 20, seconds: 1);
        card.EnergyResult = FakeSysman.DeviceLost;
        var failed = Should.NotThrow(() => Tick(source));
        failed.DGpuPresent.ShouldBeTrue();
        failed.DGpuW.ShouldBeNull();

        card.EnergyResult = 0;
        card.Throws = new SEHException("External component has thrown an exception.");
        var thrown = Should.NotThrow(() => Tick(source));
        thrown.DGpuPresent.ShouldBeTrue();
        thrown.DGpuW.ShouldBeNull();

        card.Throws = null;
        card.Draw(watts: 40, seconds: 1);
        Tick(source).DGpuW.ShouldNotBeNull().ShouldBe(30, 1e-9);      // 60 J over the 2 s since the last good reading
    }

    [Fact]
    public void A_card_windows_has_switched_off_reads_zero_when_its_counter_cannot_be_read_either()
    {
        var card = _sysman.Add(ArcA770, PowerDomain.Card)[0];
        card.EnergyResult = FakeSysman.Unsupported;
        using var source = Open();

        Tick(source).DGpuW.ShouldBeNull();
        _switchedOff = true;
        Tick(source).DGpuW.ShouldBe(0);

        using var broken = new ArcSource(_sysman, _ => () => throw new InvalidOperationException("no answer"));
        Should.NotThrow(() => Tick(broken)).DGpuW.ShouldBeNull();
    }

    [Fact]
    public void The_source_is_named_for_the_status_screen()
    {
        using var source = Open();
        source.Name.ShouldBe("arc-gpu");
    }
}
