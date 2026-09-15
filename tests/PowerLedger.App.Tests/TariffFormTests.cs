using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public class TariffFormTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 14, 30, 0, TimeSpan.Zero);
    private readonly FakeLink _link = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public TariffFormTests() => _link.Connect(true);

    private TariffForm Form(string culture = "en-US") => new(_link, UiThreads.Inline, _clock, TimeZoneInfo.Utc, CultureInfo.GetCultureInfo(culture), "USD");

    private (decimal Price, string Currency, DateTimeOffset? From) Sent => ((decimal, string, DateTimeOffset?))_link.Writes.Single();

    [Fact]
    public async Task A_price_goes_to_the_service_from_its_days_midnight()
    {
        var form = Form();
        form.Currency.ShouldBe("USD");
        form.From.ShouldBe(new DateTime(2026, 9, 15));
        form.Price = "0.17";
        form.From = new DateTime(2026, 9, 1);

        (await form.SaveAsync()).ShouldBeTrue();

        Sent.ShouldBe((0.17m, "USD", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));
        form.Message.ShouldBe("Saved: $0.17 / kWh from 1 Sep 2026.");
    }

    [Theory]
    [InlineData("de-DE", "0,17")]
    [InlineData("de-DE", "0.17")]
    [InlineData("en-US", " 0.17 ")]
    public async Task A_price_reads_in_the_users_culture_or_with_a_point(string culture, string typed)
    {
        var form = Form(culture);
        form.Price = typed;
        (await form.SaveAsync()).ShouldBeTrue();
        Sent.Price.ShouldBe(0.17m);
    }

    [Theory]
    [InlineData("abc", "EUR", "Type the price per kWh as a number, like 0.17.")]
    [InlineData("-1", "EUR", "Type the price per kWh as a number, like 0.17.")]
    [InlineData("2000000", "EUR", "The price per kWh must be between 0 and 1,000,000.")]
    [InlineData("0.17", "EU", "The currency is a three-letter code, like USD or EUR.")]
    [InlineData("0.17", "E1R", "The currency is a three-letter code, like USD or EUR.")]
    public async Task What_cannot_be_sent_is_said_and_nothing_is_sent(string price, string currency, string message)
    {
        var form = Form();
        form.Price = price;
        form.Currency = currency;
        (await form.SaveAsync()).ShouldBeFalse();
        form.Message.ShouldBe(message);
        _link.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_currency_is_sent_in_capitals()
    {
        var form = Form();
        form.Price = "0.25";
        form.Currency = " eur ";
        await form.SaveAsync();
        Sent.Currency.ShouldBe("EUR");
    }

    [Fact]
    public async Task The_services_refusal_is_shown()
    {
        _link.Answer = new WriteResult("A tariff can start at any time from 2000 until tomorrow.");
        var form = Form();
        form.Price = "0.17";
        (await form.SaveAsync()).ShouldBeFalse();
        form.Message.ShouldBe("A tariff can start at any time from 2000 until tomorrow.");
    }

    [Fact]
    public async Task A_saved_tariff_is_announced()
    {
        var form = Form();
        var saved = 0;
        form.Saved += () => saved++;
        form.Price = "0.17";
        await form.SaveAsync();
        saved.ShouldBe(1);
        form.IsEmpty.ShouldBeFalse();
    }
}
