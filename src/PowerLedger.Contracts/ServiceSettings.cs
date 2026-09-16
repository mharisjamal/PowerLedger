namespace PowerLedger.Contracts;

/// <summary>
/// What the App may change over the pipe (spec §8): the machine profile, the idle threshold, the sample interval and
/// how long history is kept. Tariffs travel on their own message. The service accepts nothing until
/// <see cref="Validate"/> passes, and nothing here names a file or a command. Init-only properties with initialisers,
/// so settings stored by an older version take the default for anything added since.
/// </summary>
public sealed record ServiceSettings
{
    public const int MinIdleThresholdSeconds = 60;
    public const int MaxIdleThresholdSeconds = 1800;
    public const int MinSampleIntervalSeconds = 1;
    public const int MaxSampleIntervalSeconds = 5;
    public const int MinRawRetentionHours = 24;
    public const int MaxRawRetentionHours = 168;
    public const int MinHistoryRetentionYears = 1;
    public const int MaxHistoryRetentionYears = 5;

    public MachineProfile Profile { get; init; } = MachineProfile.DefaultLaptop;

    /// <summary>Seconds without input after which the user counts as idle (spec §6).</summary>
    public int IdleThresholdSeconds { get; init; } = 300;

    /// <summary>Seconds between samples (spec §4).</summary>
    public int SampleIntervalSeconds { get; init; } = 1;

    /// <summary>Hours of one-second rows to keep (spec §7).</summary>
    public int RawRetentionHours { get; init; } = 48;

    /// <summary>Years of one-minute rows to keep; hour rows are kept for ever (spec §7).</summary>
    public int HistoryRetentionYears { get; init; } = 2;

    public static ServiceSettings Default { get; } = new();

    /// <summary>Null when every value is acceptable; otherwise the first problem, in words the App can show.</summary>
    public string? Validate()
    {
        if (Profile is null) return "The machine profile is missing.";
        if (IdleThresholdSeconds is < MinIdleThresholdSeconds or > MaxIdleThresholdSeconds)
            return $"The idle threshold must be between {MinIdleThresholdSeconds} and {MaxIdleThresholdSeconds} seconds.";
        if (SampleIntervalSeconds is < MinSampleIntervalSeconds or > MaxSampleIntervalSeconds)
            return $"The sample interval must be between {MinSampleIntervalSeconds} and {MaxSampleIntervalSeconds} seconds.";
        if (RawRetentionHours is < MinRawRetentionHours or > MaxRawRetentionHours)
            return $"Second-by-second history must be kept between {MinRawRetentionHours} and {MaxRawRetentionHours} hours.";
        if (HistoryRetentionYears is < MinHistoryRetentionYears or > MaxHistoryRetentionYears)
            return $"Minute-by-minute history must be kept between {MinHistoryRetentionYears} and {MaxHistoryRetentionYears} years.";
        return ValidateProfile(Profile);
    }

    private static string? ValidateProfile(MachineProfile p)
    {
        if (!Enum.IsDefined(p.Chassis)) return "The chassis is not one PowerLedger knows.";
        if (!Enum.IsDefined(p.PsuTier)) return "The power supply rating is not one PowerLedger knows.";
        if (p.RamSticks is < 1 or > 32) return "Memory must be between 1 and 32 sticks.";
        if (p.SsdCount is < 0 or > 32 || p.HddCount is < 0 or > 32) return "Drive counts must be between 0 and 32.";
        if (p.FanCount is < 0 or > 32) return "The fan count must be between 0 and 32.";
        if (p.ExternalMonitors is < 0 or > 16) return "External monitors must be between 0 and 16.";
        if (!InRange(p.ExtrasWatts, 0, 1000)) return "Extras must be between 0 and 1000 W.";
        if (!InRange(p.MonitorWatts, 0, 500)) return "A monitor must draw between 0 and 500 W.";
        if (p.DisplayDiagonalInches != 0 && !InRange(p.DisplayDiagonalInches, 7, 50))
            return "The panel size must be 0 for none, or between 7 and 50 inches.";
        if (p.CpuTdpOverrideW is { } cpu && !InRange(cpu, 1, 1000)) return "The processor's rated power must be between 1 and 1000 W.";
        if (p.GpuTdpOverrideW is { } gpu && !InRange(gpu, 1, 1500)) return "The graphics card's rated power must be between 1 and 1500 W.";
        return ValidateMonitors(p.Monitors);
    }

    private static string? ValidateMonitors(IReadOnlyList<MonitorChoice>? monitors)
    {
        if (monitors is null) return "The monitor choices are missing.";
        if (monitors.Count > 16) return "At most 16 monitors can be listed.";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var monitor in monitors)
        {
            if (monitor?.Key is not { Length: >= 1 and <= 200 }) return "A monitor's key must be between 1 and 200 characters.";
            if (monitor.Watts is { } watts && !InRange(watts, 0, 500)) return "A monitor must draw between 0 and 500 W.";
            if (!keys.Add(monitor.Key)) return "Each monitor can be listed once.";
        }
        return null;
    }

    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}
