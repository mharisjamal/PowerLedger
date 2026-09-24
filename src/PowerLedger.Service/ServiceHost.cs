using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using PowerLedger.Contracts;
using PowerLedger.Sensors;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Service.Sharing;
using PowerLedger.Storage;
using Serilog;

namespace PowerLedger.Service;

/// <summary>The database the service owns for its lifetime (Plan A: one per process), and what start-up had to do to get it.</summary>
internal sealed record OpenedDatabase(SqliteDatabase Database, string? Notice)
{
    public static OpenedDatabase Open(string path, DateTimeOffset now, string? folderNotice)
    {
        var (database, notice) = DatabaseOpener.Open(path, now);
        var notes = string.Join(" ", new[] { folderNotice, notice }.Where(n => n is not null));
        return new OpenedDatabase(database, notes.Length > 0 ? notes : null);
    }
}

/// <summary>Builds the service's host: configuration, logging, the database and every component, wired once.</summary>
internal static class ServiceHost
{
    public const string ServiceName = "PowerLedger";

    /// <summary>Configuration key, "--pipe" on the command line, that renames the pipe for a development run.</summary>
    public const string PipeKey = "pipe";

    /// <exception cref="UntrustedDataDirectoryException">The data folder belongs to another account.</exception>
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var asService = WindowsServiceHelpers.IsWindowsService();
        var paths = ServicePaths.From(builder.Configuration);
        var folderNotice = DataDirectory.Prepare(paths, enforce: asService, DateTimeOffset.UtcNow);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.Logs, "service-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5 * 1024 * 1024, rollOnFileSizeLimit: true)
            .CreateLogger();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);
        if (asService)
        {
            builder.Logging.AddEventLog(settings => settings.SourceName = ServiceName);
            builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Warning);
        }
        else
        {
            builder.Logging.AddConsole();
        }

        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        if (asService) builder.Services.AddSingleton<IHostLifetime, PowerLedgerServiceLifetime>();

        var pipeName = builder.Configuration[PipeKey] is { Length: > 0 } name ? name : PipeProtocol.PipeName;
        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ServiceSignals>();
        services.AddSingleton<ShutdownSignal>();
        services.AddSingleton<LoopCommands>();
        services.AddSingleton<LiveFeed>();
        services.AddSingleton<StatusBoard>();
        services.AddSingleton(provider => new MonitorBoard(MonitorCatalogue.Shipped, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => OpenedDatabase.Open(paths.Database, provider.GetRequiredService<TimeProvider>().GetUtcNow(), folderNotice));
        services.AddSingleton(provider => provider.GetRequiredService<OpenedDatabase>().Database);
        services.AddSingleton(provider => new TariffRepository(provider.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton(provider => LoopEnvironmentFor(provider, asService));
        services.AddSingleton(paths);                           // Program writes the service's crash files under it
        services.AddSingleton<SharingCommands>();
        services.AddSingleton<ISharingClient>(_ => new SharingClient(SharingEndpoint.Resolve()));
        services.AddSingleton(_ => new NoticeHub());
        services.AddSingleton(provider =>
        {
            var log = provider.GetRequiredService<ILogger<HouseholdWorker>>();
            return new HouseholdEnvironment(
                new WindowsDiscovery(log), new WindowsNetworkCategory(log),
                new RelayClient(SharingEndpoint.Resolve(), provider.GetRequiredService<TimeProvider>()), IPAddress.IPv6Any);
        });
        services.AddSingleton(provider => new HouseholdWorker(
            provider.GetRequiredService<SqliteDatabase>(), provider.GetRequiredService<StatusBoard>(), provider.GetRequiredService<NoticeHub>(),
            provider.GetRequiredService<HouseholdEnvironment>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<HouseholdWorker>>()));
        services.AddSingleton<IHouseholdRequests>(provider => provider.GetRequiredService<HouseholdWorker>());
        services.AddSingleton<PipeHandler>();
        services.AddHostedService<PowerNotifications>();
        services.AddSingleton<SamplingLoop>();                  // also resolved by Program and the lifetime, to see whether it failed
        services.AddHostedService(provider => provider.GetRequiredService<SamplingLoop>());
        services.AddHostedService(provider => new SharingWorker(
            provider.GetRequiredService<SqliteDatabase>(), provider.GetRequiredService<StatusBoard>(), provider.GetRequiredService<SharingCommands>(),
            provider.GetRequiredService<ISharingClient>(), SharingEnvironment.For(paths), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<SharingWorker>>()));
        services.AddHostedService(provider => provider.GetRequiredService<HouseholdWorker>());
        services.AddHostedService(provider => new PipeServer(
            provider.GetRequiredService<PipeHandler>(), provider.GetRequiredService<LiveFeed>(),
            provider.GetRequiredService<ServiceSignals>(), provider.GetRequiredService<ILogger<PipeServer>>(), pipeName,
            provider.GetRequiredService<NoticeHub>()));
        return builder.Build();
    }

    /// <summary>
    /// The owner's tick for reading a power supply over USB, as the loop last put it in force. The sensor thread asks it
    /// before every read, and the loop replaces its settings whenever the App applies new ones, so the answer comes from
    /// the status board, which is written for any thread to read. Until the loop has published any settings, and once
    /// this sensor set has been retired, nothing at all is sent to the device.
    /// </summary>
    internal static Func<bool> PowerSupplySwitch(StatusBoard board, CancellationToken retired)
        => () => !retired.IsCancellationRequested && board.Settings?.Profile.ReadPowerSupply == true;

    private static LoopEnvironment LoopEnvironmentFor(IServiceProvider provider, bool asService)
    {
        var signals = provider.GetRequiredService<ServiceSignals>();
        var shutdown = provider.GetRequiredService<ShutdownSignal>();
        var monitors = provider.GetRequiredService<MonitorBoard>();
        var board = provider.GetRequiredService<StatusBoard>();
        // In session 0 input is invisible, so idle time comes from the App; a console run reads its own session.
        Func<double?>? idle = asService ? signals.UserIdleSeconds : null;
        return new LoopEnvironment(
            Sensors: retired => new MachineSensorSet(MachineSensors.Create(
                () => signals.DisplayOn, () => signals.SessionLocked, idle,
                monitorsDetected: found => monitors.Detected(found, retired),
                readPowerSupply: PowerSupplySwitch(board, retired))),
            Inventory: HardwareInventory.Detect,
            SystemUptime: () => TimeSpan.FromMilliseconds(Environment.TickCount64),
            SystemShuttingDown: () => shutdown.SystemShuttingDown,
            DatabaseNotice: provider.GetRequiredService<OpenedDatabase>().Notice);
    }
}
