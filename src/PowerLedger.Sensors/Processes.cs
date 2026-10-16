using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <summary>One process at one moment, as NtQuerySystemInformation lists it.</summary>
/// <param name="CreateTime">When it started, as a FILETIME: with the id, it tells a process from a later one given the
/// same id.</param>
/// <param name="Cycles">The processor cycles its threads have used so far.</param>
/// <param name="Time">The kernel and user time its threads have used so far, in 100 ns units.</param>
/// <param name="Name">Where its image name is, for <see cref="IProcessSource.ImageName"/> until the next read.</param>
public readonly record struct ProcessCounts(uint Pid, long CreateTime, uint Session, ulong Cycles, long Time, int Name);

/// <summary>Every logical processor's times added up since boot, in 100 ns units.</summary>
public readonly record struct ProcessorTimes(long Idle, long Kernel, long User, long Dpc, long Interrupt)
{
    /// <summary>Kernel and user time less idle time: kernel time holds idle time, as in GetSystemTimes.</summary>
    public long Busy => Kernel + User - Idle;

    /// <summary>What drivers' deferred procedure calls and interrupts took, which Windows charges to no process.</summary>
    public long Drivers => Dpc + Interrupt;
}

/// <summary>A process's image path and Store package family, and the SID of the account it runs as; any may be missing.</summary>
public sealed record ProcessImage(string? Path, string? PackageFamily, string? UserSid = null);

/// <summary>What the Apps page asks Windows about processes (Plan AE), so tests can stand in for it. Single-threaded.</summary>
public interface IProcessSource : IDisposable
{
    /// <summary>Fills <paramref name="into"/> with every process now, and gives the processors' times; false when Windows
    /// would not answer.</summary>
    bool Read(List<ProcessCounts> into, out ProcessorTimes processors);

    /// <summary>The image name a process in the last read was listed with, e.g. "chrome.exe" or "Registry".</summary>
    string ImageName(int name);

    /// <summary>The process's image and package, or null when it cannot be opened or the id names another process now.</summary>
    ProcessImage? Image(uint pid, long createTime);

    /// <summary>What the App calls an image: its description, else its product name, else its file name.</summary>
    string DisplayName(string path);

    /// <summary>The Windows folder in lower case, ending in a backslash.</summary>
    string WindowsFolder { get; }
}

/// <summary>Where SYSTEM_PROCESS_INFORMATION keeps what the Apps page reads, for a 64-bit process (x64 and Arm64) and a
/// 32-bit one: pointers and handles are the process's width.</summary>
internal readonly record struct ProcessLayout(int Cycles, int Create, int User, int Kernel, int NameLength, int NameBuffer, int Pid, int Session, bool Wide)
{
    public static ProcessLayout SixtyFourBit { get; } = new(24, 32, 40, 48, 56, 64, 80, 100, Wide: true);

    public static ProcessLayout ThirtyTwoBit { get; } = new(24, 32, 40, 48, 56, 60, 68, 80, Wide: false);

    public static ProcessLayout Current => Environment.Is64BitProcess ? SixtyFourBit : ThirtyTwoBit;

    /// <summary>The least an entry holds, up to its session id.</summary>
    public int Size => Session + 4;
}

/// <summary>
/// The Windows answers, for the service's thread (Plan AE). One NtQuerySystemInformation call lists every process with its
/// cycles, times, session and start, into a buffer kept and grown as needed, so a read allocates nothing; a second call
/// gives the processors' busy, interrupt and deferred-call times, as GetSystemTimes does but with the drivers' share.
/// A process's image is asked only once it is new, with the least access there is.
/// </summary>
public sealed unsafe class WindowsProcesses : IProcessSource
{
    private const int SystemProcessInformation = 5;
    private const int SystemProcessorPerformanceInformation = 8;
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);
    private const int ProcessorEntry = 48;
    private const int ProcessorsPerGroup = 64;
    private const int MostBytes = 64 << 20;
    private const uint QueryLimitedInformation = 0x1000;
    private const int InsufficientBuffer = 122;

    private static readonly ProcessLayout Layout = ProcessLayout.Current;

    private readonly ushort _groups = Math.Max((ushort)1, GetActiveProcessorGroupCount());
    private byte* _processors = (byte*)NativeMemory.Alloc(ProcessorEntry * ProcessorsPerGroup);
    private char[] _chars = new char[1024];
    private byte* _buffer;
    private int _size;
    private int _used;
    private bool _disposed;

    public string WindowsFolder { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\').ToLowerInvariant() + "\\";

    public bool Read(List<ProcessCounts> into, out ProcessorTimes processors)
    {
        into.Clear();
        processors = default;
        if (_disposed || !QueryProcesses() || !QueryProcessors(out processors)) return false;
        Parse(new ReadOnlySpan<byte>(_buffer, _used), Layout, into);
        return true;
    }

    public string ImageName(int name) => _buffer is null ? "" : ImageName(new ReadOnlySpan<byte>(_buffer, _used), Layout, (long)_buffer, name);

    public ProcessImage? Image(uint pid, long createTime)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == 0) return null;
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _) || created != createTime) return null;
            return new ProcessImage(ImagePath(handle), PackageFamily(handle), UserSid(handle));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public string DisplayName(string path)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        if (Remote(path)) return file;                      // a share that went away would hold the service's thread
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return Clean(info.FileDescription) ?? Clean(info.ProductName) ?? file;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return file;
        }
    }

    /// <summary>Frees the buffers, once however often it is called (review item 15); a read after it answers nothing.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMemory.Free(_buffer);
        _buffer = null;
        _used = 0;
        NativeMemory.Free(_processors);
        _processors = null;
    }

    /// <summary>Every entry in <paramref name="buffer"/>, each one's offset as its name; a list that runs past the buffer
    /// stops where it does.</summary>
    internal static void Parse(ReadOnlySpan<byte> buffer, ProcessLayout layout, List<ProcessCounts> into)
    {
        into.Clear();
        var offset = 0;
        while (offset >= 0 && offset <= buffer.Length - layout.Size)
        {
            var entry = buffer[offset..];
            var pid = layout.Wide ? (uint)BinaryPrimitives.ReadUInt64LittleEndian(entry[layout.Pid..]) : BinaryPrimitives.ReadUInt32LittleEndian(entry[layout.Pid..]);
            into.Add(new ProcessCounts(
                pid, BinaryPrimitives.ReadInt64LittleEndian(entry[layout.Create..]), BinaryPrimitives.ReadUInt32LittleEndian(entry[layout.Session..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[layout.Cycles..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[layout.User..]) + BinaryPrimitives.ReadInt64LittleEndian(entry[layout.Kernel..]), offset));
            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (next == 0) return;
            offset += (int)next;
        }
    }

    /// <summary>The image name of the entry at <paramref name="entry"/>, its pointer read as if the buffer sat at
    /// <paramref name="baseAddress"/>; empty for the idle process, which has none, and for a name outside the buffer.</summary>
    internal static string ImageName(ReadOnlySpan<byte> buffer, ProcessLayout layout, long baseAddress, int entry)
    {
        if (entry < 0 || entry > buffer.Length - layout.Size) return "";
        var fields = buffer[entry..];
        int length = BinaryPrimitives.ReadUInt16LittleEndian(fields[layout.NameLength..]) & ~1;
        long address = layout.Wide ? BinaryPrimitives.ReadInt64LittleEndian(fields[layout.NameBuffer..]) : BinaryPrimitives.ReadUInt32LittleEndian(fields[layout.NameBuffer..]);
        var at = address - baseAddress;
        if (length == 0 || address == 0 || at < 0 || at > buffer.Length - length) return "";
        return new string(MemoryMarshal.Cast<byte, char>(buffer.Slice((int)at, length)));
    }

    private bool QueryProcesses()
    {
        if (_buffer is null) Grow(256 << 10);
        while (true)
        {
            var status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _size, out var needed);
            if (status == InfoLengthMismatch && _size < MostBytes)
            {
                Grow(Math.Min(MostBytes, Math.Max(needed, _size) + (64 << 10)));
                continue;
            }
            _used = status >= 0 ? Math.Min(needed > 0 ? needed : _size, _size) : 0;
            return status >= 0;
        }
    }

    private void Grow(int size)
    {
        NativeMemory.Free(_buffer);
        _buffer = null;
        _buffer = (byte*)NativeMemory.Alloc((nuint)size);
        _size = size;
    }

    /// <summary>Every processor group's times added up; one group, which almost every PC has, is one call.</summary>
    private bool QueryProcessors(out ProcessorTimes times)
    {
        times = default;
        long idle = 0, kernel = 0, user = 0, dpc = 0, interrupt = 0;
        for (ushort group = 0; group < _groups; group++)
        {
            var status = _groups == 1
                ? NtQuerySystemInformation(SystemProcessorPerformanceInformation, _processors, ProcessorEntry * ProcessorsPerGroup, out var returned)
                : NtQuerySystemInformationEx(SystemProcessorPerformanceInformation, &group, sizeof(ushort), _processors, ProcessorEntry * ProcessorsPerGroup, out returned);
            if (status < 0) return false;
            for (var at = 0; at + ProcessorEntry <= returned; at += ProcessorEntry)
            {
                var entry = new ReadOnlySpan<byte>(_processors + at, ProcessorEntry);
                idle += BinaryPrimitives.ReadInt64LittleEndian(entry);
                kernel += BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
                user += BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
                dpc += BinaryPrimitives.ReadInt64LittleEndian(entry[24..]);
                interrupt += BinaryPrimitives.ReadInt64LittleEndian(entry[32..]);
            }
        }
        times = new ProcessorTimes(idle, kernel, user, dpc, interrupt);
        return true;
    }

    private string? ImagePath(nint handle)
    {
        while (true)
        {
            var size = _chars.Length;
            fixed (char* chars = _chars)
            {
                if (QueryFullProcessImageNameW(handle, 0, chars, ref size)) return new string(chars, 0, size);
            }
            if (Marshal.GetLastPInvokeError() != InsufficientBuffer || _chars.Length >= 32768) return null;
            _chars = new char[_chars.Length * 4];
        }
    }

    /// <summary>The SID of the account the process runs as, from its token; null when the token will not be opened, as a
    /// protected process's may not be.</summary>
    private static string? UserSid(nint handle)
    {
        const uint TokenQuery = 0x0008;
        const int TokenUser = 1;
        if (!OpenProcessToken(handle, TokenQuery, out var token)) return null;
        try
        {
            var buffer = stackalloc byte[256];
            if (!GetTokenInformation(token, TokenUser, buffer, 256, out _)) return null;
            var sid = *(nint*)buffer;                               // TOKEN_USER: the SID's address first
            return sid == 0 ? null : new System.Security.Principal.SecurityIdentifier(sid).Value;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string? PackageFamily(nint handle)
    {
        const int Room = 256;
        var chars = stackalloc char[Room];
        var length = (uint)Room;
        try
        {
            return GetPackageFamilyName(handle, ref length, chars) == 0 && length > 1 ? new string(chars, 0, (int)length - 1) : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>On a share, by its name or through a mapped drive.</summary>
    private static bool Remote(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            return path.Length >= 2 && path[1] == ':' && new DriveInfo(path[..1]).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A name fit to show: trimmed, without control characters, at most 100 characters; null when nothing is left.</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length == 0 ? null : clean.Length > 100 ? clean[..100].TrimEnd() : clean;
    }

    [DllImport("ntdll.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQuerySystemInformation(int informationClass, void* information, int length, out int returnLength);

    [DllImport("ntdll.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQuerySystemInformationEx(int informationClass, void* input, int inputLength, void* information, int length, out int returnLength);

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, char* name, ref int size);

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPackageFamilyName(nint process, ref uint length, char* name);

    [DllImport("advapi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int informationClass, void* information, int length, out int returned);
}
