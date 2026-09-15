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

    /// <summary>Every change the App asked for, in order.</summary>
    public List<object> Writes { get; } = [];

    /// <summary>What every change comes back as.</summary>
    public WriteResult Answer { get; set; } = WriteResult.Done;

    public Task<WriteResult> SetSettingsAsync(ServiceSettings settings, CancellationToken cancel = default) => Write(settings);

    public Task<WriteResult> SetTariffAsync(decimal pricePerKwh, string currency, DateTimeOffset? effectiveFrom, CancellationToken cancel = default)
        => Write((pricePerKwh, currency, effectiveFrom));

    public Task<WriteResult> ResetCalibrationAsync(CancellationToken cancel = default) => Write("reset");

    private Task<WriteResult> Write(object change)
    {
        if (!IsConnected) return Task.FromResult(WriteResult.NotConnected);
        Writes.Add(change);
        return Task.FromResult(Answer);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
