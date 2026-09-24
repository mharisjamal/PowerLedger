using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>Where the service looks for NVIDIA's nvml.dll, and where it never does (feedback issue #4: an R390 driver
/// for a Fermi Quadro puts it only under Program Files).</summary>
public class NvmlLibraryTests
{
    private const string System32 = @"C:\Windows\System32";
    private const string ProgramFiles = @"C:\Program Files";
    private const string DchFolder = @"C:\Windows\System32\DriverStore\FileRepository\nvdmi.inf_amd64_ed02e74d55d2ca32";

    [Fact]
    public void System32_comes_first_then_nvsmi_then_the_display_drivers_own_folder()
    {
        NvmlLibrary.Candidates(System32, ProgramFiles, [DchFolder]).ShouldBe(
        [
            @"C:\Windows\System32\nvml.dll",
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll",
            DchFolder + @"\nvml.dll",
        ]);
    }

    [Fact]
    public void The_first_place_that_has_it_and_loads_it_wins()
    {
        var candidates = NvmlLibrary.Candidates(System32, ProgramFiles, [DchFolder]);
        var asked = new List<string>();

        // An R390 driver: nothing in System32, the library under NVSMI.
        var handle = NvmlLibrary.Probe(candidates, path => { asked.Add(path); return path.Contains("NVSMI"); }, _ => 42);
        handle.ShouldBe(42);
        asked.ShouldBe([@"C:\Windows\System32\nvml.dll", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll"]);

        // A copy that will not load is passed over for the next.
        var loaded = new List<string>();
        NvmlLibrary.Probe(candidates, _ => true, path => { loaded.Add(path); return path.StartsWith(DchFolder) ? 7 : 0; }).ShouldBe(7);
        loaded.ShouldBe(candidates);

        NvmlLibrary.Probe(candidates, _ => false, _ => throw new InvalidOperationException("never loaded")).ShouldBe(IntPtr.Zero);
    }

    [Theory]
    [InlineData(@"drivers")]                                                                    // relative: the current directory
    [InlineData(@"C:\Users\someone\Downloads")]                                                 // writable by a user
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"\\server\share\nvidia")]                                                      // another machine
    [InlineData(@"\\?\C:\Windows\System32\DriverStore\FileRepository\nv.inf_amd64_1")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\..\..\..\..\Users\someone")]   // climbs out
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository")]                             // the store itself, not a driver's folder
    [InlineData(@"C:\Windows\System32\DriverStore")]
    [InlineData(@"C:/Windows/System32/DriverStore/FileRepository/nv.inf_amd64_1")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepositoryX\nv.inf_amd64_1")]
    public void A_driver_folder_outside_the_driver_store_is_never_looked_in(string folder)
        => NvmlLibrary.Candidates(System32, ProgramFiles, [folder]).ShouldBe(
            [@"C:\Windows\System32\nvml.dll", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll"]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"Program Files")]
    [InlineData(@"\\server\Program Files")]
    public void Without_a_real_program_files_folder_nvsmi_is_skipped(string? programFiles)
        => NvmlLibrary.Candidates(System32, programFiles, []).ShouldBe([@"C:\Windows\System32\nvml.dll"]);

    [Fact]
    public void Every_candidate_is_an_absolute_path_to_nvml_dll()
    {
        var candidates = NvmlLibrary.Candidates(System32, ProgramFiles, [DchFolder, "x", @"C:\Temp"]);

        candidates.ShouldAllBe(path => Path.IsPathFullyQualified(path) && Path.GetFileName(path) == "nvml.dll");
        candidates.ShouldNotContain("nvml.dll");
    }

    [Fact]
    public void The_driver_folders_are_those_of_nvidia_display_adapters_only()
    {
        // As the display adapter class keys hold them on the development laptop, plus an older driver and a stranger.
        var folders = NvmlLibrary.DriverFolders(
        [
            new DisplayDriverKey("Intel Corporation", @"PCI\VEN_8086&DEV_9A49&SUBSYS_0A251028",
                [@"C:\WINDOWS\System32\DriverStore\FileRepository\iigd_dch.inf_amd64_54754d77b2c2ee59\igdumdim64.dll"]),
            new DisplayDriverKey("NVIDIA", @"pci\ven_10de&dev_1d16&subsys_0a251028",
            [
                DchFolder + @"\nvldumdx.dll",
                DchFolder + @"\nvldumdx.dll",
            ]),
            new DisplayDriverKey("NVIDIA", @"pci\ven_10de&dev_06d8", ["nvd3dumx.dll", "nvwgf2umx.dll"]),     // pre-DCH: bare names
            new DisplayDriverKey(null, @"pci\ven_10de&dev_1b80", [@"C:\WINDOWS\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_1\nvldumdx.dll"]),
            new DisplayDriverKey("Advanced Micro Devices, Inc.", @"pci\ven_1002&dev_73bf", [@"C:\WINDOWS\System32\DriverStore\FileRepository\u0123.inf_amd64_2\aticfx64.dll"]),
        ]);

        folders.ShouldBe([DchFolder, @"C:\WINDOWS\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_1"], ignoreOrder: false);
    }

    [Fact]
    public void Only_nvml_is_resolved_here_and_every_other_library_is_left_to_the_runtime()
    {
        var probes = 0;
        IntPtr Probe()
        {
            probes++;
            return 99;
        }

        NvmlLibrary.Resolve("dxgi.dll", Probe).ShouldBe(IntPtr.Zero);
        NvmlLibrary.Resolve("cfgmgr32.dll", Probe).ShouldBe(IntPtr.Zero);
        probes.ShouldBe(0);

        NvmlLibrary.Resolve("nvml.dll", Probe).ShouldBe(99);
        NvmlLibrary.Resolve("NVML.DLL", Probe).ShouldBe(99);
        NvmlLibrary.Resolve("nvml", Probe).ShouldBe(99);
        probes.ShouldBe(3);
    }
}
