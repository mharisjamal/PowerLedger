namespace PowerLedger.Service.Tests;

internal static class WaitFor
{
    /// <summary>Polls on real time until the condition holds; fails the test after <paramref name="timeoutMs"/>. Yields for the
    /// first 100 ms rather than sleeping, because a timer sleep lasts a whole 15 ms scheduler tick on Windows.</summary>
    public static async Task True(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            var elapsed = Environment.TickCount64 - start;
            if (elapsed > timeoutMs) throw new TimeoutException("The condition never became true.");
            if (elapsed < 100) await Task.Yield();
            else await Task.Delay(5);
        }
    }
}
