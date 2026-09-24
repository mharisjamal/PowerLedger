namespace PowerLedger.Service.Households;

/// <summary>
/// One change to the household at a time. The worker's own work, the hour rows and both syncs, runs inside the gate with a
/// token that is cancelled as soon as anything else asks for it: a request from the App, or a pairing that has to record
/// its outcome, so neither waits on the network, and the work starts again at its next turn.
/// </summary>
internal sealed class HouseholdGate
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Lock _sync = new();
    private CancellationTokenSource? _background;
    private int _waiting;

    /// <summary>Enters for a change that mustn't wait: the worker's own work under way gives way at once.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancel)
    {
        lock (_sync)
        {
            _waiting++;
            _background?.Cancel();
        }
        try
        {
            await _lock.WaitAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync) _waiting--;
        }
        return new Lease(this, null);
    }

    /// <summary>Enters for the worker's own work; <see cref="Lease.Attention"/> is cancelled when anything else wants in.</summary>
    public async Task<Lease> EnterBackgroundAsync(CancellationToken cancel)
    {
        await _lock.WaitAsync(cancel).ConfigureAwait(false);
        var attention = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        lock (_sync)
        {
            _background = attention;
            if (_waiting > 0) attention.Cancel();
        }
        return new Lease(this, attention);
    }

    private void Leave(CancellationTokenSource? attention)
    {
        if (attention is not null)
        {
            lock (_sync)
            {
                if (_background == attention) _background = null;
            }
            attention.Dispose();
        }
        _lock.Release();
    }

    internal sealed class Lease(HouseholdGate gate, CancellationTokenSource? attention) : IDisposable
    {
        private int _left;

        /// <summary>Cancelled when a change that mustn't wait asks for the gate, or the service stops.</summary>
        public CancellationToken Attention => attention?.Token ?? CancellationToken.None;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0) gate.Leave(attention);
        }
    }
}
