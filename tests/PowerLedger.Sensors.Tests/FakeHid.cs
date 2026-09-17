using PowerLedger.Sensors;

namespace PowerLedger.Sensors.Tests;

/// <summary>The usages of the HID power device page a UPS answers on, spelled out for the tests.</summary>
internal static class Pdc
{
    public const ushort Page = 0x84;
    public const ushort Ups = 0x04;
    public const ushort PowerConverter = 0x16;
    public const ushort Input = 0x1A;
    public const ushort Output = 0x1C;
    public const ushort Flow = 0x1E;
    public const ushort Outlet = 0x20;
    public const ushort PowerSummary = 0x24;
    public const ushort ApparentPower = 0x33;
    public const ushort ActivePower = 0x34;
    public const ushort PercentLoad = 0x35;
    public const ushort ConfigApparentPower = 0x43;
    public const ushort ConfigActivePower = 0x44;

    /// <summary>The HID unit of a watt or a volt-ampere, which carries an exponent of 7 for whole watts.</summary>
    public const uint Watt = 0x0000D121;
}

/// <summary>A set of HID devices under the test's control: any of them can refuse to open, go silent or throw.</summary>
internal sealed class FakeHid : IHid
{
    /// <summary>The devices Windows would list, in the order it would list them.</summary>
    public List<FakeCollection> Attached { get; } = [];

    /// <summary>How many times the whole set has been looked through.</summary>
    public int Looks { get; private set; }

    public Exception? ListThrows { get; set; }

    public Exception? OpenThrows { get; set; }

    /// <summary>Paths Windows refuses to open, as it does for a device another program holds exclusively.</summary>
    public HashSet<string> Refused { get; } = [];

    public IReadOnlyList<HidPath> Interfaces()
    {
        Looks++;
        if (ListThrows is { } error) throw error;
        return [.. Attached.Select(device => new HidPath(device.Path, device.Device))];
    }

    public IHidCollection? Open(HidPath path)
    {
        if (OpenThrows is { } error) throw error;
        if (Refused.Contains(path.Path)) return null;

        var device = Attached.FirstOrDefault(attached => attached.Path == path.Path);
        device?.Opened();
        return device;
    }
}

/// <summary>One HID collection the test describes: its tree, its fields, and what its reports carry.</summary>
internal sealed class FakeCollection(string path, ushort usagePage = Pdc.Page, ushort usage = Pdc.Ups, string? device = null)
    : IHidCollection
{
    private readonly Dictionary<(byte Report, ushort Usage, ushort Collection), uint> _carried = [];
    private readonly List<HidCollection> _tree = [new(usagePage, usage, 0, 1)];
    private readonly List<HidValue> _fields = [];

    public string Path { get; } = path;

    public string Device { get; } = device ?? path;

    public ushort UsagePage { get; } = usagePage;

    public ushort Usage { get; } = usage;

    public IReadOnlyList<HidCollection> Collections => _tree;

    public IReadOnlyList<HidValue> Values => _fields;

    public string? Manufacturer { get; set; }

    public string? Product { get; set; }

    /// <summary>Every report id asked for, in order, so a test can see what was read and how often.</summary>
    public List<byte> Reads { get; } = [];

    /// <summary>Reports the device will not answer with.</summary>
    public HashSet<byte> Silent { get; } = [];

    /// <summary>True once every report is silent, as a UPS that has been unplugged would be.</summary>
    public bool Gone { get; set; }

    public Exception? ReadThrows { get; set; }

    /// <summary>What opening this one throws, as an odd device Windows will not describe would.</summary>
    public Exception? OpenThrows { get; set; }

    public int Opens { get; private set; }

    public int Closes { get; private set; }

    /// <summary>Adds a link collection, such as Output, and answers the index fields use to name it.</summary>
    public ushort Holder(ushort usage, byte type = 0, ushort parent = 0)
    {
        _tree.Add(new HidCollection(Pdc.Page, usage, parent, type));
        return (ushort)(_tree.Count - 1);
    }

    /// <summary>Adds a field of a feature report, and the raw value that report carries for it.</summary>
    public FakeCollection Field(
        ushort usage, uint raw, byte report = 1, ushort holder = 0, uint units = 0, uint unitsExp = 0,
        int min = 0, int max = 0, ushort bits = 16, bool hasNull = false, ushort count = 1)
    {
        _fields.Add(new HidValue
        {
            UsagePage = Pdc.Page,
            Usage = usage,
            ReportId = report,
            Collection = holder,
            Units = units,
            UnitsExp = unitsExp,
            LogicalMin = min,
            LogicalMax = max,
            BitSize = bits,
            HasNull = hasNull,
            ReportCount = count,
        });
        _carried[(report, usage, holder)] = raw;
        return this;
    }

    public byte[]? Feature(byte reportId)
    {
        Reads.Add(reportId);
        if (ReadThrows is { } error) throw error;
        return Gone || Silent.Contains(reportId) ? null : [reportId, 0, 0, 0];
    }

    public uint? Raw(HidValue value, byte[] report)
        => report[0] == value.ReportId && _carried.TryGetValue((value.ReportId, value.Usage, value.Collection), out var raw)
            ? raw
            : null;

    public void Opened()
    {
        Opens++;
        if (OpenThrows is { } error) throw error;
    }

    public void Dispose() => Closes++;
}
