using System.Threading.Channels;
using PowerLedger.Contracts;

namespace PowerLedger.Service;

/// <summary>
/// Hands each tick's reading to every subscribed pipe client (spec §8). Each subscriber has a small queue that drops its
/// oldest frames when the client falls behind, so a stalled App never holds up the loop or another client.
/// </summary>
internal sealed class LiveFeed
{
    /// <summary>Frames a subscriber may fall behind by before the oldest are dropped.</summary>
    public const int Backlog = 8;

    private readonly Lock _gate = new();
    private Channel<ReadingFrame>[] _subscribers = [];

    public int Subscribers => Volatile.Read(ref _subscribers).Length;

    public ChannelReader<ReadingFrame> Subscribe()
    {
        var channel = Channel.CreateBounded<ReadingFrame>(new BoundedChannelOptions(Backlog)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_gate) _subscribers = [.. _subscribers, channel];
        return channel.Reader;
    }

    public void Unsubscribe(ChannelReader<ReadingFrame> reader)
    {
        lock (_gate)
        {
            var channel = _subscribers.FirstOrDefault(c => c.Reader == reader);
            if (channel is null) return;
            channel.Writer.TryComplete();
            _subscribers = [.. _subscribers.Where(c => c != channel)];
        }
    }

    /// <summary>Never blocks: a full queue drops its oldest frame.</summary>
    public void Publish(ReadingFrame frame)
    {
        foreach (var channel in Volatile.Read(ref _subscribers)) channel.Writer.TryWrite(frame);
    }
}
