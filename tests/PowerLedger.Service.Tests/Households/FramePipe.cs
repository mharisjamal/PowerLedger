using System.Threading.Channels;
using PowerLedger.Service.Households.Lan;

namespace PowerLedger.Service.Tests;

/// <summary>Two ends of an in-memory connection that carries whole frames, for the pairing and sync state machines.</summary>
internal sealed class FramePipe : IFrameChannel
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;

    private FramePipe(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    /// <summary>Frames this end has sent, in order.</summary>
    public List<byte[]> Sent { get; } = [];

    public static (FramePipe A, FramePipe B) Create()
    {
        var aToB = Channel.CreateUnbounded<byte[]>();
        var bToA = Channel.CreateUnbounded<byte[]>();
        return (new FramePipe(bToA, aToB), new FramePipe(aToB, bToA));
    }

    public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancel = default)
    {
        var copy = frame.ToArray();
        lock (Sent) Sent.Add(copy);
        await _outgoing.Writer.WriteAsync(copy, cancel);
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancel = default)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(cancel);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
