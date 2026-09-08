namespace PowerLedger.Contracts;

/// <summary>
/// Why a power session started or ended. Persisted in SQLite by name (Enum.Parse):
/// append new members, never rename existing ones.
/// </summary>
public enum SessionReason
{
    /// <summary>Started: the machine booted and the service came up with it.</summary>
    Boot = 0,
    /// <summary>Started: the service was (re)started on a machine that was already running.</summary>
    ServiceStart = 1,
    /// <summary>Started: the machine woke from sleep or hibernation.</summary>
    Resume = 2,
    /// <summary>Ended: the machine is going to sleep or hibernation.</summary>
    Suspend = 3,
    /// <summary>Ended: the machine is shutting down or restarting.</summary>
    Shutdown = 4,
    /// <summary>Ended: the service was stopped while the machine kept running.</summary>
    ServiceStop = 5,
    /// <summary>The previous session never wrote an end (crash or power loss): it is closed with this reason, and the new session starts with it too.</summary>
    CrashRecovered = 6,
}
