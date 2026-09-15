using System.Globalization;
using Shouldly;

namespace PowerLedger.App.Tests;

public class RangePickerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly DateOnly Today = new(2026, 9, 8);

    [Theory]
    [InlineData("Today", "Today")]
    [InlineData("SevenDays", "Last 7 days")]
    [InlineData("ThirtyDays", "Last 30 days")]
    [InlineData("ThisMonth", "September 2026")]
    [InlineData("LastMonth", "August 2026")]
    [InlineData("Custom", "2 Sep – 8 Sep 2026")]
    public void Each_choice_resolves_to_its_range(string choice, string title)
        => new RangePicker(Enum.Parse<RangeChoice>(choice), Today).Resolve(Now, TimeZoneInfo.Utc, English).Title.ShouldBe(title);

    [Fact]
    public void Custom_days_change_the_range_only_while_custom_is_chosen()
    {
        var picker = new RangePicker(RangeChoice.Today, Today);
        var changes = 0;
        picker.Changed += () => changes++;

        picker.From = new DateTime(2026, 9, 1);
        changes.ShouldBe(0);
        picker.Choice = RangeChoice.Custom;
        changes.ShouldBe(1);
        picker.IsCustom.ShouldBeTrue();
        picker.To = new DateTime(2026, 9, 3);
        changes.ShouldBe(2);
        picker.Resolve(Now, TimeZoneInfo.Utc, English).Title.ShouldBe("1 Sep – 3 Sep 2026");
    }

    [Fact]
    public void A_cleared_date_keeps_the_last_day()
    {
        var picker = new RangePicker(RangeChoice.Custom, Today);
        picker.From = null;
        picker.From.ShouldBe(new DateTime(2026, 9, 2));
    }
}
