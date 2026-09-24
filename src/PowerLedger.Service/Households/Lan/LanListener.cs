using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace PowerLedger.Service.Households.Lan;

/// <summary>A connection from another PC, once its first frame came: that frame as it came, which a pairing's transcript
/// takes, the hello read from it, and the address it came from.</summary>
internal sealed record LanCall(IFrameChannel Channel, byte[] Hello, LanMessage Message, IPAddress? From);

/// <summary>
/// The household's TCP listener on the network (households design §3), on a port Windows chooses, which the DNS-SD
/// announcement gives. Each connection starts with a hello; the handler takes it from there, pairing or syncing. A few
/// connections at a time are served and the rest turned away, and one that says nothing in time is closed, so nobody on
/// the network can tie the service up. The installer's firewall rule lets other PCs in on Private networks only.
/// </summary>
internal sealed class LanListener : IAsyncDisposable
{
    public const int MaxConnections = 8;

    /// <summary>The most connections served at once from one address (plan 0.8).</summary>
    public const int MaxPerAddress = 2;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    private readonly IPAddress _address;
    private readonly Func<LanCall, CancellationToken, Task> _handle;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly List<Task> _connections = [];
    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, int> _perAddress = [];
    private TcpListener? _listener;
    private Task? _accepting;
    private volatile bool _closed;

    /// <param name="address">Where to listen: every address, IPv4 and IPv6, for the service; loopback for tests.</param>
    /// <param name="handle">Carries on a connection from its hello.</param>
    public LanListener(IPAddress address, Func<LanCall, CancellationToken, Task> handle, ILogger log)
    {
        _address = address;
        _handle = handle;
        _log = log;
    }

    /// <summary>The port Windows chose; 0 before <see cref="Start"/>.</summary>
    public int Port => _listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0;

    public void Start()
    {
        var listener = new TcpListener(_address, 0);
        if (_address.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
        listener.Start();
        _listener = listener;
        _accepting = AcceptAsync(listener, _stop.Token);
        _log.LogInformation("Listening for the household's other PCs on port {Port}", Port);
    }

    /// <summary>Shuts the port before the first await, so a caller that doesn't wait finds it already shut, then ends the
    /// connections being served.</summary>
    public async ValueTask DisposeAsync()
    {
        _closed = true;
        _listener?.Stop();
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_accepting is not null) await _accepting.ConfigureAwait(false);
        Task[] running;
        lock (_gate) running = [.. _connections];
        await Task.WhenAll(running).ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                if (_closed || stop.IsCancellationRequested) return;                   // InvalidOperationException: shut between two accepts
                _log.LogDebug(error, "Accepting a connection failed");
                continue;
            }
            var from = Address(client);
            if (!TakeAddress(from))
            {
                client.Dispose();                                             // too many at once from there: turned away
                continue;
            }
            if (!_slots.Wait(0))
            {
                LeaveAddress(from);
                client.Dispose();                                             // too many at once: turned away
                continue;
            }
            var serving = ServeAsync(client, stop);
            lock (_gate) _connections.Add(serving);
            _ = serving.ContinueWith(
                done =>
                {
                    lock (_gate) _connections.Remove(done);
                    LeaveAddress(from);
                    _slots.Release();
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static IPAddress? Address(TcpClient client) =>
        (client.Client.RemoteEndPoint as IPEndPoint)?.Address is { } address && address.IsIPv4MappedToIPv6 ? address.MapToIPv4()
            : (client.Client.RemoteEndPoint as IPEndPoint)?.Address;

    private bool TakeAddress(IPAddress? from)
    {
        if (from is null) return true;
        lock (_gate)
        {
            var open = _perAddress.GetValueOrDefault(from);
            if (open >= MaxPerAddress) return false;
            _perAddress[from] = open + 1;
            return true;
        }
    }

    private void LeaveAddress(IPAddress? from)
    {
        if (from is null) return;
        lock (_gate)
        {
            if (_perAddress.GetValueOrDefault(from) <= 1) _perAddress.Remove(from);
            else _perAddress[from]--;
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        await Task.Yield();
        await using var channel = new StreamFrameChannel(client.GetStream());
        using (client)
        {
            try
            {
                byte[]? first;
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    limit.CancelAfter(HelloTimeout);
                    first = await channel.ReceiveAsync(limit.Token).ConfigureAwait(false);
                }
                if (first is null || LanMessages.Read(first) is not { Type: "hello" } hello) return;
                await _handle(new LanCall(channel, first, hello, Address(client)), stop).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or ObjectDisposedException or SocketException)
            {
                _log.LogDebug(error, "A household connection ended early");
            }
            catch (Exception error)
            {
                _log.LogWarning(error, "Serving a household connection failed");
            }
        }
    }
}

/// <summary>Connects to another PC's listener.</summary>
internal static class LanConnector
{
    /// <exception cref="IOException">It couldn't be reached in time.</exception>
    public static async Task<IFrameChannel> ConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancel = default)
    {
        var client = new TcpClient(address.AddressFamily);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(address, port, limit.Token).ConfigureAwait(false);
            return new StreamFrameChannel(new OwningStream(client));
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            client.Dispose();
            throw new IOException($"{address} didn't answer on port {port}.", error);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>The client's stream, which closes the client with it.</summary>
    private sealed class OwningStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
