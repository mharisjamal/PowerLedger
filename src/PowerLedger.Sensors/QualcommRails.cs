namespace PowerLedger.Sensors;

/// <summary>One row of <see cref="QualcommRails.Table"/>.</summary>
/// <param name="Key">The rail's name lowercased, with everything but letters and digits and any trailing number taken off,
/// so "cpu_cluster_0", "CPU_CLUSTER_1" and "cpu cluster 2" are all "cpucluster".</param>
/// <param name="Kind">What the rail measures.</param>
/// <param name="Inside">The key of the rail whose energy already holds this one's, or null for the outermost rail. A rail is
/// never added to a rail it is inside, so nothing is counted twice.</param>
public readonly record struct PlatformRail(string Key, RailKind Kind, string? Inside);

/// <summary>
/// The Energy Meter rails a Snapdragon X machine publishes, and how they nest. UNVERIFIED ON REAL HARDWARE: no PowerLedger
/// test has run on a Snapdragon machine. The names come from npu-watt (github.com/hotschmoe/npu-watt, src/lib.rs, which reads
/// <c>\Energy Meter(*)\Energy</c> and lowercases the instance names: <c>npu</c>, <c>gpu</c>, <c>cpu_cluster_*</c>,
/// <c>memory</c>, <c>soc</c>, <c>system</c>), and its note that "system includes the display and platform overhead"; it also
/// takes the system rail as the machine's draw on AC, which is why that rail is read as the load behind the charger rather
/// than what the adapter delivers (<see cref="SystemHoldsCharging"/>). The nesting below is the research's reading of those
/// names (PowerLedger research "measure more", 2026-09-30): the CPU clusters, the graphics and the NPU sit inside the soc,
/// and the soc and memory inside the system rail. Whether soc really holds the clusters and the graphics, and whether the X1
/// and X2 agree, needs one device dump: compare the system rail with the battery's discharge on battery, and the soc rail with
/// the sum of its parts. A device that disagrees is corrected here, in this one table, and nowhere else.
/// </summary>
public static class QualcommRails
{
    public static readonly IReadOnlyList<PlatformRail> Table =
    [
        new("system", RailKind.Platform, Inside: null),
        new("soc", RailKind.Package, Inside: "system"),
        new("memory", RailKind.Memory, Inside: "system"),
        new("cpucluster", RailKind.Cores, Inside: "soc"),
        new("gpu", RailKind.IntegratedGpu, Inside: "soc"),
        new("npu", RailKind.Npu, Inside: "soc"),
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

    /// <summary>The table's row for a counter instance name, or null when the name is none of these rails.</summary>
    public static PlatformRail? Find(string name, IReadOnlyList<PlatformRail> table)
    {
        var key = Key(name);
        foreach (var rail in table)
        {
            if (rail.Key == key) return rail;
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
