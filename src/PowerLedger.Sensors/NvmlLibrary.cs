using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace PowerLedger.Sensors;

/// <summary>An NVIDIA display adapter's driver as its class key in the registry describes it.</summary>
/// <param name="Provider">ProviderName, e.g. "NVIDIA".</param>
/// <param name="MatchingDeviceId">The hardware id the driver was installed for, e.g. "pci\ven_10de&amp;dev_1d16".</param>
/// <param name="UserModeDrivers">UserModeDriverName: bare file names for an older driver, absolute paths into the
/// driver's DriverStore folder for a DCH one.</param>
internal readonly record struct DisplayDriverKey(string? Provider, string? MatchingDeviceId, IReadOnlyList<string> UserModeDrivers);

/// <summary>
/// Finds NVIDIA's management library, nvml.dll, wherever the display driver put it. Drivers from about R418 on copy it
/// into System32. Older ones, among them R390, the last for Fermi cards such as the Quadro 6000, leave it only in
/// "%ProgramW6432%\NVIDIA Corporation\NVSMI". A DCH driver keeps one in its own DriverStore folder as well, which is
/// found from the display adapter's class key (HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-...}\NNNN): its
/// UserModeDriverName lists the driver's user-mode DLLs by absolute path into that folder, which is exactly the folder
/// Windows loads the driver itself from, where the InfPath value names only the published "oemNN.inf".
/// Only absolute paths inside folders that need an administrator to write to are ever tried: System32, Program Files
/// and the DriverStore. Never the current directory or the PATH, because whatever loads here runs inside a LocalSystem
/// service. NVML has no 32-bit version, and the 64-bit Program Files is what %ProgramW6432% names.
/// </summary>
internal static class NvmlLibrary
{
    internal const string FileName = "nvml.dll";

    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string NvidiaHardwareId = @"pci\ven_10de";

    private static readonly Lock Gate = new();
    private static bool _registered;
    private static IntPtr _handle;

    /// <summary>Answers the runtime's request for nvml.dll from here on, for this assembly's DllImports. Once is enough; later
    /// calls do nothing.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered) return;
            _registered = true;
            NativeLibrary.SetDllImportResolver(typeof(NvmlLibrary).Assembly, (name, _, _) => Resolve(name, Cached));
        }
    }

    /// <summary>A handle for nvml.dll from <paramref name="probe"/>; zero for any other library, and for nvml.dll when none
    /// was found, which leaves both to the runtime's own search: System32 alone, as the DllImports ask.</summary>
    internal static IntPtr Resolve(string libraryName, Func<IntPtr> probe)
        => string.Equals(libraryName, FileName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(libraryName, Path.GetFileNameWithoutExtension(FileName), StringComparison.OrdinalIgnoreCase)
            ? probe()
            : IntPtr.Zero;

    /// <summary>Where to look, in order: System32, NVSMI under Program Files, then each NVIDIA display driver's own folder.
    /// A place that is not an absolute path inside one of those folders is left out.</summary>
    internal static IReadOnlyList<string> Candidates(string systemDirectory, string? programFiles, IEnumerable<string> driverFolders)
    {
        var candidates = new List<string>();
        void Add(string? folder)
        {
            if (folder is null) return;
            var path = folder.TrimEnd('\\') + @"\" + FileName;
            if (IsProtected(path, systemDirectory, programFiles) && !candidates.Contains(path, StringComparer.OrdinalIgnoreCase)) candidates.Add(path);
        }

        Add(systemDirectory);
        if (IsLocalAbsolute(programFiles)) Add(Path.Combine(programFiles!, "NVIDIA Corporation", "NVSMI"));
        foreach (var folder in driverFolders) Add(folder);
        return candidates;
    }

    /// <summary>The first candidate that exists and loads, or zero.</summary>
    internal static IntPtr Probe(IEnumerable<string> candidates, Func<string, bool> exists, Func<string, IntPtr> load)
    {
        foreach (var path in candidates)
        {
            if (!exists(path)) continue;
            var handle = load(path);
            if (handle != IntPtr.Zero) return handle;
        }
        return IntPtr.Zero;
    }

    /// <summary>The folders NVIDIA display drivers were installed to, from their class keys: the folder of each user-mode
    /// driver given by absolute path. An older driver names its DLLs bare, from System32, and adds nothing here.</summary>
    internal static IReadOnlyList<string> DriverFolders(IEnumerable<DisplayDriverKey> adapters)
    {
        var folders = new List<string>();
        foreach (var adapter in adapters)
        {
            var nvidia = adapter.Provider?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true
                         || adapter.MatchingDeviceId?.StartsWith(NvidiaHardwareId, StringComparison.OrdinalIgnoreCase) == true;
            if (!nvidia) continue;
            foreach (var driver in adapter.UserModeDrivers)
            {
                if (!IsLocalAbsolute(driver) || Path.GetDirectoryName(driver) is not { Length: > 0 } folder) continue;
                if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);
            }
        }
        return folders;
    }

    /// <summary>True for nvml.dll directly in System32, directly in Program Files' NVSMI folder, or in a driver's folder
    /// in the DriverStore, given as a plain absolute path with nothing to climb out of it.</summary>
    private static bool IsProtected(string path, string systemDirectory, string? programFiles)
    {
        if (!IsLocalAbsolute(path) || !IsLocalAbsolute(systemDirectory)) return false;
        if (!string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase)) return false;   // "..", "/" or "."
        if (!string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase)) return false;

        var folder = Path.GetDirectoryName(path) ?? "";
        if (Same(folder, systemDirectory)) return true;
        if (IsLocalAbsolute(programFiles) && Same(folder, Path.Combine(programFiles!, "NVIDIA Corporation", "NVSMI"))) return true;
        var store = Path.Combine(systemDirectory, "DriverStore", "FileRepository") + @"\";
        return folder.StartsWith(store, StringComparison.OrdinalIgnoreCase) && folder.Length > store.Length;
    }

    /// <summary>An absolute path on a drive of this machine: not relative, not another machine's share, not a device path.</summary>
    private static bool IsLocalAbsolute(string? path)
        => !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal)
           && !path.StartsWith("//", StringComparison.Ordinal);

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The library, looked for once it is asked for and kept once found. Not finding it is not kept, so a driver
    /// installed while the service runs is found when the sensors are next built.</summary>
    private static IntPtr Cached()
    {
        lock (Gate)
        {
            if (_handle != IntPtr.Zero) return _handle;
            var candidates = Candidates(
                Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), DriverFolders(DisplayDrivers()));
            _handle = Probe(candidates, File.Exists, static path => NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero);
            return _handle;
        }
    }

    /// <summary>Every display adapter's class key. Nothing when the registry will not answer.</summary>
    internal static List<DisplayDriverKey> DisplayDrivers()
    {
        var drivers = new List<DisplayDriverKey>();
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (adapters is null) return drivers;
            foreach (var name in adapters.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;   // "0000", not "Configuration" or "Properties"
                using var adapter = adapters.OpenSubKey(name);
                if (adapter is null) continue;
                IReadOnlyList<string> modules = adapter.GetValue("UserModeDriverName") switch
                {
                    string[] many => many,
                    string one => [one],
                    _ => [],
                };
                drivers.Add(new DisplayDriverKey(adapter.GetValue("ProviderName") as string, adapter.GetValue("MatchingDeviceId") as string, modules));
            }
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return drivers;
    }
}
