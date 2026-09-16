using System.Text.RegularExpressions;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// Answers pipe requests (spec §8). Everything a client sends is range-checked here, and nothing it can say names a
/// file or runs a command (spec §11). Anything that changes what the loop is doing goes to the loop as a command, and
/// the reply waits until the loop has done it.
/// </summary>
internal sealed partial class PipeHandler(
    LoopCommands commands, StatusBoard board, MonitorBoard monitors, ServiceSignals signals, TariffRepository tariffs, TimeProvider clock)
{
    /// <summary>How long a request waits for the loop before the client is told it did not answer.</summary>
    public static readonly TimeSpan LoopTimeout = TimeSpan.FromSeconds(10);

    internal const string Starting = "The service is still starting.";
    internal const string NoAnswer = "The service did not answer in time.";

    private const decimal MaxPricePerKwh = 1_000_000m;   // room for currencies with small units
    private static readonly DateTimeOffset EarliestTariff = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <param name="client">The connection the request came on, so idle reports stay per client.</param>
    public async Task<PipeMessage> HandleAsync(PipeMessage message, string client, CancellationToken cancel)
    {
        switch (message)
        {
            case SubscribeRequest request:
                return new OkReply(request.Id);
            case GetStatusRequest request:
                return board.Status is { } status ? new StatusReply(request.Id, status) : new ErrorReply(request.Id, Starting);
            case GetSettingsRequest request:
                return board.Settings is { } settings ? new SettingsReply(request.Id, settings) : new ErrorReply(request.Id, Starting);
            case SetSettingsRequest request:
                if (request.Settings is null) return new ErrorReply(request.Id, "The settings are missing.");
                if (request.Settings.Validate() is { } problem) return new ErrorReply(request.Id, problem);
                return await RunAsync(request.Id, new ApplySettingsCommand(request.Settings), cancel).ConfigureAwait(false);
            case SetTariffRequest request:
                return SetTariff(request);
            case ResetCalibrationRequest request:
                return await RunAsync(request.Id, new ResetCalibrationCommand(), cancel).ConfigureAwait(false);
            case ReportActivityRequest request:
                if (!double.IsFinite(request.IdleSeconds) || request.IdleSeconds < 0)
                    return new ErrorReply(request.Id, "Idle time must be a number of seconds, zero or more.");
                signals.ReportIdle(client, request.IdleSeconds);
                return new OkReply(request.Id);
            case ReportBrightnessRequest request:
                if (request.Validate() is { } invalid) return new ErrorReply(request.Id, invalid);
                monitors.Report(request.Monitors, request.Power);
                return new OkReply(request.Id);
            case PipeRequest request:
                return new ErrorReply(request.Id, "The service does not handle that request.");
            default:
                return new ErrorReply(null, "Only requests may be sent to the service.");
        }
    }

    private async Task<PipeMessage> RunAsync(long id, LoopCommand command, CancellationToken cancel)
    {
        try
        {
            await commands.SendAsync(command).WaitAsync(LoopTimeout, clock, cancel).ConfigureAwait(false);
            return new OkReply(id);
        }
        catch (TimeoutException)
        {
            return new ErrorReply(id, NoAnswer);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new ErrorReply(id, error.Message);
        }
    }

    private PipeMessage SetTariff(SetTariffRequest request)
    {
        if (request.PricePerKwh is < 0 or > MaxPricePerKwh) return new ErrorReply(request.Id, "The price per kWh must be zero or more.");
        if (request.Currency is null || !CurrencyCode().IsMatch(request.Currency))
            return new ErrorReply(request.Id, "The currency must be a three-letter ISO 4217 code such as USD.");
        var now = clock.GetUtcNow();
        var from = request.EffectiveFrom ?? now;
        if (from < EarliestTariff || from > now.AddDays(1)) return new ErrorReply(request.Id, "A tariff can start at any time from 2000 until tomorrow.");
        try
        {
            tariffs.Add(new Tariff(from, request.PricePerKwh, request.Currency));
            return new OkReply(request.Id);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            return new ErrorReply(request.Id, $"The tariff could not be saved: {error.Message}");
        }
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();
}
