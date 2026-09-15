using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PowerLedger.Service;

/// <summary>Set when the service control manager announces a system shutdown, so the session ends as a shutdown, not a stop.</summary>
internal sealed class ShutdownSignal
{
    private volatile bool _shuttingDown;

    public bool SystemShuttingDown
    {
        get => _shuttingDown;
        set => _shuttingDown = value;
    }
}

/// <summary>
/// The Windows service lifetime plus three things the default one drops: session lock changes, which the activity
/// source reports; the difference between a system shutdown and a plain service stop; and a failure exit code when
/// the sampling loop died, so the service control manager's recovery actions restart the service (spec §10).
/// </summary>
internal sealed class PowerLedgerServiceLifetime : WindowsServiceLifetime
{
    /// <summary>ERROR_EXCEPTION_IN_SERVICE.</summary>
    private const int ExceptionInService = 1064;

    private readonly ServiceSignals _signals;
    private readonly ShutdownSignal _shutdown;
    private readonly SamplingLoop _loop;

    public PowerLedgerServiceLifetime(
        IHostEnvironment environment, IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory,
        IOptions<HostOptions> hostOptions, IOptions<WindowsServiceLifetimeOptions> serviceOptions,
        ServiceSignals signals, ShutdownSignal shutdown, SamplingLoop loop)
        : base(environment, applicationLifetime, loggerFactory, hostOptions, serviceOptions)
    {
        _signals = signals;
        _shutdown = shutdown;
        _loop = loop;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
    }

    /// <summary>Lock and unlock set the session's lock state; every other session change leaves it alone.</summary>
    internal static void Apply(SessionChangeReason reason, ServiceSignals signals)
    {
        if (reason == SessionChangeReason.SessionLock) signals.SessionLocked = true;
        else if (reason == SessionChangeReason.SessionUnlock) signals.SessionLocked = false;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        Apply(changeDescription.Reason, _signals);
        base.OnSessionChange(changeDescription);
    }

    protected override void OnShutdown()
    {
        _shutdown.SystemShuttingDown = true;
        base.OnShutdown();
    }

    /// <summary>A stop the loop caused by failing is reported as a failure; the recovery actions act only on those.</summary>
    protected override void OnStop()
    {
        if (_loop.ExecuteTask is { IsFaulted: true }) ExitCode = ExceptionInService;
        base.OnStop();
    }
}
