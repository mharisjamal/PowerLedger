using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan AE: the Apps page's ViewModel, reading Today, this week or this month off the UI thread while the page
/// shows.</summary>
public class AppsViewModelTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 16, 14, 30, 0, TimeSpan.Zero);   // a Friday
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeAppHistory _history = new();

    private AppsViewModel Model(UiThreads? threads = null) => new(_history, threads ?? UiThreads.Inline, _clock, TimeZoneInfo.Utc, English);

    private static string Rupees(decimal amount) => Money.Format(amount, "PKR", English);

    [Fact]
    public void Nothing_is_read_until_the_page_shows_then_today_at_once_and_every_minute_while_it_shows()
    {
        var model = Model();
        _history.Reads.ShouldBeEmpty();

        model.Show();

        _history.Reads.Single().Title.ShouldBe("Today");
        _history.Reads[0].From.ShouldBe(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
        model.Rows.Count.ShouldBe(8);
        model.Rows[0].Name.ShouldBe("Google Chrome");
        model.Summary.ShouldBe($"1.66 kWh, {Rupees(58.17m)} so far today");
        model.Message.ShouldBeNull();
        model.HasCost.ShouldBeTrue();
        model.Note.ShouldBeNull();

        _clock.Advance(AppsViewModel.RefreshEvery);
        _history.Reads.Count.ShouldBe(2);
        model.Hide();
        _clock.Advance(AppsViewModel.RefreshEvery * 3);
        _history.Reads.Count.ShouldBe(2);
    }

    [Fact]
    public void The_week_and_the_month_are_this_calendar_week_and_month_so_far()
    {
        var model = Model();
        model.Show();

        model.Period = AppsPeriod.Week;
        (_history.Reads[^1].From, _history.Reads[^1].To).ShouldBe((new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), Now));
        model.Summary.ShouldEndWith("so far this week");

        model.Period = AppsPeriod.Month;
        (_history.Reads[^1].From, _history.Reads[^1].To).ShouldBe((new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), Now));
        model.Summary.ShouldEndWith("so far this month");
    }

    /// <summary>A range that began before PowerLedger counted apps says from when they are counted.</summary>
    [Fact]
    public void A_range_from_before_apps_were_counted_says_since_when()
    {
        var model = Model();
        model.Show();
        model.Note.ShouldBeNull();

        model.Period = AppsPeriod.Week;
        model.Note.ShouldBe("Apps counted from today.");

        _history.Answer = range => AppLedgerTests.Day() with { CountedSince = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero) };
        model.Period = AppsPeriod.Month;
        model.Note.ShouldBe("Apps counted from 14 Oct.");

        _history.Answer = range => AppLedgerTests.Day() with { CountedSince = Now.AddMinutes(-30) };
        model.Period = AppsPeriod.Today;
        model.Note.ShouldBe("Apps counted from 2:00 PM today.");
    }

    [Fact]
    public void A_history_that_cannot_be_read_says_so()
    {
        _history.Answer = _ => null;
        var model = Model();
        model.Show();

        model.Rows.ShouldBeEmpty();
        model.Message.ShouldBe("Apps can't be read right now. They come back when the service is running.");
        model.Summary.ShouldBe("");
    }

    /// <summary>Plan AE's empty state: hours before 0.12.0, or its first minutes.</summary>
    [Fact]
    public void Before_apps_have_been_watched_a_few_minutes_it_says_when_they_show()
    {
        _history.Answer = _ => AppsRange.None(Now.AddHours(-14), Now, null, "PKR");
        var model = Model();
        model.Show();

        model.Rows.ShouldBeEmpty();
        model.Message.ShouldBe("Apps show here once PowerLedger has watched them for a few minutes.");
    }

    [Fact]
    public void Without_a_tariff_there_is_no_cost_to_show()
    {
        _history.Answer = _ => AppLedgerTests.Day(currency: null);
        var model = Model();
        model.Show();

        model.HasCost.ShouldBeFalse();
        model.Summary.ShouldBe("1.66 kWh so far today");
    }

    [Fact]
    public void Costs_partly_in_an_earlier_currency_say_so()
    {
        _history.Answer = _ => AppLedgerTests.Day() with { CostIsPartial = true };
        var model = Model();
        model.Show();

        model.Note.ShouldBe("Costs leave out energy priced in an earlier currency.");
    }

    /// <summary>A line opens alone, stays open through the next read and closes again; opening reads nothing.</summary>
    [Fact]
    public void A_line_opens_alone_stays_open_through_a_read_and_closes_again()
    {
        var model = Model();
        model.Show();
        var (chrome, terminal) = (model.Rows[0].Key, model.Rows[2].Key);

        model.Toggle.Execute(chrome);
        model.Rows.Where(r => r.IsOpen).Select(r => r.Key).ShouldBe([chrome]);
        model.Toggle.Execute(terminal);
        model.Rows.Where(r => r.IsOpen).Select(r => r.Key).ShouldBe([terminal]);
        _history.Reads.Count.ShouldBe(1);

        _clock.Advance(AppsViewModel.RefreshEvery);
        model.Rows.Where(r => r.IsOpen).Select(r => r.Key).ShouldBe([terminal]);

        model.Toggle.Execute(terminal);
        model.Rows.ShouldAllBe(r => !r.IsOpen);
    }

    [Fact]
    public void A_read_a_newer_one_overtook_is_dropped()
    {
        var pending = new List<Action>();
        var model = Model(new UiThreads(action => action(), pending.Add));
        model.Show();
        model.Period = AppsPeriod.Week;

        pending[1]();
        pending[0]();

        model.Summary.ShouldEndWith("so far this week");
    }

    [Fact]
    public void A_read_that_throws_reads_as_one_that_could_not_be_read()
    {
        _history.Answer = _ => throw new InvalidOperationException("locked");
        var model = Model();
        model.Show();

        model.Message.ShouldBe("Apps can't be read right now. They come back when the service is running.");
    }
}
