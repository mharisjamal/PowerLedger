using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class IntelCardSourceTests
{
    private const uint A770 = 0x56A0;
    private readonly FakeIgcl _igcl = new();
    private readonly FakeSource _levelZero = new("arc-gpu", draft => draft.AddGpu(DiscreteGpu.IntelVendor, A770).Scope = GpuPowerScope.Package);
    private bool _poweredOff;

    [Fact]
    public void The_card_s_total_energy_counter_gives_board_watts_between_two_readings()
    {
        _igcl.Discrete().Next(card: 1000, seconds: 10).Next(card: 1180, seconds: 11);
        using var source = Source();                    // the reading that showed the card counter starts the first interval

        var card = Tick(source).Gpus.ShouldHaveSingleItem();

        card.Watts.ShouldNotBeNull().ShouldBe(180, 1e-9);
        card.Scope.ShouldBe(GpuPowerScope.Board);
        card.VendorId.ShouldBe(DiscreteGpu.IntelVendor);
        card.DeviceId.ShouldBe(A770);
        source.Name.ShouldBe("arc-gpu");
        _levelZero.Calls.ShouldBe(0);
    }

    [Fact]
    public void Integrated_graphics_are_skipped_and_with_nothing_else_level_zero_is_asked()
    {
        _igcl.Adapter(integrated: true).Next(card: 5, seconds: 1);
        using var source = Source();

        Tick(source).Gpus.ShouldHaveSingleItem().Scope.ShouldBe(GpuPowerScope.Package);
        _levelZero.Calls.ShouldBe(1);
        _igcl.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void A_card_without_a_total_card_counter_is_left_to_level_zero()
    {
        _igcl.Discrete().Next(card: null, gpu: 500, seconds: 1);
        using var source = Source();

        Tick(source);

        _levelZero.Calls.ShouldBe(1);
    }

    [Fact]
    public void A_missing_library_is_quietly_level_zero()
    {
        using var source = new IntelCardSource(() => throw new DllNotFoundException("ControlLib.dll"), () => _levelZero, _ => () => _poweredOff);

        Tick(source);

        source.Supported.ShouldBeTrue();
        _levelZero.Calls.ShouldBe(1);
    }

    [Fact]
    public void A_library_that_will_not_start_is_level_zero_too()
    {
        _igcl.InitResult = 0x40000008;
        using var source = Source();

        Tick(source);

        _levelZero.Calls.ShouldBe(1);
    }

    [Fact]
    public void A_32_bit_process_never_loads_the_library()
    {
        using var source = new IntelCardSource(() => throw new InvalidOperationException("must not load"), () => _levelZero, _ => () => false,
            is64BitProcess: false);

        Tick(source);

        _levelZero.Calls.ShouldBe(1);
    }

    [Fact]
    public void A_card_windows_has_switched_off_draws_nothing_and_is_not_asked()
    {
        _igcl.Discrete().Next(card: 1000, seconds: 10);
        using var source = Source();
        var asked = _igcl.TelemetryCalls;
        _poweredOff = true;

        Tick(source).Gpus.ShouldHaveSingleItem().Watts.ShouldBe(0);
        _igcl.TelemetryCalls.ShouldBe(asked);
    }

    [Fact]
    public void A_counter_that_went_backwards_starts_a_new_interval()
    {
        _igcl.Discrete().Next(card: 1000, seconds: 10).Next(card: 1180, seconds: 11).Next(card: 5, seconds: 12).Next(card: 65, seconds: 13);
        using var source = Source();
        Tick(source).Gpus[0].Watts.ShouldNotBeNull().ShouldBe(180, 1e-9);

        Tick(source).Gpus[0].Watts.ShouldBeNull();
        Tick(source).Gpus[0].Watts.ShouldNotBeNull().ShouldBe(60, 1e-9);
    }

    [Fact]
    public void A_library_that_keeps_failing_hands_the_card_to_level_zero_for_good()
    {
        _igcl.Discrete().Next(card: 1000, seconds: 10);
        using var source = Source();
        _igcl.TelemetryResult = 0x40000001;

        for (var i = 0; i < IntelCardSource.FailuresBeforeLevelZero; i++) Tick(source).Gpus[0].Watts.ShouldBeNull();
        _levelZero.Calls.ShouldBe(0);

        Tick(source);
        _levelZero.Calls.ShouldBe(1);
        _igcl.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void A_card_asleep_says_so_rather_than_failing()
    {
        _igcl.Discrete().Next(card: 1000, seconds: 10);
        using var source = Source();
        _igcl.TelemetryResult = IntelCardSource.DeviceUnavailable;

        for (var i = 0; i < IntelCardSource.FailuresBeforeLevelZero + 2; i++) Tick(source).Gpus[0].Watts.ShouldBeNull();
        _levelZero.Calls.ShouldBe(0);
    }

    private IntelCardSource Source() => new(() => _igcl, () => _levelZero, _ => () => _poweredOff);

    private static SampleDraft Tick(ISensorSource source)
    {
        var draft = new SampleDraft();
        source.Contribute(draft);
        return draft;
    }

    private sealed class FakeIgcl : IIgcl
    {
        private readonly List<IgclAdapter> _adapters = [];
        private readonly Queue<IgclTelemetry> _readings = new();
        private IgclTelemetry _last;

        public int InitResult { get; set; }
        public int TelemetryResult { get; set; }
        public int TelemetryCalls { get; private set; }
        public bool Disposed { get; private set; }

        public FakeIgcl Discrete() => Adapter(integrated: false);

        public FakeIgcl Adapter(bool integrated)
        {
            _adapters.Add(new IgclAdapter(new IntPtr(_adapters.Count + 1), DiscreteGpu.IntelVendor, integrated ? 0x9A49u : A770, integrated,
                integrated ? "Intel(R) Iris(R) Xe Graphics" : "Intel(R) Arc(TM) A770 Graphics"));
            return this;
        }

        public FakeIgcl Next(double? card, double seconds, double? gpu = null)
        {
            _readings.Enqueue(new IgclTelemetry(card, gpu, seconds));
            return this;
        }

        public int Init() => InitResult;

        public int Adapters(out IgclAdapter[] adapters)
        {
            adapters = [.. _adapters];
            return 0;
        }

        public int Telemetry(IntPtr adapter, out IgclTelemetry telemetry)
        {
            TelemetryCalls++;
            if (_readings.Count > 0) _last = _readings.Dequeue();
            telemetry = _last;
            return TelemetryResult;
        }

        public void Dispose() => Disposed = true;
    }
}
