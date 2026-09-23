using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// Whether the consent dialog should open this App session (data-sharing design §2): once the service has answered with
/// a <see cref="ServiceStatus.Sharing"/> whose consent is unanswered. Checks at most once a session: covers an existing
/// user's first open after updating, and, called again after the wizard finishes, a new install. A service older than
/// the feature, which sends no Sharing status, and one already answered, open nothing.
/// </summary>
internal sealed class ConsentGate(IServiceLink link, UiThreads threads, Action<Consent> open)
{
    private bool _checked;

    /// <summary>Checks once; every later call does nothing. Call on the UI thread.</summary>
    public void CheckOnce()
    {
        if (_checked) return;
        _checked = true;
        threads.Background(() => _ = RunAsync());
    }

    private async Task RunAsync()
    {
        var status = await link.GetStatusAsync().ConfigureAwait(false);
        if (status?.Sharing is { Consent.Answered: false } sharing) threads.Post(() => open(sharing.Consent));
    }
}
