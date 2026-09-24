namespace PowerLedger.App.Tests;

/// <summary>Stands in for the real loopback HTTP listener: the test resolves <see cref="Redirect"/> itself, as if the
/// system browser had redirected back with that query string.</summary>
internal sealed class FakeLoopbackServer : ILoopbackServer
{
    public int Port { get; init; } = 51234;

    public TaskCompletionSource<string> Redirect { get; } = new();

    public Task<string> WaitForRedirectAsync(CancellationToken cancel) => Redirect.Task.WaitAsync(cancel);

    public void Dispose()
    {
    }
}
