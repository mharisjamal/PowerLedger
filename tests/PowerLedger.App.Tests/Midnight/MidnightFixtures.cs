using System.Globalization;
using System.Windows;
using PowerLedger.Core;

namespace PowerLedger.App.Tests;

/// <summary>What the Midnight tests draw: a Tuesday's readings and the chart built from them, and the palette put in place.</summary>
internal static class MidnightFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static ResourceDictionary? _palette;

    /// <summary>Today's range in UTC, as the Dashboard's 1D pill reads it.</summary>
    public static DateRange Today() => Ranges.Today(Now, TimeZoneInfo.Utc, English);

    /// <summary>Today's chart in watts: asleep until 07:30, a working morning, an idle patch, a peak at 14:00, on to now.</summary>
    public static ChartModel DayChart() => Charts.Build(Today(), DaySeries(Today().From), ChartUnit.Watts, TimeZoneInfo.Utc, English);

    /// <summary>Five-minute aggregates from midnight to now, with a sleep gap and a shape a person recognises as a day.</summary>
    public static IReadOnlyList<Aggregate> DaySeries(DateTimeOffset dayStart)
    {
        var series = new List<Aggregate>();
        var seed = 7;
        double Noise()
        {
            seed = (seed * 9301 + 49297) % 233280;
            return seed / 233280.0 - 0.5;
        }
        for (var i = 0; i < 175; i++)
        {
            var total = i switch
            {
                < 90 => 0,
                < 108 => 14 + Noise() * 3,
                < 150 => 38 + 10 * Math.Sin((i - 108) / 42.0 * Math.PI) + Noise() * 8,
                < 159 => 15 + Noise() * 2,
                168 => 68,
                _ => 44 + Noise() * 6,
            };
            var start = dayStart.AddMinutes(5 * i);
            series.Add(total <= 0
                ? Aggregate.Empty(start) with { GapSeconds = 300 }
                : Aggregate.Empty(start) with
                {
                    CpuWh = total * 0.45 / 12, GpuWh = total * 0.11 / 12, DisplayWh = 4 / 12.0, RestWh = (total * 0.44 - 4) / 12,
                    EnergyWh = total / 12, OnSeconds = 300,
                });
        }
        return series;
    }

    /// <summary>Puts Midnight's palette for <paramref name="theme"/> in front of the application's dictionaries, where the App keeps its palette,
    /// taking out the one this put there before. TEMPORARY: the copies under Temp\ until plan-o/f's palettes land.</summary>
    public static void UsePalette(Theme theme)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_palette is not null) merged.Remove(_palette);
        _palette = TempPalette.Load(theme);
        merged.Insert(0, _palette);
    }
}
