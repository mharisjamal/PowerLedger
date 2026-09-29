using System.Diagnostics;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// A server's or workstation's own input power, as its baseboard management controller measures it: DCMI's Get Power
/// Reading, sent through Windows' own IPMI driver. Used only where that driver found a BMC; everywhere else the source is
/// unsupported and asks nothing.
///
/// A BMC is slow to answer and may be busy, so it is asked on a thread of its own every <see cref="ReadEvery"/>, and the
/// ticks carry the last answer while it is fresh (<see cref="StaleAfter"/>). A request that fails is tried again after a
/// wait that doubles to <see cref="LongestBackoff"/>; a BMC that doesn't know the command is asked again only rarely.
/// The reading fills the machine's meter fields only when a platform power meter hasn't this tick, since both measure the
/// same input and Windows' counters are cheaper to read.
/// </summary>
public sealed class IpmiSource : ISensorSource
{
    public static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan LongestBackoff = TimeSpan.FromMinutes(1);

    /// <summary>How long a BMC that doesn't support the reading is left before it is asked again (firmware can change).</summary>
    public static readonly TimeSpan UnsupportedWait = TimeSpan.FromMinutes(30);

    private const string NoBmc = "no BMC: Windows' IPMI driver found none";

    private readonly IBmc? _bmc;
    private readonly Func<TimeSpan> _clock;
    private readonly CancellationTokenSource _stop = new();
    private int _failures;
    private Reading? _latest;
    private volatile string? _unavailable;

    /// <summary>Looks for the machine's BMC through Windows' IPMI driver and, where there is one, starts asking it.</summary>
    public IpmiSource() : this(FindBmc(out var why), Elapsed(), runsItself: true, why)
    {
    }

    /// <summary>Test seam: any BMC or none, any clock, and a source that asks only when <see cref="Poll"/> is called.</summary>
    internal IpmiSource(IBmc? bmc, Func<TimeSpan> clock, bool runsItself, string? whyNone = null)
    {
        _bmc = bmc;
        _clock = clock;
        Supported = bmc is not null;
        _unavailable = bmc is null ? whyNone ?? NoBmc : "the BMC not read yet";
        if (Supported && runsItself) _ = Task.Run(() => RunAsync(_stop.Token));
    }

    public string Name => "ipmi";

    public bool Supported { get; }

    public string? Unavailable => _unavailable;

    public void Contribute(SampleDraft draft)
    {
        if (Volatile.Read(ref _latest) is not { } latest || _clock() - latest.At > StaleAfter) return;
        if (draft.SystemMeterW is not null) return;       // a platform power meter answered first
        draft.SystemMeterW = latest.Watts;
        draft.SystemMeter = SystemMeterKind.Bmc;
        draft.SystemMeterName = "BMC (DCMI)";
    }

    /// <summary>One request to the BMC, as the source's own thread makes it; returns how long to wait before the next.</summary>
    internal TimeSpan Poll()
    {
        if (_bmc is null) return UnsupportedWait;
        try
        {
            var answer = _bmc.Send(Dcmi.NetworkFunction, Dcmi.GetPowerReading, Dcmi.Request());
            var watts = Dcmi.Watts(answer, out var why);
            _failures = 0;
            if (watts is { } read)
            {
                Volatile.Write(ref _latest, new Reading(read, _clock()));
                _unavailable = null;
            }
            else
            {
                _unavailable = why ?? "the BMC reads no power just now";
            }
            return answer.CompletionCode == 0xC1 ? UnsupportedWait : ReadEvery;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A busy or hung BMC, or WMI failing to ask it: said, and asked again later, less and less often.
            _unavailable = $"asking the BMC failed: {error.Message.TrimEnd('.')}";
            _failures++;
            var backoff = ReadEvery * Math.Pow(2, Math.Min(_failures - 1, 10));
            return backoff < LongestBackoff ? backoff : LongestBackoff;
        }
    }

    public void Dispose() => _stop.Cancel();

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested) await Task.Delay(Poll(), stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private static IBmc? FindBmc(out string? why)
    {
        why = null;
        try
        {
            return WmiBmc.Find();
        }
        catch (Exception error) when (Wmi.IsFailure(error))
        {
            why = $"Windows' IPMI driver couldn't be asked: {error.Message.TrimEnd('.')}";
            return null;
        }
    }

    private static Func<TimeSpan> Elapsed()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }

    private sealed record Reading(double Watts, TimeSpan At);
}
