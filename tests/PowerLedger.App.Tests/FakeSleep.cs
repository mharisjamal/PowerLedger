namespace PowerLedger.App.Tests;

/// <summary>Windows' timeouts as the test sets them, counting the reads.</summary>
internal sealed class FakeSleep : ISleepSettings
{
    public int Reads { get; private set; }

    public SleepTimeouts Timeouts { get; set; } = new(TimeSpan.FromHours(3), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));

    public SleepTimeouts Read()
    {
        Reads++;
        return Timeouts;
    }
}
