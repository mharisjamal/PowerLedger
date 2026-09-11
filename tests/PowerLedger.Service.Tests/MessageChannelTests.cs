using System.Text;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class MessageChannelTests
{
    [Fact]
    public async Task Messages_are_read_one_line_at_a_time_however_the_bytes_arrive()
    {
        var bytes = Lines(new GetStatusRequest(1), new OkReply(2), new ErrorReply(null, "x"));
        await using var channel = new MessageChannel(new TrickleStream(bytes, chunk: 3));
        (await channel.ReadAsync()).ShouldBe(new GetStatusRequest(1));
        (await channel.ReadAsync()).ShouldBe(new OkReply(2));
        (await channel.ReadAsync()).ShouldBe(new ErrorReply(null, "x"));
        (await channel.ReadAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Blank_lines_and_windows_line_endings_are_tolerated()
    {
        var text = "\n\r\n" + """{"type":"getStatus","id":5}""" + "\r\n";
        await using var channel = new MessageChannel(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        (await channel.ReadAsync()).ShouldBe(new GetStatusRequest(5));
        (await channel.ReadAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task A_line_longer_than_64_KB_is_a_protocol_error_without_reading_it_all()
    {
        var stream = new TrickleStream(Encoding.UTF8.GetBytes(new string('x', 200_000)), chunk: 8192);
        await using var channel = new MessageChannel(stream);
        await Should.ThrowAsync<PipeProtocolException>(async () => await channel.ReadAsync());
        stream.Position.ShouldBeLessThan(100_000);
    }

    [Fact]
    public async Task A_connection_that_closes_mid_message_is_a_protocol_error()
    {
        await using var channel = new MessageChannel(new MemoryStream(Encoding.UTF8.GetBytes("""{"type":"getSta""")));
        await Should.ThrowAsync<PipeProtocolException>(async () => await channel.ReadAsync());
    }

    [Fact]
    public async Task Writes_from_many_tasks_never_interleave()
    {
        var sink = new MemoryStream();
        await using (var writer = new MessageChannel(sink))
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(task => Task.Run(async () =>
            {
                for (var i = 0; i < 50; i++) await writer.WriteAsync(new ErrorReply(task * 100 + i, new string('y', 500)));
            })));
        }

        await using var reader = new MessageChannel(new MemoryStream(sink.ToArray()));
        var ids = new List<long?>();
        while (await reader.ReadAsync() is { } message) ids.Add(message.ShouldBeOfType<ErrorReply>().Id);
        ids.Count.ShouldBe(400);
        ids.Distinct().Count().ShouldBe(400);
    }

    private static byte[] Lines(params PipeMessage[] messages) => [.. messages.SelectMany(PipeProtocol.Serialize)];
}

/// <summary>A stream that hands out a few bytes a read, as a busy pipe does.</summary>
internal sealed class TrickleStream(byte[] data, int chunk) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _position);
        data.AsSpan(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(buffer.Span));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
