using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public unsafe class AmdSourceTests
{
    /// <summary>ADLX_BAD_VER, which is what an ADLX too old for the version asked for answers.</summary>
    private const int BadVersion = 5;

    /// <summary>ADLX_ORPHAN_OBJECTS, which every ADLX call answers once the user has logged off.</summary>
    private const int OrphanObjects = 11;

    /// <summary>ADL_ERR_NOT_INIT.</summary>
    private const int AdlNotInitialised = -2;

    private const string CardDevice = @"PCI\VEN_1002&DEV_747E&SUBSYS_54021462&REV_C8\4&1A2B3C4D&0&0008A";

    private TimeSpan _now = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_card_that_measures_its_whole_board_reports_board_power()
    {
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true, MeasuresChip = true, BoardWatts = 142.5 });
        using var source = From(adlx);

        var draft = new SampleDraft();
        source.Contribute(draft);

        source.Supported.ShouldBeTrue();
        source.Unavailable.ShouldBeNull();
        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuW.ShouldBe(142.5);
        draft.DGpuScope.ShouldBe(GpuPowerScope.Board);

        // The load is the load source's to fill, and this source leaves it alone.
        draft.DGpuLoad.ShouldBeNull();
        adlx.CalledAnUnknownMethod.ShouldBeFalse();
    }

    [Fact]
    public void A_card_that_measures_only_its_chip_says_so()
    {
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = false, MeasuresChip = true, ChipWatts = 118 });
        using var source = From(adlx);

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.DGpuW.ShouldBe(118);
        draft.DGpuScope.ShouldBe(GpuPowerScope.ChipOnly);
    }

    [Fact]
    public void A_card_that_measures_neither_is_there_but_has_no_watts()
    {
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = false, MeasuresChip = false });
        using var source = From(adlx);

        var draft = new SampleDraft();
        source.Contribute(draft);

        source.Supported.ShouldBeTrue();
        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuW.ShouldBeNull();

        // Nothing to read means nothing is asked for: the power model falls back to the load estimate.
        adlx.Reads.ShouldBe(0);
    }

    [Fact]
    public void Graphics_inside_the_processor_are_passed_over()
    {
        using var integrated = new FakeAdlx(new FakeAdlxGpu { Type = 1, MeasuresBoard = true });
        using var source = From(integrated);

        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no AMD discrete GPU");

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.DGpuPresent.ShouldBeFalse();
        draft.DGpuW.ShouldBeNull();

        // Nothing is held on to, and ADLX is stopped again.
        integrated.OutstandingInterfaces.ShouldBe(0);
        integrated.Stops.ShouldBe(1);
    }

    [Fact]
    public void A_card_beside_the_processor_graphics_is_the_one_read()
    {
        using var adlx = new FakeAdlx(
            new FakeAdlxGpu { Type = 1, MeasuresBoard = true, BoardWatts = 12 },
            new FakeAdlxGpu { Type = 2, MeasuresBoard = true, BoardWatts = 210 });
        using var source = From(adlx);

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.DGpuW.ShouldBe(210);
    }

    [Fact]
    public void Without_adlx_the_older_library_answers()
    {
        using var adl = new FakeAdl(new FakeAdlAdapter { BoardWatts = 173, ChipWatts = 150 });
        using var source = From(adlx: null, adl);

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuW.ShouldBe(173);
        draft.DGpuScope.ShouldBe(GpuPowerScope.Board);
        adl.AllocatorWorks.ShouldBeTrue();
    }

    [Fact]
    public void Where_adlx_will_not_have_this_driver_the_older_library_answers()
    {
        // ADLX declines drivers older than itself, and those drivers are exactly the ones ADL was left in for.
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true }) { StartResult = BadVersion };
        using var adl = new FakeAdl(new FakeAdlAdapter { BoardWatts = 173 });
        using var source = From(adlx, adl);

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.DGpuW.ShouldBe(173);
    }

    [Fact]
    public void While_adlx_is_silent_the_older_librarys_empty_answer_settles_nothing()
    {
        // ADL in session 0 has been seen to list no adapters at all, which says nothing about a machine whose ADLX has
        // not started yet, so the source stays alive to ask again.
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true }) { StartResult = BadVersion };
        using var adl = new FakeAdl();
        using var source = From(adlx, adl);

        source.Supported.ShouldBeTrue();
        source.Unavailable.ShouldBeNull();
    }

    [Fact]
    public void The_older_library_reads_the_chip_where_the_card_reports_no_board_power()
    {
        using var adl = new FakeAdl(new FakeAdlAdapter { ChipWatts = 150 });
        using var source = From(adlx: null, adl);

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.DGpuW.ShouldBe(150);
        draft.DGpuScope.ShouldBe(GpuPowerScope.ChipOnly);
    }

    [Fact]
    public void The_older_library_passes_over_graphics_inside_the_processor()
    {
        // ADL_ASIC_INTEGRATED and ADL_ASIC_FUSION, as an APU's graphics report themselves.
        using var adl = new FakeAdl(new FakeAdlAdapter { AsicTypes = (1 << 1) | (1 << 5), BoardWatts = 15 });
        using var source = From(adlx: null, adl);

        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no AMD discrete GPU");
        adl.Destroys.ShouldBe(1);
    }

    [Fact]
    public void The_older_library_believes_the_unique_device_id_over_adls_own_vendor_field()
    {
        // ADL's vendor field has been seen to hold what Windows wrote in hexadecimal read as decimal, so the unique
        // device id, which Windows words as "PCI_VEN_8086&...", decides.
        using var adl = new FakeAdl(new FakeAdlAdapter
        {
            Udid = "PCI_VEN_8086&DEV_56A0&SUBSYS_10203040&REV_08_4&5E6F7A8B&0&0010A",
            VendorId = 0x1002,
            BoardWatts = 90,
        });
        using var source = From(adlx: null, adl);

        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no AMD discrete GPU");
    }

    [Fact]
    public void The_older_library_falls_back_on_the_vendor_field_where_the_device_id_does_not_name_a_vendor()
    {
        using var adl = new FakeAdl(new FakeAdlAdapter { Udid = "0:2:0:0:0", VendorId = 0x1002, BoardWatts = 90 });
        using var source = From(adlx: null, adl);

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.DGpuW.ShouldBe(90);
    }

    [Fact]
    public void The_older_library_passes_over_an_adapter_the_machine_no_longer_has()
    {
        // A bus number below zero is an adapter Windows remembers and cannot be asked about.
        using var adl = new FakeAdl(new FakeAdlAdapter { Bus = -1, BoardWatts = 90 });
        using var source = From(adlx: null, adl);

        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no AMD discrete GPU");
    }

    [Fact]
    public void The_older_library_names_the_card_so_windows_can_be_asked_whether_it_is_switched_off()
    {
        using var adl = new FakeAdl(new FakeAdlAdapter { Pnp = CardDevice, BoardWatts = 90 });
        var asked = new List<string>();
        using var source = From(adlx: null, adl, poweredOff: device => { asked.Add(device); return true; });

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.DGpuW.ShouldBe(0);
        adl.Reads.ShouldBe(0);
        asked.ShouldHaveSingleItem().ShouldBe(CardDevice);
    }

    [Fact]
    public void A_machine_with_neither_library_says_so_and_claims_nothing()
    {
        using var source = From(adlx: null, adl: null);

        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no AMD driver installed");
        source.Name.ShouldBe("amd-gpu");

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.DGpuPresent.ShouldBeFalse();
        draft.DGpuW.ShouldBeNull();
    }

    [Fact]
    public void A_library_that_will_not_start_yet_is_tried_again_and_keeps_the_source_alive()
    {
        // A service starts before anyone has logged on, and ADLX answers no one until they do.
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true, BoardWatts = 99 }) { StartResult = BadVersion };
        using var source = From(adlx);

        source.Supported.ShouldBeTrue();
        source.Unavailable.ShouldBeNull();

        var early = new SampleDraft();
        source.Contribute(early);
        early.DGpuW.ShouldBeNull();
        adlx.Starts.ShouldBe(0);

        // Not before the waiting time is up, and then only once a minute.
        _now += AmdSource.RetryEvery - TimeSpan.FromSeconds(1);
        source.Contribute(new SampleDraft());
        adlx.Starts.ShouldBe(0);

        adlx.StartResult = 0;
        _now += TimeSpan.FromSeconds(1);
        var later = new SampleDraft();
        source.Contribute(later);

        adlx.Starts.ShouldBe(1);
        later.DGpuW.ShouldBe(99);
    }

    [Fact]
    public void A_library_that_has_been_lost_is_let_go_of_and_opened_again()
    {
        // Every ADLX call answers ADLX_ORPHAN_OBJECTS once the user logs off, and the interfaces must be given back.
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true, BoardWatts = 88 });
        using var source = From(adlx);

        source.Contribute(new SampleDraft());
        adlx.ReadResult = OrphanObjects;

        var lost = new SampleDraft();
        source.Contribute(lost);
        lost.DGpuW.ShouldBeNull();
        adlx.OutstandingInterfaces.ShouldBe(0);
        adlx.Stops.ShouldBe(1);

        adlx.ReadResult = 0;
        _now += AmdSource.RetryEvery;
        var again = new SampleDraft();
        source.Contribute(again);

        adlx.Starts.ShouldBe(2);
        again.DGpuW.ShouldBe(88);
    }

    [Fact]
    public void A_reading_the_card_declines_leaves_the_watts_out_and_the_library_open()
    {
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true, BoardWatts = 70 }) { PowerResult = 12 };
        using var source = From(adlx);

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuW.ShouldBeNull();
        adlx.Stops.ShouldBe(0);

        // ADLX_NOT_SUPPORTED is this card declining once, not the library going away, so the metrics are still let go.
        adlx.OutstandingInterfaces.ShouldBe(2);
    }

    [Fact]
    public void The_older_library_that_has_been_lost_is_opened_again()
    {
        using var adl = new FakeAdl(new FakeAdlAdapter { BoardWatts = 173 }) { ReadResult = AdlNotInitialised };
        using var source = From(adlx: null, adl);

        source.Contribute(new SampleDraft());
        adl.Destroys.ShouldBe(1);

        adl.ReadResult = 0;
        _now += AmdSource.RetryEvery;
        var again = new SampleDraft();
        source.Contribute(again);

        adl.Creates.ShouldBe(2);
        again.DGpuW.ShouldBe(173);
    }

    [Fact]
    public void A_switched_off_card_draws_nothing_and_the_library_is_never_asked()
    {
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true, Pnp = CardDevice });
        var asked = new List<string>();
        using var source = From(adlx, poweredOff: device => { asked.Add(device); return true; });

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuW.ShouldBe(0);
        draft.DGpuScope.ShouldBe(GpuPowerScope.Board);
        adlx.Reads.ShouldBe(0);

        // Windows is asked about the card ADLX named, by the Plug and Play instance id ADLX gave for it.
        asked.ShouldHaveSingleItem().ShouldBe(CardDevice);
    }

    [Fact]
    public void Every_interface_is_given_back_and_the_library_stopped()
    {
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true });
        var source = From(adlx);

        for (var tick = 0; tick < 5; tick++) source.Contribute(new SampleDraft());
        adlx.Reads.ShouldBe(5);
        source.Dispose();

        adlx.OutstandingInterfaces.ShouldBe(0);
        adlx.Starts.ShouldBe(1);
        adlx.Stops.ShouldBe(1);
        adlx.CalledAnUnknownMethod.ShouldBeFalse();
    }

    [Fact]
    public void Two_readers_share_one_adlx_session_and_the_last_one_out_stops_it()
    {
        // ADLX is started for the whole process, not for each caller. A sensor set that is thrown away while a stuck
        // one still holds its interfaces must not stop the library out from under it.
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true });
        var library = adlx.Library;

        var first = Adlx.Open(library).Gpu.ShouldNotBeNull();
        var second = Adlx.Open(library).Gpu.ShouldNotBeNull();
        adlx.Starts.ShouldBe(1);

        first.Dispose();
        adlx.Stops.ShouldBe(0);

        second.Dispose();
        adlx.Stops.ShouldBe(1);
        adlx.OutstandingInterfaces.ShouldBe(0);
    }

    [Fact]
    public void A_lost_session_is_only_started_again_once_its_last_reader_has_let_go()
    {
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true });
        var library = adlx.Library;
        var first = Adlx.Open(library).Gpu.ShouldNotBeNull();
        var second = Adlx.Open(library).Gpu.ShouldNotBeNull();

        adlx.ReadResult = OrphanObjects;                    // the user logged off
        first.Read().Lost.ShouldBeTrue();
        first.Dispose();
        adlx.Stops.ShouldBe(0);

        // Stopping ADLX now would leave the other reader's interfaces pointing at memory that is no longer theirs.
        Adlx.Open(library).State.ShouldBe(AmdLibrary.WouldNotStart);
        adlx.Starts.ShouldBe(1);

        second.Dispose();
        adlx.Stops.ShouldBe(1);
        Adlx.Open(library).Gpu.ShouldNotBeNull().Dispose();
        adlx.Starts.ShouldBe(2);
    }

    [Fact]
    public void A_reader_that_never_lets_go_is_written_off_so_the_watts_come_back_without_a_restart()
    {
        // A reader wedged inside the library, or dropped without being closed, never calls Leave. Its lost session
        // would otherwise keep a count of one for ever and every later reader would be turned away for good: AMD's
        // watts would not come back until the service was restarted.
        var now = TimeSpan.Zero;
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true });
        var library = adlx.LibraryOn(() => now);
        var stuck = Adlx.Open(library).Gpu.ShouldNotBeNull();
        var second = Adlx.Open(library).Gpu.ShouldNotBeNull();

        adlx.ReadResult = OrphanObjects;                    // the user logged off
        second.Read().Lost.ShouldBeTrue();
        second.Dispose();                                   // and this one let go; the stuck one never will

        Adlx.Open(library).State.ShouldBe(AmdLibrary.WouldNotStart);

        now += AdlxLibrary.OrphanGrace - TimeSpan.FromSeconds(1);
        Adlx.Open(library).State.ShouldBe(AmdLibrary.WouldNotStart);

        now += TimeSpan.FromSeconds(2);
        var again = Adlx.Open(library).Gpu.ShouldNotBeNull();
        adlx.Starts.ShouldBe(2);

        // The written-off reader may still hold interfaces from the old session, so ADLX is never stopped again: a
        // library left running for the life of the process beats one torn out from under a live pointer.
        again.Dispose();
        adlx.Stops.ShouldBe(0);

        GC.KeepAlive(stuck);
    }

    [Fact]
    public void The_wait_for_an_orphan_is_measured_from_the_moment_the_session_was_lost()
    {
        var now = TimeSpan.Zero;
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true });
        var library = adlx.LibraryOn(() => now);

        // A whole grace period of ordinary reading is no reason to write the other reader off the moment it is needed.
        var first = Adlx.Open(library).Gpu.ShouldNotBeNull();
        now += AdlxLibrary.OrphanGrace * 2;
        var second = Adlx.Open(library).Gpu.ShouldNotBeNull();
        adlx.ReadResult = OrphanObjects;
        second.Read().Lost.ShouldBeTrue();
        second.Dispose();

        Adlx.Open(library).State.ShouldBe(AmdLibrary.WouldNotStart);
        adlx.Starts.ShouldBe(1);

        // The reader that is still there lets go in good time, so nothing is written off and ADLX stops as it should.
        first.Dispose();
        adlx.Stops.ShouldBe(1);
    }

    [Fact]
    public void The_version_asked_for_is_the_published_one()
    {
        using var adlx = new FakeAdlx(new FakeAdlxGpu { MeasuresBoard = true });
        using var source = From(adlx);

        // ADLX_FULL_VERSION packs major, minor, release and build into 16 bits each; the SDK this was written against
        // is 2.0.0.125.
        adlx.AskedVersion.ShouldBe((2UL << 48) | 125UL);
    }

    [Fact]
    public void A_library_that_throws_costs_its_own_fields_and_nothing_else()
    {
        var source = new AmdSource(() => throw new InvalidOperationException("no"), _ => false, () => _now);

        source.Supported.ShouldBeTrue();
        var draft = new SampleDraft();
        Should.NotThrow(() => source.Contribute(draft));
        draft.DGpuW.ShouldBeNull();
        Should.NotThrow(source.Dispose);
    }

    [Fact]
    public void A_card_that_throws_when_it_is_read_costs_its_own_fields_and_nothing_else()
    {
        var falling = new FallingOverGpu();
        var source = new AmdSource(() => new AmdOpening(AmdLibrary.Reading, falling, null), _ => false, () => _now);

        var draft = new SampleDraft();
        Should.NotThrow(() => source.Contribute(draft));
        draft.DGpuW.ShouldBeNull();
        falling.Disposals.ShouldBe(1);
        Should.NotThrow(source.Dispose);
    }

    [Fact]
    public void The_structures_are_the_size_the_headers_say()
    {
        // AdapterInfo is eight integers and five ADL_MAX_PATH strings; ADLPMLogDataOutput is a size and 256 sensors of
        // two integers. A structure of the wrong size would read another adapter's fields, or none.
        sizeof(Adl.AdapterInfo).ShouldBe(1572);
        sizeof(Adl.PmLogData).ShouldBe(2052);
    }

    private AmdSource From(FakeAdlx? adlx, FakeAdl? adl = null, Func<string, bool>? poweredOff = null)
        => new(() => AmdSource.Choose(Opening(adlx), () => Opening(adl)), poweredOff ?? (_ => false), () => _now);

    private static AmdOpening Opening(FakeAdlx? adlx)
        => adlx is null
            ? new AmdOpening(AmdLibrary.NotInstalled, null, "AMD's ADLX library is not installed")
            : Adlx.Open(adlx.Library);

    private static AmdOpening Opening(FakeAdl? adl)
        => adl is null
            ? new AmdOpening(AmdLibrary.NotInstalled, null, "no AMD driver installed")
            : Adl.Open(adl.Library);

    private sealed class FallingOverGpu : IAmdGpu
    {
        public int Disposals { get; private set; }

        public string? DeviceId => null;

        public AmdReading Read() => throw new InvalidOperationException("the card fell over");

        public void Dispose()
        {
            Disposals++;
            throw new InvalidOperationException("and again on the way out");
        }
    }
}
