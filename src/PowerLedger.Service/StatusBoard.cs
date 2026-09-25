using PowerLedger.Contracts;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <summary>The latest status and settings the loop has published, for the pipe and the sharing worker to read from any
/// thread, with the hardware it detected and whether its last reading found a discrete graphics card. The sharing and
/// household workers publish how sharing and the household stand, and the update worker how updates do (Plan Q §4), which the
/// status carries from the moment they change.</summary>
internal sealed class StatusBoard
{
    private ServiceStatus? _status;
    private ServiceSettings? _settings;
    private InventoryFacts? _facts;
    private SharingStatus? _sharing;
    private HouseholdStatus? _household;
    private UpdateStatus? _updates;
    private int _discreteGpu;

    /// <summary>The loop's latest status with sharing's, the household's and updates'; null until the loop has published one.</summary>
    public ServiceStatus? Status
    {
        get
        {
            var status = Volatile.Read(ref _status);
            if (status is null) return null;
            var sharing = Volatile.Read(ref _sharing);
            var household = Volatile.Read(ref _household);
            var updates = Volatile.Read(ref _updates);
            if (sharing is not null) status = status with { Sharing = sharing };
            if (updates is not null) status = status with { Updates = updates };
            return household is not null ? status with { Household = household } : status;
        }
    }

    /// <summary>How the household stands as last published; null before the household worker has published.</summary>
    public HouseholdStatus? Household => Volatile.Read(ref _household);

    public ServiceSettings? Settings => Volatile.Read(ref _settings);

    /// <summary>The hardware detected at start or at the last resume.</summary>
    public InventoryFacts? Facts => Volatile.Read(ref _facts);

    /// <summary>Whether the latest reading found a discrete graphics card.</summary>
    public bool DiscreteGpu => Volatile.Read(ref _discreteGpu) == 1;

    public void Publish(ServiceStatus status) => Volatile.Write(ref _status, status);

    public void Publish(ServiceSettings settings) => Volatile.Write(ref _settings, settings);

    public void Publish(InventoryFacts facts) => Volatile.Write(ref _facts, facts);

    public void Publish(SharingStatus sharing) => Volatile.Write(ref _sharing, sharing);

    public void Publish(HouseholdStatus household) => Volatile.Write(ref _household, household);

    /// <summary>How updates stand, from the update worker.</summary>
    public void Publish(UpdateStatus updates) => Volatile.Write(ref _updates, updates);

    public void PublishDiscreteGpu(bool present) => Volatile.Write(ref _discreteGpu, present ? 1 : 0);
}
