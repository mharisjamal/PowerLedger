using PowerLedger.Sensors;

namespace PowerLedger.Sensors.Tests;

/// <summary>
/// Level Zero Sysman under the test's control: one driver whose devices the test adds, each with the power domains it
/// reports and an energy counter the test moves. Handles are small numbers, as opaque to the source as the driver's own.
/// </summary>
internal sealed class FakeSysman : ISysman
{
    /// <summary>ZE_RESULT_ERROR_UNINITIALIZED, what Level Zero says before it has started.</summary>
    public const int Uninitialised = 0x78000001;

    /// <summary>ZE_RESULT_ERROR_UNSUPPORTED_FEATURE, what a driver says when it cannot answer the question at all.</summary>
    public const int Unsupported = 0x78000003;

    /// <summary>ZE_RESULT_ERROR_DEVICE_LOST, what a driver says when the device has gone.</summary>
    public const int DeviceLost = 0x70000001;

    private readonly Dictionary<IntPtr, Device> _devices = [];
    private readonly Dictionary<IntPtr, Domain> _domains = [];
    private nint _nextHandle = 1;

    /// <summary>What zesInit answers.</summary>
    public int InitResult { get; set; }

    /// <summary>Thrown by every call when set, as a missing or broken library throws.</summary>
    public Exception? Throw { get; set; }

    /// <summary>Every power domain of every device, in the order they were added.</summary>
    public IReadOnlyList<Domain> Domains => [.. _domains.Values];

    /// <summary>Adds a device with these power domains and returns them, so a test can move their counters.</summary>
    public IReadOnlyList<Domain> Add(SysmanDevice properties, params PowerDomain[] kinds)
    {
        var device = new Device(properties);
        _devices[_nextHandle++] = device;
        foreach (var kind in kinds)
        {
            var domain = new Domain(_nextHandle, kind);
            _domains[_nextHandle++] = domain;
            device.Domains.Add(domain);
        }
        return device.Domains;
    }

    /// <summary>The device the given properties belong to, so a test can make it fail.</summary>
    public Device Find(SysmanDevice properties) => _devices.Values.Single(device => device.Properties == properties);

    public int Init()
    {
        ThrowIfBroken();
        return InitResult;
    }

    public int GetDrivers(out IntPtr[] drivers)
    {
        ThrowIfBroken();
        drivers = _devices.Count == 0 ? [] : [DriverHandle];
        return 0;
    }

    public int GetDevices(IntPtr driver, out IntPtr[] devices)
    {
        ThrowIfBroken();
        devices = driver == DriverHandle ? [.. _devices.Keys] : [];
        return 0;
    }

    public int GetDeviceProperties(IntPtr device, out SysmanDevice properties)
    {
        ThrowIfBroken();
        var found = _devices[device];
        properties = found.Properties;
        return found.PropertiesResult;
    }

    public int GetPowerDomains(IntPtr device, out IntPtr[] domains)
    {
        ThrowIfBroken();
        var found = _devices[device];
        domains = [.. found.Domains.Select(domain => domain.Handle)];
        return found.DomainsResult;
    }

    public int GetPowerDomain(IntPtr domain, out PowerDomain kind)
    {
        ThrowIfBroken();
        var found = _domains[domain];
        kind = found.Kind;
        return found.PropertiesResult;
    }

    public int GetEnergyCounter(IntPtr domain, out SysmanEnergy energy)
    {
        ThrowIfBroken();
        var found = _domains[domain];
        found.Read();
        energy = new SysmanEnergy(found.Microjoules, found.Microseconds);
        return found.EnergyResult;
    }

    /// <summary>The one driver's handle; anything else is not a driver of this fake.</summary>
    private static IntPtr DriverHandle => 0x101;

    private void ThrowIfBroken()
    {
        if (Throw is { } error) throw error;
    }

    /// <summary>One Sysman device and what it answers.</summary>
    internal sealed class Device(SysmanDevice properties)
    {
        public SysmanDevice Properties { get; } = properties;

        public List<Domain> Domains { get; } = [];

        /// <summary>What zesDeviceGetProperties answers.</summary>
        public int PropertiesResult { get; set; }

        /// <summary>What zesDeviceEnumPowerDomains answers.</summary>
        public int DomainsResult { get; set; }
    }

    /// <summary>One power domain of one device, with the monotonic energy counter Level Zero reports.</summary>
    internal sealed class Domain(IntPtr handle, PowerDomain kind)
    {
        public IntPtr Handle { get; } = handle;

        public PowerDomain Kind { get; } = kind;

        public ulong Microjoules { get; set; }

        public ulong Microseconds { get; set; }

        /// <summary>What zesPowerGetProperties answers for this domain.</summary>
        public int PropertiesResult { get; set; }

        /// <summary>What zesPowerGetEnergyCounter answers for this domain.</summary>
        public int EnergyResult { get; set; }

        /// <summary>Thrown by a reading of this domain's counter when set.</summary>
        public Exception? Throws { get; set; }

        /// <summary>How many times this domain's counter has been read.</summary>
        public int Reads { get; private set; }

        /// <summary>Moves the counter on as a card drawing these watts for these seconds would.</summary>
        public void Draw(double watts, double seconds)
        {
            Microjoules += (ulong)Math.Round(watts * seconds * 1e6);
            Microseconds += (ulong)Math.Round(seconds * 1e6);
        }

        internal void Read()
        {
            Reads++;
            if (Throws is { } error) throw error;
        }
    }
}
