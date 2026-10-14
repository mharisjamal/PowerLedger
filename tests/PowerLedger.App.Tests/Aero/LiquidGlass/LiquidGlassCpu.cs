using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Xunit.Abstractions;

namespace PowerLedger.App.Tests;

/// <summary>
/// The engine spec's CPU measurement (manual): this process's CPU as a share of one core, read every second for 30 s
/// after 10 s to settle, with the Aero window or the watts overlay on the real screen, live glass on or none. Behind it a
/// still picture (rest: the mockup's background, a window over the whole primary monitor) or the intro video looping in
/// its own process (video). Also counted: the capture thread's wakeups (its context switches) and the frames taken and
/// drawn. Opt in with PL_CPU_DIR, a folder the results are added to (cpu.txt); PL_CPU_ENERGY_SAVER=1 runs it as if
/// Windows' Energy Saver were on. Run one scene at a time:
/// <c>dotnet test -c Release --filter "FullyQualifiedName~LiquidGlassCpu&amp;DisplayName~main-rest&amp;DisplayName~True"</c>.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class LiquidGlassCpu(ITestOutputHelper output)
{
    private static string? Folder => Environment.GetEnvironmentVariable("PL_CPU_DIR") is { Length: > 0 } dir ? dir : null;

    [Theory]
    [InlineData("main-rest", true)]
    [InlineData("main-rest", false)]
    [InlineData("main-video", true)]
    [InlineData("main-video", false)]
    [InlineData("overlay-rest", true)]
    [InlineData("overlay-video", true)]
    [InlineData("hidden", true)]
    public void Cpu(string scene, bool glass)
    {
        if (Folder is not { } dir) return;
        Directory.CreateDirectory(dir);
        var line = UiHarness.OnUi(() => ScreenCapture.Aware(() => Measure(scene, glass)));
        output.WriteLine(line);
        File.AppendAllLines(Path.Combine(dir, "cpu.txt"), [line]);
    }

    /// <summary>
    /// The before and after picture for an engine change that must leave the look alone (manual, PL_CPU_DIR): the Aero
    /// window on the real screen, its glass reading a still picture (MonitorCapture.StillPicture, the same picture on a
    /// window over the whole monitor), so our windows can be grabbed. A band of another window then slides up and down
    /// behind it, reaching past it, ten times, and goes: each step a change another window made under part of the glass,
    /// drawn with no compare (a frame of a few pieces), the picture behind unchanged. The window's rectangle is grabbed
    /// with BitBlt into render-PL_CPU_LABEL.png, to be diffed against another build's.
    /// </summary>
    [Fact]
    public void Grab()
    {
        if (Folder is not { } dir) return;
        Directory.CreateDirectory(dir);
        var line = UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var picture = Picture();
            MonitorCapture.StillPicture = picture;
            LiquidGlassSources.ExcludeFromCapture = false;
            var image = new Image { Source = picture, Stretch = Stretch.None };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            var desk = ScreenCapture.Show(new Border { Background = Brushes.Black, Child = image }, 0, 0, 10, 10);
            var scale = VisualTreeHelper.GetDpi(desk).DpiScaleX;
            (desk.Left, desk.Top, desk.Width, desk.Height) = (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            (image.Width, image.Height) = (picture.PixelWidth / scale, picture.PixelHeight / scale);
            var band = ScreenCapture.Show(new Border { Background = Brushes.OrangeRed }, 0, 200, SystemParameters.PrimaryScreenWidth, 120);
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver), Theme.Dark);
            LiquidGlassSources.Override = null;
            (window.Left, window.Top, window.Width, window.Height, window.Topmost) = (60, 40, 1100, 700, true);
            window.Show();
            var partial = typeof(GpuGlassWindow).GetField("PartialPresents", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            try
            {
                UiHarness.Pump(TimeSpan.FromSeconds(5));   // the reveal, and the glass's first frames
                var before = (long?)partial?.GetValue(null);
                for (var i = 0; i < 10; i++)
                {
                    band.Top = 200 + 30 * (i % 5);
                    UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                }
                band.Close();
                UiHarness.Pump(TimeSpan.FromSeconds(1.5));
                DwmFlush();
                int x = (int)Math.Round(window.Left * scale), y = (int)Math.Round(window.Top * scale), w = (int)Math.Round(window.ActualWidth * scale), h = (int)Math.Round(window.ActualHeight * scale);
                var pixels = ScreenCapture.Grab(x, y, w, h);
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                var file = Path.Combine(dir, $"render-{Environment.GetEnvironmentVariable("PL_CPU_LABEL")}.png");
                LiquidGlassProofTests.Write(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4), file);
                var presents = before is { } b ? $"{(long)partial!.GetValue(null)! - b} presents of their own rectangles" : "no partial presents in this build";
                return FormattableString.Invariant($"{DateTime.Now:yyyy-MM-dd HH:mm} {Environment.GetEnvironmentVariable("PL_CPU_LABEL")} grab {w}x{h} at {scale}: {file}; {presents}");
            }
            finally
            {
                window.CloseForSwitch();
                if (band.IsVisible) band.Close();
                desk.Close();
                MonitorCapture.StillPicture = null;
                LiquidGlassSources.ExcludeFromCapture = true;
                UiHarness.NoCapture();
            }
        }));
        output.WriteLine(line);
        File.AppendAllLines(Path.Combine(dir, "cpu.txt"), [line]);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private static string Measure(string scene, bool glass)
    {
        var video = scene.EndsWith("-video", StringComparison.Ordinal);
        Process? player = null;
        Window? desk = null;
        Window? shown = null;
        var energySaver = Environment.GetEnvironmentVariable("PL_CPU_ENERGY_SAVER") == "1";
        try
        {
            if (video)
            {
                player = Play(60, 40, 1100, 700);
            }
            else
            {
                var picture = Picture();
                var image = new Image { Source = picture, Stretch = Stretch.UniformToFill };
                desk = ScreenCapture.Show(new Border { Background = Brushes.Black, Child = image }, 0, 0, 10, 10);
                (desk.Left, desk.Top, desk.Width, desk.Height) = (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            }
            UiHarness.Pump(TimeSpan.FromSeconds(1));
            var saver = new FakeSaver();
            if (scene.StartsWith("overlay", StringComparison.Ordinal))
            {
                var now = MidnightFixtures.NowScreen(out _);
                var settings = MidnightFixtures.SettingsScreen(new FakeUiSettings());
                var overlay = AeroHost.Dressed(new OverlayWindow(now, settings) { Placing = false, Left = 400, Top = 300 }, Theme.Dark);
                if (glass) LiquidGlassSources.Override = null;
                overlay.Apply(OverlaySettings.Default with { Enabled = true });
                overlay.ShowOverlay();
                shown = overlay;
            }
            else
            {
                var window = AeroHost.Window(AeroFixtures.Shell(saver), Theme.Dark);
                if (glass) LiquidGlassSources.Override = null;
                (window.Left, window.Top, window.Width, window.Height, window.Topmost) = (60, 40, 1100, 700, true);
                window.Show();
                shown = window;
            }
            if (energySaver) GlassPowerForTests(true);
            UiHarness.Pump(TimeSpan.FromSeconds(4));
            if (scene == "hidden") shown.Hide();
            UiHarness.Pump(TimeSpan.FromSeconds(6));   // the runtime's tiered compilation settles

            using var process = Process.GetCurrentProcess();
            var capture = MonitorCapture.ThreadIds.ToArray();
            var shares = new List<double>();
            var (threads0, acquired0, drawn0) = (Threads(), Acquired(), Drawn());
            var (presents0, copied0) = (Counter("Presents"), Counter("PixelsCopied"));
            QueryProcessCycleTime(process.Handle, out var cycles0);
            var captureCycles0 = capture.Sum(ThreadCycles);
            var cpu0 = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            var (cpu, at) = (cpu0, TimeSpan.Zero);
            for (var i = 0; i < 30; i++)
            {
                UiHarness.Pump(TimeSpan.FromSeconds(1));
                var (cpuNow, atNow) = (process.TotalProcessorTime, clock.Elapsed);
                shares.Add((cpuNow - cpu) / (atNow - at) * 100);
                (cpu, at) = (cpuNow, atNow);
            }
            var seconds = clock.Elapsed.TotalSeconds;
            var mean = (process.TotalProcessorTime - cpu0).TotalSeconds / seconds * 100;
            QueryProcessCycleTime(process.Handle, out var cycles1);
            var captureCycles = (capture.Sum(ThreadCycles) - captureCycles0) / seconds / 1e6;
            var threads1 = Threads();
            // Each thread's own count since the start (a thread new since then, all of its own): threads come and go.
            double Woken(Func<int, bool> which) => threads1.Where(t => which(t.Key)).Sum(t => t.Value - threads0.GetValueOrDefault(t.Key)) / seconds;
            var wakes = Woken(id => Array.IndexOf(capture, id) >= 0);
            var allWakes = Woken(_ => true);
            var state = string.Join(" | ", LiquidGlassSources.Live.OfType<WindowGlassSource>().Select(s => s.Gpu?.State.Split(';')[0] ?? s.Kind.ToString()));
            var taken = (Acquired() - acquired0) / seconds;
            var frames = (Drawn() - drawn0) / seconds;
            var (presents1, copied1) = (Counter("Presents"), Counter("PixelsCopied"));
            var copy = presents1 is not { } p1 || copied1 is not { } c1 ? "the whole layer copied each present"
                : p1 == presents0 ? "no presents" : FormattableString.Invariant($"{(c1 - copied0!.Value) * 4.0 / (p1 - presents0!.Value) / 1e6:0.00} MB copied a present");
            return FormattableString.Invariant(
                $"{DateTime.Now:yyyy-MM-dd HH:mm} {Environment.GetEnvironmentVariable("PL_CPU_LABEL")} {scene} glass {(glass ? "on" : "none")}{(energySaver ? " energy-saver" : "")}: CPU {mean:0.00} % (seconds {shares.Min():0.0} to {shares.Max():0.0}), {(cycles1 - cycles0) / seconds / 1e6:0.0} M cycles/s (capture thread {captureCycles:0.00}); wakeups: capture thread {wakes:0.0}/s, process {allWakes:0.0}/s; frames taken {taken:0.0}/s, drawn {frames:0.0}/s, {copy}; {state}");
        }
        finally
        {
            if (energySaver) GlassPowerForTests(false);
            if (shown is AeroWindow aero) aero.CloseForSwitch();
            else shown?.Close();
            desk?.Close();
            if (player is { HasExited: false })
            {
                player.Kill();
                player.Dispose();
            }
            UiHarness.NoCapture();
            UiHarness.Pump(TimeSpan.FromMilliseconds(500));
        }
    }

    /// <summary>Energy Saver as Windows would report it, for the engine (reflection: the switch is newer than the
    /// harness, which also measures the builds before it, where this does nothing).</summary>
    private static void GlassPowerForTests(bool on)
        => typeof(MonitorCapture).Assembly.GetType("PowerLedger.App.Aero.GlassPower")?.GetProperty("EnergySaver")?.SetValue(null, on);

    /// <summary>A counter of GpuGlassWindow's by name; null in a build without it.</summary>
    private static long? Counter(string name)
        => (long?)typeof(GpuGlassWindow).GetField(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);

    private static long Acquired()
    {
        long sum = 0;
        foreach (var source in LiquidGlassSources.Live.OfType<WindowGlassSource>()) sum += source.Session?.FramesAcquired ?? 0;
        return sum;
    }

    private static long Drawn()
    {
        long sum = 0;
        foreach (var source in LiquidGlassSources.Live.OfType<WindowGlassSource>()) sum += source.Gpu?.Frames ?? 0;
        return sum;
    }

    /// <summary>The mockup's background where the proofs keep it, else a plain gradient: what lies still behind.</summary>
    private static BitmapSource Picture()
    {
        var file = Environment.GetEnvironmentVariable("PL_PROOF_DIR") is { Length: > 0 } proof ? Path.Combine(proof, "ref", "Main-bg-125.png") : "";
        if (File.Exists(file)) return LiquidGlassProofTests.Load(file);
        return FakeGlassSource.Picture(1920, 1080, (x, y) => ((byte)(x / 8), (byte)(y / 5), 120));
    }

    /// <summary>The intro video looping, muted, in a topmost window of its own process (PowerShell's WPF), at the given
    /// device-independent place: what plays behind the glass.</summary>
    private static Process Play(int x, int y, int width, int height)
    {
        var mp4 = Path.Combine(AppContext.BaseDirectory, "Assets", "Intro", "aero-intro.mp4");
        var script = Path.Combine(Path.GetTempPath(), "powerledger-cpu-video.ps1");
        File.WriteAllText(script, $$"""
            Add-Type -AssemblyName PresentationFramework
            $win = New-Object System.Windows.Window
            $win.WindowStyle = 'None'; $win.ResizeMode = 'NoResize'; $win.ShowInTaskbar = $false; $win.ShowActivated = $false; $win.Topmost = $true
            $win.WindowStartupLocation = 'Manual'; $win.Left = {{x}}; $win.Top = {{y}}; $win.Width = {{width}}; $win.Height = {{height}}; $win.Background = 'Black'
            $media = New-Object System.Windows.Controls.MediaElement
            $media.Source = New-Object Uri '{{mp4}}'
            $media.LoadedBehavior = 'Manual'; $media.Stretch = 'UniformToFill'; $media.IsMuted = $true
            $media.Add_MediaEnded({ $media.Position = [TimeSpan]::Zero; $media.Play() })
            $win.Content = $media
            $win.Add_Loaded({ $media.Play() })
            $app = New-Object System.Windows.Application
            $app.Run($win) | Out-Null
            """);
        var player = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -STA -File \"{script}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        UiHarness.Pump(TimeSpan.FromSeconds(3));
        return player;
    }

    /// <summary>Each thread of this process and its context switches so far: each is the thread waking.</summary>
    private static Dictionary<int, long> Threads()
    {
        var size = 1 << 20;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(5, buffer, size, out var needed);   // SystemProcessInformation
                if (status == unchecked((int)0xC0000004))   // STATUS_INFO_LENGTH_MISMATCH
                {
                    size = Math.Max(size * 2, needed + 65536);
                    continue;
                }
                var threads = new Dictionary<int, long>();
                if (status < 0) return threads;
                // x64 SYSTEM_PROCESS_INFORMATION: NextEntryOffset at 0, NumberOfThreads at 4, UniqueProcessId at 0x50, its
                // SYSTEM_THREAD_INFORMATION entries (0x50 bytes each) from 0x100: UniqueThread at 48, ContextSwitches at 64.
                for (var entry = buffer; ; )
                {
                    var next = Marshal.ReadInt32(entry);
                    if ((long)Marshal.ReadIntPtr(entry, 0x50) == Environment.ProcessId)
                    {
                        for (var t = 0; t < Marshal.ReadInt32(entry, 4); t++)
                        {
                            var thread = entry + 0x100 + t * 0x50;
                            threads[(int)(long)Marshal.ReadIntPtr(thread, 48)] = (uint)Marshal.ReadInt32(thread, 64);
                        }
                        return threads;
                    }
                    if (next == 0) return threads;
                    entry += next;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>A thread of this process's CPU cycles so far; 0 once it is gone.</summary>
    private static long ThreadCycles(int id)
    {
        var handle = OpenThread(0x0800, false, id);   // THREAD_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return 0;
        try
        {
            return QueryThreadCycleTime(handle, out var cycles) ? (long)cycles : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(int access, bool inherit, int id);

    [DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);
}
