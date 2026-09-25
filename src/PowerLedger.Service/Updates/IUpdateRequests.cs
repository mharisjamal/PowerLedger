namespace PowerLedger.Service.Updates;

/// <summary>The App's requests of the update worker, from the pipe (Plan Q §3).</summary>
internal interface IUpdateRequests
{
    /// <summary>The blocking window's Update now: install the newest release as soon as it is downloaded and checked,
    /// without waiting for the user, and without waiting for an unmetered connection when the update is required. Taken
    /// only from the console user's App, or from any while the update is required, so another session's user can't close
    /// the console user's App. Answers at once: null when the worker has taken it on, otherwise why it can't, in words the
    /// App can show before it falls back to its own setup.</summary>
    /// <param name="session">The Windows session of the App that asked; null when Windows wouldn't say.</param>
    Task<string?> InstallNowAsync(uint? session, CancellationToken cancel);
}
