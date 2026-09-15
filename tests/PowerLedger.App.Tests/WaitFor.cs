namespace PowerLedger.App.Tests;

/// <summary>Waits in real time for something another thread does, with a timeout so a broken test fails instead of hanging.</summary>
internal static class WaitFor
{
    public static async Task True(Func<bool> condition, int timeoutMs = 5000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("The condition did not become true in time.");
            await Task.Delay(10);
        }
    }
}
