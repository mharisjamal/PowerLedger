using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App;

/// <summary>
/// A new tariff (spec §8, §9): the price per kWh, its ISO 4217 currency, and the first day it applies, from that day's
/// local midnight. The day may be in the past; energy already recorded is repriced from then (spec §7). What was typed is
/// checked before anything is sent, and the form says what came of it.
/// </summary>
internal sealed class TariffForm : ObservableObject
{
    private const decimal MaxPrice = 1_000_000m;

    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private string _price = "";
    private string _currency;
    private DateTime? _from;
    private string? _message;

    public TariffForm(IServiceLink link, UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture, string regionCurrency)
    {
        _link = link;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        _currency = regionCurrency;
        _from = Ranges.LocalDay(clock.GetUtcNow(), zone).ToDateTime(TimeOnly.MinValue);
        Save = new RelayCommand(() => _ = SaveAsync());
    }

    /// <summary>Raised on the UI thread once the service has taken a tariff.</summary>
    public event Action? Saved;

    public string Price
    {
        get => _price;
        set
        {
            if (SetProperty(ref _price, value)) OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public string Currency { get => _currency; set => SetProperty(ref _currency, value); }

    /// <summary>The first day the price applies.</summary>
    public DateTime? From { get => _from; set => SetProperty(ref _from, value); }

    /// <summary>What came of the last save, or why it could not be sent.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>Nothing typed: the wizard takes that as "later".</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Price);

    public ICommand Save { get; }

    /// <summary>Sends the tariff typed, and says whether the service took it.</summary>
    public async Task<bool> SaveAsync()
    {
        if (Read(out var problem) is not { } tariff)
        {
            Message = problem;
            return false;
        }
        Message = "Saving…";
        var result = await _link.SetTariffAsync(tariff.Price, tariff.Currency, tariff.From).ConfigureAwait(false);
        _threads.Post(() =>
        {
            Message = result.Succeeded
                ? $"Saved: {Money.Rate(tariff.Price, tariff.Currency, _culture)} / kWh from {tariff.From.ToString("d MMM yyyy", _culture)}."
                : result.Problem;
            if (result.Succeeded) Saved?.Invoke();
        });
        return result.Succeeded;
    }

    /// <summary>The tariff typed, or null and why it can't be sent.</summary>
    internal (decimal Price, string Currency, DateTimeOffset From)? Read(out string? problem)
    {
        const NumberStyles plain = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowDecimalPoint;
        if (!decimal.TryParse(Price, plain, _culture, out var price) && !decimal.TryParse(Price, plain, CultureInfo.InvariantCulture, out price))
        {
            problem = "Type the price per kWh as a number, like 0.17.";
            return null;
        }
        if (price > MaxPrice)
        {
            problem = "The price per kWh must be between 0 and 1,000,000.";
            return null;
        }
        var code = Currency.Trim().ToUpperInvariant();
        if (code.Length != 3 || !code.All(c => c is >= 'A' and <= 'Z'))
        {
            problem = "The currency is a three-letter code, like USD or EUR.";
            return null;
        }
        var day = From is { } chosen ? DateOnly.FromDateTime(chosen) : Ranges.LocalDay(_clock.GetUtcNow(), _zone);
        problem = null;
        return (price, code, Ranges.Midnight(day, _zone));
    }
}
