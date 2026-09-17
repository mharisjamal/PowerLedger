using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// Spec §4: sits between the sampler and the model and marks rather than throws. Out-of-range values are dropped,
/// a single-tick spike is replaced with the last good reading, and a second high reading in a row is accepted as a
/// real change of level rather than rejected forever. The seconds after an AC change are flagged so the calibration
/// learner skips them while the quality label switches at once.
/// CPU watts come from the energy meter as averages of real energy over the tick, so they are range-checked but never
/// spike-filtered: a jump from idle to turbo is a fact, not a glitch. The watts a UPS or a power supply reports are
/// treated the same way, and are the readings a ceiling matters most for, since the model takes one of them as the whole
/// machine's total. Single-threaded: the sampling loop owns it.
/// </summary>
public sealed class SampleValidator
{
    private readonly ValidatorOptions _options;
    private readonly Channel _cpu;
    private readonly Channel _gpu;
    private readonly Channel _battery;

    private bool? _lastOnBattery;
    private DateTimeOffset _transitionAt = DateTimeOffset.MinValue;

    public SampleValidator(ValidatorOptions? options = null)
    {
        _options = options ?? new ValidatorOptions();
        _cpu = new Channel(_options.MedianWindow);
        _gpu = new Channel(_options.MedianWindow);
        _battery = new Channel(_options.MedianWindow);
    }

    /// <summary>How many ticks have been marked suspect since the service started (spec §4, shown in status).</summary>
    public int SuspectCount { get; private set; }

    public Sample Validate(Sample raw)
    {
        var suspect = false;

        if (_lastOnBattery is { } previous && previous != raw.OnBattery)
        {
            _transitionAt = raw.Timestamp;
            _battery.Reset();          // a discharge rate from before the last time on mains says nothing about now
        }
        _lastOnBattery = raw.OnBattery;
        if ((raw.Timestamp - _transitionAt).TotalSeconds < _options.TransitionSeconds) suspect = true;

        var cpu = Check(raw.CpuPackageW, _options.CpuMaxW, _cpu, spikeFilter: false, ref suspect);
        var gpu = Check(raw.DGpuW, _options.GpuMaxW, _gpu, spikeFilter: true, ref suspect);
        var battery = Check(raw.BatteryRateW, _options.BatteryMaxW, _battery, spikeFilter: true, ref suspect);
        var igpu = InRange(raw.IGpuW, _options.CpuMaxW, ref suspect);

        // A UPS or a power supply gives the whole machine's draw, and the model takes it as the total, so a misread unit
        // exponent that turns 250 W into 25 kW would bank 6.94 Wh in one second as a measured, unsuspected reading. They
        // are range-checked like the rest and, like the CPU's, never spike-filtered: a jump from idle to load is a fact.
        var ups = InRange(raw.UpsOutputW, _options.UpsMaxW, ref suspect);
        var psu = InRange(raw.PsuOutputW, _options.PsuMaxW, ref suspect);

        var brightness = Fraction(raw.Brightness, ref suspect);
        var load = Fraction(raw.CpuLoad, ref suspect) ?? 0;
        var gpuLoad = Fraction(raw.DGpuLoad, ref suspect);

        if (suspect) SuspectCount++;
        return raw with
        {
            CpuPackageW = cpu,
            IGpuW = igpu,
            DGpuW = gpu,
            BatteryRateW = battery,
            Brightness = brightness,
            CpuLoad = load,
            DGpuLoad = gpuLoad,
            UpsOutputW = ups,
            PsuOutputW = psu,
            Suspect = suspect,
        };
    }

    private double? Check(double? value, double max, Channel channel, bool spikeFilter, ref bool suspect)
    {
        if (InRange(value, max, ref suspect) is not { } reading) return null;

        // The spike test engages only once the window is full: a median of one or two readings says nothing.
        var isSpike = spikeFilter
            && channel.Window.Count >= _options.MedianWindow
            && channel.Window.Median is { } median && median > 0
            && reading >= median * _options.OutlierFactor;

        if (isSpike && channel.ConsecutiveSpikes == 0)
        {
            // One high reading is a spike: replace it, and keep it out of the window so the median stays honest.
            channel.ConsecutiveSpikes = 1;
            suspect = true;
            return channel.Last;
        }

        // A second high reading in a row is a real change of level, so the old median no longer applies.
        if (isSpike) channel.Window.Reset();

        channel.ConsecutiveSpikes = 0;
        channel.Window.Add(reading);
        channel.Last = reading;
        return reading;
    }

    /// <summary>A watts reading: dropped when it is not a number, negative, or above the plausible ceiling.</summary>
    private static double? InRange(double? value, double max, ref bool suspect)
    {
        if (value is not { } reading) return null;
        if (double.IsFinite(reading) && reading >= 0 && reading <= max) return reading;
        suspect = true;
        return null;
    }

    /// <summary>A 0..1 reading: clamped when it is merely out of range, dropped when it is not a number.</summary>
    private static double? Fraction(double? value, ref bool suspect)
    {
        if (value is not { } reading) return null;
        if (!double.IsFinite(reading))
        {
            suspect = true;
            return null;
        }
        if (reading is < 0 or > 1)
        {
            suspect = true;
            return Math.Clamp(reading, 0, 1);
        }
        return reading;
    }

    private sealed class Channel(int window)
    {
        public RollingMedian Window { get; } = new(window);
        public double? Last { get; set; }
        public int ConsecutiveSpikes { get; set; }

        public void Reset()
        {
            Window.Reset();
            Last = null;
            ConsecutiveSpikes = 0;
        }
    }
}
