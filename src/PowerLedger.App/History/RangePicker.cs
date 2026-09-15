using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerLedger.App;

/// <summary>The ranges the history screens offer (spec §9: today, 7 days, 30 days and custom, and the months a report wants).</summary>
internal enum RangeChoice
{
    Today,
    SevenDays,
    ThirtyDays,
    ThisMonth,
    LastMonth,
    Custom,
}

/// <summary>
/// Which range a history screen shows. The custom days start as the last week and change the range only while Custom is
/// chosen; a date the picker clears keeps the last day.
/// </summary>
internal sealed class RangePicker : ObservableObject
{
    private RangeChoice _choice;
    private DateTime? _from;
    private DateTime? _to;

    public RangePicker(RangeChoice choice, DateOnly today)
    {
        _choice = choice;
        _from = today.AddDays(-6).ToDateTime(TimeOnly.MinValue);
        _to = today.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>Raised when the range to show has changed.</summary>
    public event Action? Changed;

    public RangeChoice Choice
    {
        get => _choice;
        set
        {
            if (!SetProperty(ref _choice, value)) return;
            OnPropertyChanged(nameof(IsCustom));
            Changed?.Invoke();
        }
    }

    public bool IsCustom => Choice == RangeChoice.Custom;

    /// <summary>The custom range's first day, as the date picker gives it.</summary>
    public DateTime? From { get => _from; set => SetDay(ref _from, value, nameof(From)); }

    /// <summary>The custom range's last day.</summary>
    public DateTime? To { get => _to; set => SetDay(ref _to, value, nameof(To)); }

    public DateRange Resolve(DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture) => Choice switch
    {
        RangeChoice.Today => Ranges.Today(now, zone, culture),
        RangeChoice.SevenDays => Ranges.LastDays(7, now, zone, culture),
        RangeChoice.ThirtyDays => Ranges.LastDays(30, now, zone, culture),
        RangeChoice.ThisMonth => Ranges.ThisMonth(now, zone, culture),
        RangeChoice.LastMonth => Ranges.LastMonth(now, zone, culture),
        _ => Ranges.Days(Day(_from, now, zone), Day(_to, now, zone), now, zone, culture),
    };

    private void SetDay(ref DateTime? field, DateTime? value, string name)
    {
        if (value is null)
        {
            OnPropertyChanged(name);   // the picker shows the last day again
            return;
        }
        if (SetProperty(ref field, value.Value.Date, name) && IsCustom) Changed?.Invoke();
    }

    private static DateOnly Day(DateTime? day, DateTimeOffset now, TimeZoneInfo zone) => day is { } d ? DateOnly.FromDateTime(d) : Ranges.LocalDay(now, zone);
}
