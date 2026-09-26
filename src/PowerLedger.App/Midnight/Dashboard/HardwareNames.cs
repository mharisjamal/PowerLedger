using System.Management;
using System.Text.RegularExpressions;

namespace PowerLedger.App;

/// <summary>What each part of "Where the power went" is, in a few words: the processor's and graphics card's models, the
/// monitors' names, and the memory and drives behind the rest.</summary>
internal interface IHardwareNames
{
    /// <summary>A short name per part; a part Windows says nothing about is left out. Slow (WMI): call off the UI thread.</summary>
    IReadOnlyDictionary<Part, string> Read();
}

/// <summary>Reads the names from WMI, as any signed-in user may. Whatever a query can't answer is left out, never thrown.</summary>
internal sealed partial class HardwareNames : IHardwareNames
{
    private static readonly EnumerationOptions Quick = new() { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true };

    public IReadOnlyDictionary<Part, string> Read()
    {
        var names = new Dictionary<Part, string>();
        Add(names, Part.Cpu, Join(Query("root\\cimv2", "SELECT Name FROM Win32_Processor", o => Cpu(o["Name"] as string))));
        Add(names, Part.Gpu, Join(Query("root\\cimv2", "SELECT Name FROM Win32_VideoController", o => Gpu(o["Name"] as string))));
        Add(names, Part.Display, Join(Query("root\\wmi", "SELECT UserFriendlyName FROM WmiMonitorID", o => Monitor(o["UserFriendlyName"] as ushort[]))));
        Add(names, Part.Rest, Rest());
        return names;
    }

    /// <summary>"Intel(R) Core(TM) i7-8650U CPU @ 1.90GHz" as "Intel Core i7-8650U".</summary>
    internal static string? Cpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase);
        var at = text.IndexOf('@');
        if (at >= 0) text = text[..at];
        text = CpuWords().Replace(text, " ");
        return Spaces().Replace(text, " ").Trim();
    }

    /// <summary>A graphics card's name, or null for Windows' own stand-ins.</summary>
    internal static string? Gpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = Spaces().Replace(name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase), " ").Trim();
        return text.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) || text.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    /// <summary>A monitor's EDID name, which WMI gives as UTF-16 code units padded with zeros.</summary>
    internal static string? Monitor(ushort[]? units)
    {
        if (units is null) return null;
        var text = new string([.. units.TakeWhile(u => u != 0).Select(u => (char)u)]).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>"16 GB DDR4 RAM, Samsung SSD 980 1TB": the memory and up to two drives.</summary>
    private static string? Rest()
    {
        var parts = new List<string>();
        var sticks = Query("root\\cimv2", "SELECT Capacity, SMBIOSMemoryType FROM Win32_PhysicalMemory",
            o => (Bytes: Convert.ToDouble(o["Capacity"] ?? 0), Type: Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0)));
        var total = sticks.Sum(s => s.Bytes);
        if (total > 0) parts.Add(Memory(total, sticks.Select(s => s.Type).FirstOrDefault()));
        parts.AddRange(Query("root\\cimv2", "SELECT Model FROM Win32_DiskDrive WHERE MediaType LIKE 'Fixed%'", o => (o["Model"] as string)?.Trim())
            .OfType<string>().Where(m => m.Length > 0).Distinct().Take(2));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>"16 GB DDR4 RAM" from the sticks' bytes together and their SMBIOS memory type.</summary>
    internal static string Memory(double bytes, int smbiosType)
    {
        var gb = Math.Round(bytes / (1024d * 1024 * 1024));
        var kind = smbiosType switch { 24 => "DDR3 ", 26 => "DDR4 ", 29 => "LPDDR3 ", 30 => "LPDDR4 ", 34 => "DDR5 ", 35 => "LPDDR5 ", _ => "" };
        return $"{gb:0} GB {kind}RAM";
    }

    private static void Add(Dictionary<Part, string> names, Part part, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) names[part] = name;
    }

    private static string? Join(IEnumerable<string?> names)
    {
        var list = names.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    private static List<T> Query<T>(string scope, string query, Func<ManagementBaseObject, T> read)
    {
        var found = new List<T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), Quick);
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    try
                    {
                        found.Add(read(item));
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        // One odd object doesn't lose the others.
                    }
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // WMI off, the class missing, or too slow: this part goes without a name.
        }
        return found;
    }

    [GeneratedRegex(@"\b(CPU|Processor)\b|\d+-Core\b", RegexOptions.IgnoreCase)]
    private static partial Regex CpuWords();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
