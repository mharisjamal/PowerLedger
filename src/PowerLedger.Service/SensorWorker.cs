using System.Collections.Concurrent;
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <param name="Sample">The validated sample.</param>
/// <param name="Health">Each source's health after this tick.</param>
/// <param name="SuspectCount">Suspect ticks since the set was built.</param>
internal sealed record TickResult(Sample Sample, IReadOnlyList<SourceHealth> Health, int SuspectCount);

/// <summary>
/// Runs every call into a sensor set on one dedicated thread, so a native call that hangs cannot stall the loop (Plan
/// B's watchdog rule). A read that overruns its timeout abandons the set: the stuck thread is left to finish on its own
/// and dispose the set when its call returns, and the next read builds a fresh set on a fresh thread. Each set is built
/// with a token that is cancelled the moment the set is abandoned or thrown away, before a fresh one can be built, so
/// whatever a stuck call passes on when it returns can be told apart and ignored. The loop is the only caller.
/// </summary>
internal sealed class SensorWorker(Func<CancellationToken, ISensorSet> factory, TimeSpan timeout, TimeProvider clock) : IDisposable
{
    private Lane? _lane;

    /// <summary>Sets abandoned because a read hung.</summary>
    public int Abandoned { get; private set; }

    /// <summary>One tick's sample, or null when the read hung and the set was abandoned. A set is built on first use.</summary>
    public async Task<TickResult?> ReadAsync(DateTimeOffset timestamp, double deltaSeconds, CancellationToken cancel)
    {
        var lane = _lane ??= new Lane(factory);
        try
        {
            return await lane.Run(set => new TickResult(set.Read(timestamp, deltaSeconds), set.Health, set.SuspectCount))
                .WaitAsync(timeout, clock, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Retire();
            Abandoned++;
            return null;
        }
    }

    /// <summary>Throws the set away, as after a resume; the next read builds a fresh one.</summary>
    public void Rebuild() => Retire();

    public void Dispose() => Retire();

    private void Retire()
    {
        _lane?.Retire();
        _lane = null;
    }

    private sealed class Lane
    {
        private readonly BlockingCollection<Action> _work = new();
        private readonly CancellationTokenSource _retired = new();
        private readonly Func<CancellationToken, ISensorSet> _factory;
        private ISensorSet? _set;

        public Lane(Func<CancellationToken, ISensorSet> factory)
        {
            _factory = factory;
            new Thread(Drain) { IsBackground = true, Name = "PowerLedger sensors" }.Start();
        }

        public Task<T> Run<T>(Func<ISensorSet, T> call)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(() =>
            {
                try
                {
                    done.SetResult(call(_set ??= _factory(_retired.Token)));
                }
                catch (Exception error)
                {
                    done.SetException(error);
                }
            });
            return done.Task;
        }

        /// <summary>No more work, and the set's token is cancelled at once. The thread disposes the set once the call it is
        /// running, if any, returns.</summary>
        public void Retire()
        {
            _retired.Cancel();
            _work.CompleteAdding();
        }

        private void Drain()
        {
            foreach (var work in _work.GetConsumingEnumerable()) work();
            try
            {
                _set?.Dispose();
            }
            catch (Exception)
            {
                // A set that fails to dispose must not take the process down with an unhandled exception on this thread.
            }
        }
    }
}
