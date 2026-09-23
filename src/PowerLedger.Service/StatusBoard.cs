using PowerLedger.Contracts;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <summary>The latest status and settings the loop has published, for the pipe and the sharing worker to read from any
/// thread, with the hardware it detected and whether its last reading found a discrete graphics card. The sharing worker
/// publishes how sharing stands, which the status carries from the moment it changes.</summary>
internal sealed class StatusBoard
{
    private ServiceStatus? _status;
    private ServiceSettings? _settings;
    private InventoryFacts? _facts;
    private SharingStatus? _sharing;
    private int _discreteGpu;

    /// <summary>The loop's latest status with sharing's; null until the loop has published one.</summary>
    public ServiceStatus? Status
    {
        get
        {
            var status = Volatile.Read(ref _status);
            var sharing = Volatile.Read(ref _sharing);
            return status is not null && sharing is not null ? status with { Sharing = sharing } : status;
        }
    }

    public ServiceSettings? Settings => Volatile.Read(ref _settings);

    /// <summary>The hardware detected at start or at the last resume.</summary>
    public InventoryFacts? Facts => Volatile.Read(ref _facts);

    /// <summary>Whether the latest reading found a discrete graphics card.</summary>
    public bool DiscreteGpu => Volatile.Read(ref _discreteGpu) == 1;

    public void Publish(ServiceStatus status) => Volatile.Write(ref _status, status);

    public void Publish(ServiceSettings settings) => Volatile.Write(ref _settings, settings);

    public void Publish(InventoryFacts facts) => Volatile.Write(ref _facts, facts);

    public void Publish(SharingStatus sharing) => Volatile.Write(ref _sharing, sharing);

    public void PublishDiscreteGpu(bool present) => Volatile.Write(ref _discreteGpu, present ? 1 : 0);
}
