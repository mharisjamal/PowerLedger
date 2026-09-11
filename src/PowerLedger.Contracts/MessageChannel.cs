namespace PowerLedger.Contracts;

/// <summary>
/// One end of a PowerLedger pipe: whole messages in and out, one JSON line each, never more than
/// <see cref="PipeProtocol.MaxMessageBytes"/>. A line that grows past the limit is refused before it is read to the
/// end. Reads are for one reader at a time; writes may come from several tasks and are serialised here, so a pushed
/// reading never lands in the middle of a reply.
/// </summary>
public sealed class MessageChannel(Stream stream) : IAsyncDisposable
{
    private readonly byte[] _buffer = new byte[PipeProtocol.MaxMessageBytes + 2];   // room for a full line and its "\r\n"
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _start;
    private int _end;

    /// <summary>The next message, or null when the other end closed the connection between messages.</summary>
    public async ValueTask<PipeMessage?> ReadAsync(CancellationToken cancel = default)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var line = _buffer.AsMemory(_start, newline - _start);
                _start = newline + 1;
                if (line.Length > 0 && line.Span[^1] == (byte)'\r') line = line[..^1];
                if (line.Length == 0) continue;
                return PipeProtocol.Deserialize(line.Span);
            }

            if (_end - _start > PipeProtocol.MaxMessageBytes) throw new PipeProtocolException("A message was longer than 64 KB.");
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancel).ConfigureAwait(false);
            if (read == 0)
            {
                if (_end > _start) throw new PipeProtocolException("The connection closed in the middle of a message.");
                return null;
            }
            _end += read;
        }
    }

    /// <summary>Sends one message. Safe to call from several tasks at once.</summary>
    public async ValueTask WriteAsync(PipeMessage message, CancellationToken cancel = default)
    {
        var line = PipeProtocol.Serialize(message);
        await _writeGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(line, cancel).ConfigureAwait(false);
            await stream.FlushAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }
}
