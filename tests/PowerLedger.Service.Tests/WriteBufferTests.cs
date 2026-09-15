using Microsoft.Data.Sqlite;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class WriteBufferTests
{
    [Fact]
    public void A_flush_writes_everything_held_in_one_batch()
    {
        var batches = new List<int>();
        var buffer = new WriteBuffer(batch => batches.Add(batch.Count));
        for (var s = 0; s < 60; s++) buffer.Add(Readings.At(s));
        buffer.Flush().ShouldBeTrue();
        batches.ShouldBe(new[] { 60 });
        buffer.Count.ShouldBe(0);
        buffer.Problem.ShouldBeNull();
    }

    [Fact]
    public void An_empty_flush_writes_nothing()
    {
        var calls = 0;
        new WriteBuffer(_ => calls++).Flush().ShouldBeTrue();
        calls.ShouldBe(0);
    }

    [Fact]
    public void A_failed_write_keeps_the_readings_and_the_next_flush_retries_them()
    {
        var full = true;
        var written = 0;
        var buffer = new WriteBuffer(batch =>
        {
            if (full) throw new SqliteException("SQLite Error 13: 'database or disk is full'.", 13);
            written += batch.Count;
        });
        for (var s = 0; s < 60; s++) buffer.Add(Readings.At(s));
        buffer.Flush().ShouldBeFalse();
        buffer.Count.ShouldBe(60);
        buffer.Problem.ShouldNotBeNull().ShouldContain("disk is full");

        for (var s = 60; s < 120; s++) buffer.Add(Readings.At(s));
        full = false;
        buffer.Flush().ShouldBeTrue();
        written.ShouldBe(120);
        buffer.Problem.ShouldBeNull();
    }

    [Fact]
    public void A_write_that_fails_for_any_other_reason_is_held_and_reported_the_same_way()
    {
        var buffer = new WriteBuffer(_ => throw new IOException("The network location cannot be reached."));
        for (var s = 0; s < 10; s++) buffer.Add(Readings.At(s));
        buffer.Flush().ShouldBeFalse();
        buffer.Count.ShouldBe(10);
        buffer.Problem.ShouldNotBeNull().ShouldContain("cannot be reached");
    }

    [Fact]
    public void Past_its_capacity_the_oldest_readings_are_dropped_and_counted()
    {
        var buffer = new WriteBuffer(_ => throw new SqliteException("SQLite Error 10: 'disk I/O error'.", 10), capacity: 100);
        for (var s = 0; s < 150; s++) buffer.Add(Readings.At(s));
        buffer.Flush().ShouldBeFalse();
        buffer.Count.ShouldBe(100);
        buffer.Dropped.ShouldBe(50);
        buffer.Problem.ShouldNotBeNull().ShouldContain("50 older");
    }
}
