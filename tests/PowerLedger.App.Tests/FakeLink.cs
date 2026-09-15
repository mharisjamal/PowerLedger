using PowerLedger.Contracts;

namespace PowerLedger.App.Tests;

/// <summary>A service link the test drives by hand.</summary>
internal sealed class FakeLink : IServiceLink
{
    public event Action<ReadingFrame>? FrameReceived;

    public event Action<bool>? ConnectionChanged;

    public bool IsConnected { get; private set; }

    public ServiceStatus? Status { get; set; } = Statuses.Running();

    public ServiceSettings? Settings { get; set; } = ServiceSettings.Default;

    public void Start()
    {
    }

    public void Push(ReadingFrame frame) => FrameReceived?.Invoke(frame);

    public void Connect(bool connected)
    {
        IsConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    public Task<ServiceStatus?> GetStatusAsync(CancellationToken cancel = default) => Task.FromResult(IsConnected ? Status : null);

    public Task<ServiceSettings?> GetSettingsAsync(CancellationToken cancel = default) => Task.FromResult(IsConnected ? Settings : null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
