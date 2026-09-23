using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// Whether the consent dialog should open this App session (data-sharing design §2): once the service has answered with
/// a <see cref="ServiceStatus.Sharing"/> whose consent is unanswered. Checks at most once a session: covers an existing
/// user's first open after updating, and, called again after the wizard finishes, a new install. A service older than
/// the feature, which sends no Sharing status, and one already answered, open nothing. A status with no Sharing yet —
/// the link not connected, or the service still starting, as just after an update starts it and opens the App at once —
/// does not use up the session's one check: it retries on the next connection and every <see cref="RetryEvery"/>, until
/// one comes back with Sharing.
/// </summary>
internal sealed class ConsentGate(IServiceLink link, UiThreads threads, TimeProvider clock, Action<Consent> open)
{
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);

    private bool _done;
    private ITimer? _timer;

    /// <summary>Checks once a session, once a status carrying Sharing comes back. Call on the UI thread.</summary>
    public void CheckOnce()
    {
        if (_done || _timer is not null) return;
        link.ConnectionChanged += OnConnectionChanged;
        _timer = clock.CreateTimer(_ => threads.Background(() => _ = RunAsync()), null, RetryEvery, RetryEvery);
        threads.Background(() => _ = RunAsync());
    }

    private void OnConnectionChanged(bool connected)
    {
        if (connected) threads.Background(() => _ = RunAsync());
    }

    private async Task RunAsync()
    {
        var status = await link.GetStatusAsync().ConfigureAwait(false);
        if (status?.Sharing is not { } sharing) return;   // link not connected yet, or the service still starting: try again
        _done = true;
        link.ConnectionChanged -= OnConnectionChanged;
        _timer?.Dispose();
        _timer = null;
        if (!sharing.Consent.Answered) threads.Post(() => open(sharing.Consent));
    }
}
