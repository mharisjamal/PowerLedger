using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// Spec §4: sits between the sampler and the model and marks rather than throws. Out-of-range values are dropped,
/// spikes are replaced with the last good reading, a counter wrap drops that tick's CPU value, and the seconds
/// after an AC change are flagged so the calibration learner skips them while the quality label switches at once.
/// Single-threaded: the sampling loop owns it.
/// </summary>
public sealed class SampleValidator(ValidatorOptions? options = null)
{
    private readonly ValidatorOptions _options = options ?? new ValidatorOptions();
    private readonly RollingMedian _cpu = new((options ?? new ValidatorOptions()).MedianWindow);
    private readonly RollingMedian _gpu = new((options ?? new ValidatorOptions()).MedianWindow);
    private readonly RollingMedian _battery = new((options ?? new ValidatorOptions()).MedianWindow);

    private double? _lastCpu;
    private double? _lastGpu;
    private double? _lastBattery;
    private bool? _lastOnBattery;
    private DateTimeOffset _transitionAt = DateTimeOffset.MinValue;

    /// <summary>How many ticks have been marked suspect since the service started (spec §4, shown in status).</summary>
    public int SuspectCount { get; private set; }

    public Sample Validate(Sample raw)
    {
        var suspect = false;

        if (_lastOnBattery is { } previous && previous != raw.OnBattery) _transitionAt = raw.Timestamp;
        _lastOnBattery = raw.OnBattery;
        if ((raw.Timestamp - _transitionAt).TotalSeconds < _options.TransitionSeconds) suspect = true;

        // A RAPL counter wrap shows up as a negative delta, which the driver surfaces as a negative reading.
        var cpu = raw.CpuPackageW is { } candidate && candidate < 0 && _lastCpu is not null ? Drop(ref suspect) : raw.CpuPackageW;
        cpu = Check(cpu, _options.CpuMaxW, _cpu, ref _lastCpu, ref suspect);
        var gpu = Check(raw.DGpuW, _options.GpuMaxW, _gpu, ref _lastGpu, ref suspect);
        var battery = Check(raw.BatteryRateW, _options.BatteryMaxW, _battery, ref _lastBattery, ref suspect);

        var brightness = Fraction(raw.Brightness, ref suspect);
        var load = Fraction(raw.CpuLoad, ref suspect) ?? 0;
        var gpuLoad = Fraction(raw.DGpuLoad, ref suspect);

        if (suspect) SuspectCount++;
        return raw with
        {
            CpuPackageW = cpu,
            DGpuW = gpu,
            BatteryRateW = battery,
            Brightness = brightness,
            CpuLoad = load,
            DGpuLoad = gpuLoad,
            Suspect = suspect,
        };
    }

    private double? Check(double? value, double max, RollingMedian window, ref double? last, ref bool suspect)
    {
        if (value is not { } reading) return null;
        if (!double.IsFinite(reading) || reading < 0 || reading > max) return Drop(ref suspect);

        // The spike test engages only once the window is full: a median of one or two readings says nothing,
        // and rejecting against it would throw away a genuine jump from idle to load in the first seconds.
        if (window.Count >= _options.MedianWindow && window.Median is { } median && median > 0 && reading >= median * _options.OutlierFactor)
        {
            suspect = true;
            return last;                      // the spike never enters the window, so the median stays honest
        }

        window.Add(reading);
        last = reading;
        return reading;
    }

    private static double? Drop(ref bool suspect)
    {
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
}
