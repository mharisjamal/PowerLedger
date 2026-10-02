using System.Diagnostics;
using System.IO;

namespace PowerLedger.App.Tests;

/// <summary>
/// Records a rectangle of the primary monitor on every frame Windows composes, for the motion proofs: each frame's
/// pixels (BGRA rows) and when Windows presented it. Desktop duplication of a monitor is one to a process, and the
/// engine holds this process's, so the recording runs in its own process: the recorder named by PL_RECORDER (a small
/// program that writes each frame's present time and pixels to a file). Our windows show in it only while they are not
/// left out of capture (LiquidGlassSources.ExcludeFromCapture false).
/// </summary>
internal sealed class ScreenRecorder : IDisposable
{
    private readonly Process _process;
    private readonly string _path;
    private bool _stopped;

    public ScreenRecorder(string recorder, string path, int x, int y, int width, int height, double seconds)
    {
        (Width, Height, _path) = (width, height, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path + ".stop");
        _process = Process.Start(new ProcessStartInfo(recorder, $"{x} {y} {width} {height} {seconds} \"{path}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
        })!;
        var ready = _process.StandardOutput.ReadLine();
        if (ready != "ready") throw new InvalidOperationException($"The recorder said: {ready}");
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The frames, once stopped: when Windows presented each (QPC), and its pixels.</summary>
    public List<(long Time, byte[] Pixels)> Frames { get; } = [];

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        File.WriteAllText(_path + ".stop", "");
        _process.WaitForExit(10000);
        using var reader = new BinaryReader(File.OpenRead(_path));
        var size = Width * Height * 4;
        while (reader.BaseStream.Position + 8 + size <= reader.BaseStream.Length) Frames.Add((reader.ReadInt64(), reader.ReadBytes(size)));
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
    }
}
