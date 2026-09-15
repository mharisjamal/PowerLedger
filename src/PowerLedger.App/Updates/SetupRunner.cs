using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace PowerLedger.App;

/// <summary>Runs a downloaded installer as the update (spec §13).</summary>
internal interface ISetupRunner
{
    /// <summary>Checks the installer once more, starts it, and returns setup's exit code when it ends. Setup closes the App
    /// before it installs, so this returns only when the update didn't go in. Throws <see cref="UpdateException"/> for a
    /// file that changed or went, and Win32Exception when Windows won't start it (<see cref="SetupRunner.Declined"/> for a
    /// declined permission prompt).</summary>
    Task<int> RunAsync(string installer, long size, byte[] sha256, string log, CancellationToken cancel);
}

internal sealed class SetupRunner : ISetupRunner
{
    /// <summary>ERROR_CANCELLED: the permission prompt was declined.</summary>
    public const int Declined = 1223;

    /// <summary>A progress window and no questions, no restart, the App opened again at the end (PowerLedger.iss's
    /// /UPDATE=1), and a log beside the installer.</summary>
    internal static string Arguments(string log) => $"/SILENT /NORESTART /UPDATE=1 /LOG=\"{log}\"";

    public async Task<int> RunAsync(string installer, long size, byte[] sha256, string log, CancellationToken cancel)
    {
        using var setup = Start(installer, size, sha256, log);
        await setup.WaitForExitAsync(cancel).ConfigureAwait(false);
        return setup.ExitCode;
    }

    /// <summary>Holds the installer open against writers from the check until setup is running, so nothing can swap it in
    /// between.</summary>
    private static Process Start(string installer, long size, byte[] sha256, string log)
    {
        FileStream hold;
        try
        {
            hold = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new UpdateException("The downloaded update is gone. PowerLedger downloads it again.", error);
        }
        using (hold)
        {
            if (hold.Length != size || !SHA256.HashData(hold).AsSpan().SequenceEqual(sha256))
                throw new UpdateException("The downloaded update changed on disk, so it wasn't run. PowerLedger downloads it again.");
            return Process.Start(new ProcessStartInfo(installer, Arguments(log)) { UseShellExecute = true })
                ?? throw new UpdateException("Windows didn't start setup.");
        }
    }
}
