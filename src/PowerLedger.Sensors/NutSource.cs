using System.Diagnostics;
using System.Net.Sockets;
using PowerLedger.Contracts;
using PowerLedger.Sensors.Nut;

namespace PowerLedger.Sensors;

/// <summary>A UPS another machine serves over Network UPS Tools, as the owner set it up in Settings.</summary>
/// <param name="Host">The machine running upsd: a name or an address.</param>
/// <param name="Port">Its port, 3493 unless set otherwise.</param>
/// <param name="Ups">The UPS's name on that server, e.g. "myups".</param>
/// <param name="Username">Who to say this is, for a server that lists only to known users; null or blank to say nothing.</param>
/// <param name="Password">Asked for the password each time a connection is made, so it is held decrypted no longer than that.</param>
public sealed record NutTarget(string Host, int Port, string Ups, string? Username = null, Func<string?>? Password = null)
{
    /// <summary>"myups on nas.local", for the status screen.</summary>
    public string Name => Port == NutProtocol.DefaultPort ? $"{Ups} on {Host}" : $"{Ups} on {Host}:{Port}";
}

/// <summary>
/// What a UPS served by another machine over Network UPS Tools says its outlets draw (TCP 3493, RFC 9271). It fills the
/// same fields as a UPS on USB, and only when no UPS on USB has given watts this tick, so a UPS attached here always wins;
/// the owner's answer to "What does it power?" decides, for either, whether the reading stands for this PC.
///
/// The server is asked on a thread of its own, every <see cref="ReadEvery"/>, over one connection kept open between reads,
/// and the ticks carry the last answer for as long as it is fresh (<see cref="StaleAfter"/>). Every exchange has a time
/// limit; a server that can't be reached, stops answering or refuses is let go and tried again after a wait that doubles to
/// <see cref="LongestBackoff"/>. Settings are asked for on every tick, and a change of them, the password's included,
/// starts over with a new connection. Nothing that goes wrong reaches the tick: it is said in <see cref="Unavailable"/>.
/// </summary>
public sealed class NutSource : ISensorSource
{
    /// <summary>How often the server is asked.</summary>
    public static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(5);

    /// <summary>How old an answer may be and still fill a tick.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    /// <summary>The longest wait before a server that keeps failing is tried again.</summary>
    public static readonly TimeSpan LongestBackoff = TimeSpan.FromMinutes(1);

    /// <summary>How long one exchange with the server may take, connecting included.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private const string NotSetUp = "no UPS on another computer is set up";

    private readonly Func<NutTarget?> _target;
    private readonly Func<TimeSpan> _clock;
    private readonly bool _runsItself;
    private readonly TimeSpan _timeout;
    private readonly Func<string, int, CancellationToken, Task<Stream>> _open;
    private readonly object _gate = new();
    private Session? _session;
    private volatile string? _unavailable = NotSetUp;

    /// <param name="target">The owner's settings, asked on every tick; null while none is set up.</param>
    public NutSource(Func<NutTarget?> target) : this(target, Elapsed(), runsItself: true, DefaultTimeout, NutClient.OpenTcpAsync)
    {
    }

    /// <summary>Test seam: any clock, a source that polls only when <see cref="PollAsync"/> is called, any time limit and
    /// any way of opening a connection.</summary>
    internal NutSource(
        Func<NutTarget?> target, Func<TimeSpan> clock, bool runsItself, TimeSpan timeout,
        Func<string, int, CancellationToken, Task<Stream>>? open = null)
    {
        _target = target;
        _clock = clock;
        _runsItself = runsItself;
        _timeout = timeout;
        _open = open ?? NutClient.OpenTcpAsync;
    }

    public string Name => "nut";

    /// <summary>Always true: a server may be set up at any time.</summary>
    public bool Supported => true;

    public string? Unavailable => _unavailable;

    public void Contribute(SampleDraft draft)
    {
        var session = Follow(_target());
        if (session?.Latest is not { } latest || _clock() - latest.At > StaleAfter) return;
        if (draft.UpsSource != UpsPowerSource.None || draft.UpsOutputW is not null) return;   // a UPS on USB answered
        draft.UpsOutputW = latest.Watts;
        draft.UpsSource = latest.Source;
        draft.UpsName = session.Target.Name;
    }

    /// <summary>Test seam: one round with the server, as the poller's thread does it; returns how long to wait before the next.</summary>
    internal Task<TimeSpan> PollAsync(CancellationToken cancel = default)
        => Volatile.Read(ref _session) is { } session ? session.PollAsync(cancel) : Task.FromResult(ReadEvery);

    public void Dispose() => Follow(null, disposing: true);

    /// <summary>Starts over when the settings changed: the old session is stopped and a new one made for the new ones.</summary>
    private Session? Follow(NutTarget? target, bool disposing = false)
    {
        lock (_gate)
        {
            var current = _session;
            if (!disposing && ReferenceEquals(current?.Target, target)) return current;
            current?.Stop();
            _session = null;
            _unavailable = NotSetUp;
            if (disposing || target is null) return null;
            var session = new Session(this, target);
            _unavailable = $"{target.Name} not read yet";
            Volatile.Write(ref _session, session);
            if (_runsItself) session.Start();
            return session;
        }
    }

    private static Func<TimeSpan> Elapsed()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }

    /// <param name="Watts">The output watts the server's figures gave.</param>
    /// <param name="Source">How they were found.</param>
    /// <param name="At">When, on the source's clock.</param>
    private sealed record Reading(double Watts, UpsPowerSource Source, TimeSpan At);

    /// <summary>The connection and the last answer for one set of settings.</summary>
    private sealed class Session(NutSource owner, NutTarget target)
    {
        private readonly CancellationTokenSource _stop = new();
        private NutClient? _client;
        private int _failures;
        private Reading? _latest;

        public NutTarget Target { get; } = target;

        public Reading? Latest => Volatile.Read(ref _latest);

        public void Start() => _ = Task.Run(() => RunAsync(_stop.Token));

        public void Stop()
        {
            _stop.Cancel();
            if (!owner._runsItself) _ = CloseAsync();   // no thread of its own to close it on its way out
        }

        public async Task<TimeSpan> PollAsync(CancellationToken cancel)
        {
            using var both = CancellationTokenSource.CreateLinkedTokenSource(cancel, _stop.Token);
            try
            {
                _client ??= await NutClient.ConnectAsync(owner._open, Target, owner._timeout, both.Token).ConfigureAwait(false);
                var variables = await _client.ListVariablesAsync(Target.Ups, both.Token).ConfigureAwait(false);
                var (watts, source) = NutProtocol.Power(variables);
                _failures = 0;
                if (watts is { } read)
                {
                    Volatile.Write(ref _latest, new Reading(read, source, owner._clock()));
                    Say(null);
                }
                else
                {
                    Say($"{Target.Name} reports neither its output power nor its load and rating");
                }
                return ReadEvery;
            }
            catch (NutRefusedException refused) when (refused.Code == "DATA-STALE")
            {
                // The server is fine; its own link to the UPS is not. The connection is kept, and the last answer goes stale.
                Say($"the server has no fresh data from {Target.Ups}");
                return ReadEvery;
            }
            catch (NutRefusedException refused)
            {
                return await StumbleAsync(refused.Code switch
                {
                    "UNKNOWN-UPS" => $"{Target.Host} serves no UPS called {Target.Ups}",
                    "ACCESS-DENIED" or "INVALID-USERNAME" or "INVALID-PASSWORD" or "USERNAME-REQUIRED" or "PASSWORD-REQUIRED"
                        => $"{Target.Host} turned down the username or password",
                    "DRIVER-NOT-CONNECTED" => $"{Target.Host} has lost its UPS",
                    var code => $"{Target.Host} said {code}",
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested && !_stop.IsCancellationRequested)
            {
                return await StumbleAsync($"{Target.Host} did not answer in time").ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                return await StumbleAsync($"{Target.Host} can't be reached: {Plain(error)}").ConfigureAwait(false);
            }
        }

        private async Task RunAsync(CancellationToken stop)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var wait = await PollAsync(stop).ConfigureAwait(false);
                    await Task.Delay(wait, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Nothing but the settings changing ends the loop; an error nobody foresaw is said rather than lost.
                Say($"reading {Target.Name} stopped: {error.Message}");
            }
            finally
            {
                // The token source is left to the collector: Stop may still cancel it after an unforeseen end.
                await CloseAsync().ConfigureAwait(false);
            }
        }

        /// <summary>A round that went wrong: the connection goes, and each failure in a row waits twice as long as the last.</summary>
        private async Task<TimeSpan> StumbleAsync(string note)
        {
            Say(note);
            await CloseAsync().ConfigureAwait(false);
            _failures++;
            var backoff = ReadEvery * Math.Pow(2, Math.Min(_failures - 1, 10));
            return backoff < LongestBackoff ? backoff : LongestBackoff;
        }

        private async Task CloseAsync()
        {
            var client = Interlocked.Exchange(ref _client, null);
            if (client is null) return;
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A socket that won't close is the operating system's business.
            }
        }

        /// <summary>Says how the server stands, unless the settings have moved on and another session speaks now.</summary>
        private void Say(string? note)
        {
            if (ReferenceEquals(Volatile.Read(ref owner._session), this)) owner._unavailable = note;
        }

        private static string Plain(Exception error) => error.Message.TrimEnd('.');
    }
}
