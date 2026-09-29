using System.Net.Sockets;
using System.Text;

namespace PowerLedger.Sensors.Nut;

/// <summary>
/// One connection to a NUT server's upsd, used for reading only: it may say who it is (USERNAME and PASSWORD), and then
/// lists a UPS's variables as often as asked. It never sends LOGIN, which would make it count as one of the machines the
/// UPS powers and shuts down, and never sets or instructs anything. Every exchange has a time limit, so a server that stops
/// answering costs a few seconds of the poller's thread and never the sampling loop's.
/// </summary>
internal sealed class NutClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly TimeSpan _timeout;
    private readonly byte[] _buffer = new byte[1024];
    private int _start;
    private int _end;

    private NutClient(Stream stream, TimeSpan timeout)
    {
        _stream = stream;
        _timeout = timeout;
    }

    /// <summary>Opens a TCP connection to the server.</summary>
    public static async Task<Stream> OpenTcpAsync(string host, int port, CancellationToken cancel)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancel).ConfigureAwait(false);
            return new OwningStream(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Connects, and says who it is when a username is given.</summary>
    /// <exception cref="NutRefusedException">The server turned the username or password down.</exception>
    public static async Task<NutClient> ConnectAsync(
        Func<string, int, CancellationToken, Task<Stream>> open, NutTarget target, TimeSpan timeout, CancellationToken cancel)
    {
        Stream stream;
        using (var limit = Limit(timeout, cancel))
        {
            try
            {
                stream = await open(target.Host, target.Port, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                // Windows keeps trying a refused connection for a while, so a host with nothing listening often looks like this.
                throw new IOException("nothing answered at that address");
            }
        }
        var client = new NutClient(stream, timeout);
        try
        {
            if (!string.IsNullOrWhiteSpace(target.Username))
            {
                await client.AskOkAsync("USERNAME " + NutProtocol.Quote(target.Username), cancel).ConfigureAwait(false);
                if (target.Password?.Invoke() is { Length: > 0 } password)
                {
                    await client.AskOkAsync("PASSWORD " + NutProtocol.Quote(password), cancel).ConfigureAwait(false);
                }
            }
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Every variable of the UPS, by name.</summary>
    /// <exception cref="NutRefusedException">The server knows no such UPS, has no fresh data from it, or won't say.</exception>
    public async Task<IReadOnlyDictionary<string, string>> ListVariablesAsync(string ups, CancellationToken cancel)
    {
        using var limit = Limit(_timeout, cancel);
        await SendAsync("LIST VAR " + ups, limit.Token).ConfigureAwait(false);
        var first = await ReadLineAsync(limit.Token).ConfigureAwait(false);
        Refused(first);
        if (!first.StartsWith("BEGIN LIST VAR", StringComparison.Ordinal)) throw new IOException("the UPS server answered out of turn");

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            var line = await ReadLineAsync(limit.Token).ConfigureAwait(false);
            if (line.StartsWith("END LIST VAR", StringComparison.Ordinal)) return variables;
            Refused(line);
            var words = NutProtocol.Words(line);
            if (words is ["VAR", _, var name, var value]) variables[name] = value;
            if (variables.Count > NutProtocol.MaxVariables) throw new IOException("the UPS server's list never ended");
        }
    }

    /// <summary>Says goodbye where the server is still listening, and closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await SendAsync("LOGOUT", limit.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A server that has gone needs no goodbye.
        }
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task AskOkAsync(string command, CancellationToken cancel)
    {
        using var limit = Limit(_timeout, cancel);
        await SendAsync(command, limit.Token).ConfigureAwait(false);
        var reply = await ReadLineAsync(limit.Token).ConfigureAwait(false);
        Refused(reply);
        if (!reply.StartsWith("OK", StringComparison.Ordinal)) throw new IOException("the UPS server answered out of turn");
    }

    private async Task SendAsync(string command, CancellationToken cancel)
    {
        var bytes = Encoding.UTF8.GetBytes(command + "\n");
        await _stream.WriteAsync(bytes, cancel).ConfigureAwait(false);
        await _stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>One reply line, without its line ending. A line longer than any upsd sends is the wrong server.</summary>
    private async Task<string> ReadLineAsync(CancellationToken cancel)
    {
        var line = new List<byte>(128);
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _stream.ReadAsync(_buffer, cancel).ConfigureAwait(false);
                if (_end == 0) throw new IOException("the UPS server closed the connection");
            }
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            var stop = newline < 0 ? _end : newline;
            line.AddRange(_buffer.AsSpan(_start, stop - _start));
            if (line.Count > NutProtocol.MaxLineBytes) throw new IOException("the UPS server sent a line far too long");
            _start = newline < 0 ? _end : newline + 1;
            if (newline >= 0) return Encoding.UTF8.GetString([.. line]).TrimEnd('\r');
        }
    }

    private static void Refused(string line)
    {
        if (line.StartsWith("ERR", StringComparison.Ordinal))
        {
            throw new NutRefusedException(NutProtocol.Words(line) is [_, var code, ..] ? code : "ERR");
        }
    }

    private static CancellationTokenSource Limit(TimeSpan timeout, CancellationToken cancel)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        return limit;
    }

    /// <summary>A network stream that closes its socket with it.</summary>
    private sealed class OwningStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

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
