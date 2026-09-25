namespace PowerLedger.Service.Updates;

/// <summary>The App's requests of the update worker, from the pipe (Plan Q §3).</summary>
internal interface IUpdateRequests
{
    /// <summary>The blocking window's Update now: install the newest release as soon as it is downloaded and checked,
    /// without waiting for the user or an unmetered connection. Answers at once: null when the worker has taken it on,
    /// otherwise why it can't, in words the App can show before it falls back to its own setup.</summary>
    Task<string?> InstallNowAsync(CancellationToken cancel);
}
