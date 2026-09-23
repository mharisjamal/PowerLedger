using System.Text.RegularExpressions;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Service.Sharing;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// Answers pipe requests (spec §8). Everything a client sends is range-checked here, and nothing it can say names a
/// file or runs a command (spec §11). Anything that changes what the loop is doing goes to the loop as a command, and
/// the reply waits until the loop has done it. Data sharing's requests go to the sharing worker the same way, except the
/// App's usage counts and crashes: those are acknowledged once queued, so the App never sends them twice. A sharing request
/// the App is told got no answer in time is never carried out.
/// </summary>
internal sealed partial class PipeHandler(
    LoopCommands commands, StatusBoard board, MonitorBoard monitors, ServiceSignals signals, TariffRepository tariffs, TimeProvider clock,
    SharingCommands sharing)
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
                monitors.Report(request.Monitors, request.Power, request.Displays);
                return new OkReply(request.Id);
            case SetConsentRequest request:
                if (request.Consent is null) return new ErrorReply(request.Id, "The choices are missing.");
                if (request.Consent.Validate() is { } refused) return new ErrorReply(request.Id, refused);
                return await ShareAsync(new SetConsentCommand(request.Id, request.Consent), cancel).ConfigureAwait(false);
            case ReportUsageRequest request:
                if (request.Counts is null) return new ErrorReply(request.Id, "The counts are missing.");
                if (request.Counts.Validate() is { } badCounts) return new ErrorReply(request.Id, badCounts);
                return Queue(new ReportUsageCommand(request.Id, request.Counts));
            case ReportCrashRequest request:
                if (request.Crash is null) return new ErrorReply(request.Id, "The crash is missing.");
                if (request.Crash.Validate() is { } badCrash) return new ErrorReply(request.Id, badCrash);
                return Queue(new ReportCrashCommand(request.Id, request.Crash));
            case PreviewUploadRequest request:
                return await ShareAsync(new PreviewCommand(request.Id), cancel).ConfigureAwait(false);
            case SendNowRequest request:
                return await ShareAsync(new SendNowCommand(request.Id), cancel).ConfigureAwait(false);
            case DeleteMyDataRequest request:
                return await ShareAsync(new DeleteMyDataCommand(request.Id), cancel).ConfigureAwait(false);
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

    /// <summary>
    /// Hands the request to the sharing worker and answers with what it did, or says it didn't answer in time. That is said
    /// only of a request the worker hadn't taken, which it then never carries out. One it took just before the limit is
    /// waited for: the worker answers within <see cref="SharingWorker.AppWait"/> of the request being queued.
    /// </summary>
    private async Task<PipeMessage> ShareAsync(SharingCommand command, CancellationToken cancel)
    {
        sharing.TryQueue(command);                                  // one it can't take is answered no at once
        try
        {
            return await command.Reply.WaitAsync(LoopTimeout, clock, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (command.TryGiveUp(NoAnswer)) return new ErrorReply(command.Id, NoAnswer);
        }
        catch (OperationCanceledException)
        {
            command.TryGiveUp(SharingCommands.Stopping);           // nobody is left to hear the answer
            throw;
        }
        return await command.Reply.WaitAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Hands the request to the sharing worker and acknowledges it at once.</summary>
    private PipeMessage Queue(SharingCommand command) =>
        sharing.TryQueue(command) ? new OkReply(command.Id) : new ErrorReply(command.Id, command.Reply.Result.Message);

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
