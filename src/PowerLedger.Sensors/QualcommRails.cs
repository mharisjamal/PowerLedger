namespace PowerLedger.Sensors;

/// <summary>One row of <see cref="QualcommRails.Table"/>.</summary>
/// <param name="Key">The rail's name lowercased, with everything but letters and digits and any trailing number taken off,
/// so "cpu_cluster_0", "CPU_CLUSTER_1" and "cpu cluster 2" are all "cpucluster".</param>
/// <param name="Kind">What the rail measures.</param>
/// <param name="Inside">The key of the rail whose energy already holds this one's, or null for the outermost rail. A rail is
/// never added to a rail it is inside, so nothing is counted twice.</param>
public readonly record struct PlatformRail(string Key, RailKind Kind, string? Inside);

/// <summary>
/// The Energy Meter rails a Snapdragon X machine publishes, and Microsoft's rail names any other machine's meter may use, and
/// how they nest. UNVERIFIED ON REAL HARDWARE: no PowerLedger test has run on a Snapdragon machine.
/// <para>
/// Qualcomm's names are the ones its own library looks for under <c>\Energy Meter(*)</c> (libqcperf,
/// github.com/qualcomm/libqcperf, power_telemetry.c): <c>CPU_CLUSTER_0</c> to <c>CPU_CLUSTER_2</c>, <c>GPU</c>, <c>NPU</c>,
/// <c>MEMORY</c>, <c>SOC</c>, <c>SYS</c> (read as <c>SYSTEM</c> where <c>SYS</c> fails, so the two are one rail under two
/// names), <c>PSU_USB</c>, <c>USBC_TOTAL</c>, <c>ROP</c> (rest of platform), <c>INFRA</c> and <c>MULTIMEDIA</c>. npu-watt
/// (github.com/hotschmoe/npu-watt) reads the same rails lowercased and notes that "system includes the display and platform
/// overhead"; it takes the system rail as the machine's draw on AC, which is why that rail is read as the load behind the
/// charger rather than what the adapter delivers (<see cref="SystemHoldsCharging"/>). <c>PSU_USB</c> is the charger's input,
/// battery charging included, so it is read for itself and never as the PC's draw. Where <c>USBC_TOTAL</c>, <c>ROP</c>,
/// <c>INFRA</c> and <c>MULTIMEDIA</c> sit among the others is unknown until one device dump shows it, so they are listed and
/// never summed.
/// </para>
/// <para>
/// Microsoft's EMI rail naming scheme (learn.microsoft.com, powermeter/rail_naming) names a component's rail <c>CPU</c>,
/// <c>GPU</c>, <c>NPU</c>, <c>DISPLAY</c>, <c>STORAGE</c>, <c>WIFI</c> or <c>MBB</c>, and its parts with that name and an
/// underscore before the rest. The exact name wins over its parts; without it the parts are summed. Here a part is a rail inside
/// its component's, so the same nesting rule does both.
/// </para>
/// <para>
/// The nesting below is the research's reading of those names (PowerLedger research "measure more", 2026-09-30, and plan AC):
/// the CPU clusters, the graphics and the NPU sit inside the soc, and the soc, the memory and the panel, storage and radios
/// inside the system rail. Whether soc really holds the clusters and the graphics, and whether the X1 and X2 agree, needs one
/// device dump; the system rail is checked against the battery's discharge on every machine that has one
/// (<see cref="PlatformRailCheck"/>). A device that disagrees is corrected here, in this one table, and nowhere else.
/// </para>
/// </summary>
public static class QualcommRails
{
    public static readonly IReadOnlyList<PlatformRail> Table =
    [
        new("sys", RailKind.Platform, Inside: null),
        new("system", RailKind.Platform, Inside: "sys"),               // SYS under its other name: never counted twice
        new("soc", RailKind.Package, Inside: "sys"),
        new("memory", RailKind.Memory, Inside: "sys"),
        new("cpu", RailKind.Cores, Inside: "soc"),                     // Microsoft's CPU, which holds the clusters
        new("cpucluster", RailKind.Cores, Inside: "cpu"),
        new("gpu", RailKind.IntegratedGpu, Inside: "soc"),
        new("npu", RailKind.Npu, Inside: "soc"),
        new("display", RailKind.Display, Inside: "sys"),
        new("storage", RailKind.Storage, Inside: "sys"),
        new("wifi", RailKind.Network, Inside: "sys"),
        new("mbb", RailKind.MobileBroadband, Inside: "sys"),
        new("psuusb", RailKind.ChargerInput, Inside: null),
        new("usbctotal", RailKind.Unplaced, Inside: null),
        new("rop", RailKind.Unplaced, Inside: null),
        new("infra", RailKind.Unplaced, Inside: null),
        new("multimedia", RailKind.Unplaced, Inside: null),
    ];

    /// <summary>Microsoft's component names, as a part's name begins, and the key of the component's own rail.</summary>
    private static readonly (string Prefix, string Key)[] Components =
    [
        ("CPU_", "cpu"), ("GPU_", "gpu"), ("NPU_", "npu"), ("DISPLAY_", "display"), ("STORAGE_", "storage"), ("WIFI_", "wifi"), ("MBB_", "mbb"),
    ];

    /// <summary>
    /// False while the system rail is taken as the machine's own draw, behind the charger. Were a device to show that it is
    /// what the adapter delivers, charge and all, this turns true and the battery's charge rate is taken off it, because
    /// charging the battery is never counted as the PC's consumption (see <see cref="BatterySource"/>).
    /// </summary>
    public const bool SystemHoldsCharging = false;

    /// <summary>The table key for a counter instance name, e.g. "cpucluster" for "CPU_Cluster_1".</summary>
    public static string Key(string name)
    {
        Span<char> kept = stackalloc char[Math.Min(name.Length, 64)];
        var length = 0;
        foreach (var c in name)
        {
            if (length == kept.Length) break;
            if (char.IsAsciiLetterOrDigit(c)) kept[length++] = char.ToLowerInvariant(c);
        }
        while (length > 0 && char.IsAsciiDigit(kept[length - 1])) length--;
        return new string(kept[..length]);
    }

    /// <summary>The table's row for a counter instance name, or null when the name is none of these rails. A name the table
    /// does not hold that begins with one of Microsoft's component names and an underscore, as <c>GPU_TOP</c> does, is a part
    /// of that component: a row of the component's kind, inside the component's own rail.</summary>
    public static PlatformRail? Find(string name, IReadOnlyList<PlatformRail> table)
    {
        var key = Key(name);
        foreach (var rail in table)
        {
            if (rail.Key == key) return rail;
        }
        foreach (var (prefix, component) in Components)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var rail in table)
            {
                if (rail.Key == component) return new PlatformRail(key, rail.Kind, Inside: component);
            }
        }
        return null;
    }

    /// <summary>True when one of this rail's enclosing rails is among <paramref name="present"/> and is also one of
    /// <paramref name="kinds"/>, so its energy is already counted there.</summary>
    internal static bool InsideAnother(PlatformRail rail, IReadOnlySet<string> present, IReadOnlyList<PlatformRail> table, Func<RailKind, bool> kinds)
    {
        var inside = rail.Inside;
        for (var depth = 0; inside is not null && depth < table.Count; depth++)
        {
            PlatformRail? outer = null;
            foreach (var candidate in table)
            {
                if (candidate.Key == inside) outer = candidate;
            }
            if (outer is not { } found) return false;
            if (present.Contains(found.Key) && kinds(found.Kind)) return true;
            inside = found.Inside;
        }
        return false;
    }
}
