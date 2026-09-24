using PowerLedger.Contracts;
using PowerLedger.Service.Households.Lan;

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
    /// <param name="incoming">True for a pairing another PC started, which this PC's user's own stops until it asks them.</param>
    /// <returns>Null, with the reason, while another pairing runs or pairing is paused.</returns>
    private CurrentPairing? BeginPairing(out string? refusal, bool madeCode = false, CancellationToken also = default, bool incoming = false)
    {
        if (_pairingGate.TryEnter(out refusal) is not { } entered) return null;
        var pairing = new CurrentPairing(
            this, entered, madeCode, CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, also), _store.HouseholdId, incoming);
        lock (_pairingLock) _pairing = pairing;
        return pairing;
    }

    /// <summary>True while a pairing is past the point where it only finishes (plan 0.10): this PC said it is joining, or
    /// recorded the PC it adds. Leaving, removal and every other change to the household wait for it.</summary>
    private bool PairingCommitted
    {
        get
        {
            lock (_pairingLock) return _pairing?.Committed == true;
        }
    }

    /// <summary>This PC's user's own pairing comes first (plan 0.10): a pairing another PC started that hasn't asked this PC's
    /// user anything yet is stopped, and has unwound, before it begins.</summary>
    private async Task StopStrangerAsync()
    {
        CurrentPairing? pairing;
        lock (_pairingLock) pairing = _pairing;
        if (pairing is null || !pairing.TryCancelUnasked()) return;
        try
        {
            await pairing.Unwound.WaitAsync(GateWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    /// <summary>Stops the pairing under way, whichever side this PC is on; the pairing gate is free for another once it has
    /// unwound (plan 0.9).</summary>
    private async Task<PipeMessage> CancelPairingAsync(CancelPairingRequest request) =>
        await StopPairingAsync().ConfigureAwait(false)
            ? Reply(request.Id, true, "Pairing stopped.")
            : Reply(request.Id, true, "No pairing is under way.");

    /// <summary>A code this PC made and is waiting on is stopped when its user joins by code instead: the two can't both run.</summary>
    private Task StopOwnCodeAsync() => StopPairingAsync(madeCodeOnly: true);

    /// <summary>Cancels the pairing under way, if any, and waits a few seconds for it to unwind.</summary>
    /// <returns>False when none was under way.</returns>
    private async Task<bool> StopPairingAsync(bool madeCodeOnly = false)
    {
        CurrentPairing? pairing;
        lock (_pairingLock) pairing = _pairing;
        if (pairing is null || (madeCodeOnly && !pairing.MadeCode)) return false;
        pairing.Cancel();
        try
        {
            await pairing.Unwound.WaitAsync(GateWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        return true;
    }

    /// <summary>Cancels the pairing under way, if any, without waiting, as when this PC was removed or entered a household by
    /// sign-in (plan 0.9); what it would record or enter is refused, since the household is no longer the one it began in.</summary>
    private void CancelPairingUnderWay()
    {
        CurrentPairing? pairing;
        lock (_pairingLock) pairing = _pairing;
        pairing?.Cancel();
    }

    private void Forget(CurrentPairing pairing)
    {
        lock (_pairingLock)
        {
            if (_pairing == pairing) _pairing = null;
        }
        if (pairing.Committed) Kick();                                         // a turn that waited for it goes now
    }

    /// <summary>The prompts of a pairing another PC started, noting when it first asks this PC's user (plan 0.10); once its
    /// pairing was stopped, it asks nothing.</summary>
    private sealed class AskingBroker(IPromptBroker inner, CurrentPairing pairing) : IPromptBroker
    {
        public Task<bool> AskToJoinAsync(JoinQuestion question, CancellationToken cancel) =>
            pairing.TryMarkAsked() ? inner.AskToJoinAsync(question, cancel) : Task.FromResult(false);

        public Task<bool> ConfirmCodeAsync(string otherName, string code, CancellationToken cancel) =>
            pairing.TryMarkAsked() ? inner.ConfirmCodeAsync(otherName, code, cancel) : Task.FromResult(false);
    }

    /// <summary>One pairing: its place in the pairing gate, what stops it, and the household this PC was in when it began.</summary>
    private sealed class CurrentPairing(
        HouseholdWorker worker, IDisposable entered, bool madeCode, CancellationTokenSource stop, string? startHousehold, bool incoming)
        : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _unwound = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _ended;
        private bool _cancelled;
        private bool _asked;
        private bool _committed;

        public bool MadeCode { get; } = madeCode;

        public string? StartHousehold { get; } = startHousehold;

        public CancellationToken Token => stop.Token;

        /// <summary>Done once the pairing has unwound and freed the pairing gate.</summary>
        public Task Unwound => _unwound.Task;

        /// <summary>True once past the point where it only finishes (plan 0.10): a cancel no longer stops it.</summary>
        public bool Committed
        {
            get
            {
                lock (_gate) return _committed;
            }
        }

        /// <summary>The household a first pairing makes, kept here until the step that records the new member makes it (plan
        /// 0.10); null otherwise.</summary>
        public (string Id, byte[] Key)? Provisional { get; set; }

        /// <summary>From now on it only finishes (plan 0.10).</summary>
        public void Commit()
        {
            lock (_gate) _committed = true;
        }

        /// <summary>Notes that it asks this PC's user; false once it was stopped.</summary>
        public bool TryMarkAsked()
        {
            lock (_gate)
            {
                if (_ended || _cancelled) return false;
                _asked = true;
                return true;
            }
        }

        /// <summary>Stops a pairing another PC started that hasn't asked this PC's user anything (plan 0.10).</summary>
        /// <returns>True when it was stopped.</returns>
        public bool TryCancelUnasked()
        {
            lock (_gate)
            {
                if (!incoming || _asked || _ended || _cancelled || _committed) return false;
                _cancelled = true;
            }
            Stop();
            return true;
        }

        /// <summary>Stops it, unless it is past the point where it only finishes; it says goodbye and unwinds, and only then is
        /// the pairing gate free for another.</summary>
        public void Cancel()
        {
            lock (_gate)
            {
                if (_ended || _cancelled || _committed) return;
                _cancelled = true;
            }
            Stop();
        }

        private void Stop()
        {
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
            _unwound.TrySetResult();
        }
    }
}
