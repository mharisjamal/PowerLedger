using PowerLedger.Contracts;

namespace PowerLedger.Service.Households;

/// <summary>The pairing under way, on either side (households design §3, §4, plan 0.8): one at a time, which
/// <see cref="CancelPairingRequest"/> stops.</summary>
internal sealed partial class HouseholdWorker
{
    private readonly Lock _pairingLock = new();
    private CurrentPairing? _pairing;

    /// <summary>Starts a pairing, which ends when the result is disposed.</summary>
    /// <param name="madeCode">True for a code this PC made, which joining by code stops without asking.</param>
    /// <param name="also">Stops the pairing too, as the listener's connection does.</param>
    /// <returns>Null, with the reason, while another pairing runs or pairing is paused.</returns>
    private CurrentPairing? BeginPairing(out string? refusal, bool madeCode = false, CancellationToken also = default)
    {
        if (_pairingGate.TryEnter(out refusal) is not { } entered) return null;
        var pairing = new CurrentPairing(this, entered, madeCode, CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, also));
        lock (_pairingLock) _pairing = pairing;
        return pairing;
    }

    /// <summary>Stops the pairing under way, whichever side this PC is on, and frees the pairing gate at once.</summary>
    private PipeMessage CancelPairing(CancelPairingRequest request)
    {
        CurrentPairing? pairing;
        lock (_pairingLock) pairing = _pairing;
        if (pairing is null) return Reply(request.Id, true, "No pairing is under way.");
        pairing.Cancel();
        return Reply(request.Id, true, "Pairing stopped.");
    }

    /// <summary>A code this PC made and is waiting on is stopped when its user joins by code instead: the two can't both run.</summary>
    private void StopOwnCode()
    {
        CurrentPairing? pairing;
        lock (_pairingLock) pairing = _pairing;
        if (pairing is { MadeCode: true }) pairing.Cancel();
    }

    private void Forget(CurrentPairing pairing)
    {
        lock (_pairingLock)
        {
            if (_pairing == pairing) _pairing = null;
        }
    }

    /// <summary>One pairing: its place in the pairing gate and what stops it.</summary>
    private sealed class CurrentPairing(HouseholdWorker worker, IDisposable entered, bool madeCode, CancellationTokenSource stop) : IDisposable
    {
        private readonly Lock _gate = new();
        private bool _ended;
        private bool _cancelled;

        public bool MadeCode { get; } = madeCode;

        public CancellationToken Token => stop.Token;

        /// <summary>Stops it; the pairing gate is free for another at once, while this one says goodbye.</summary>
        public void Cancel()
        {
            lock (_gate)
            {
                if (_ended || _cancelled) return;
                _cancelled = true;
            }
            worker.Forget(this);
            entered.Dispose();
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_ended) return;
                _ended = true;
            }
            worker.Forget(this);
            entered.Dispose();
            stop.Dispose();
        }
    }
}
