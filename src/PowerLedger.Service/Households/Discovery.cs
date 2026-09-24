using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace PowerLedger.Service.Households;

/// <summary>A PowerLedger PC that answered on the network: its random instance name, where to reach its listener, and its TXT
/// record (households design §3).</summary>
/// <param name="Address">Its IPv4 address when it has one, else its IPv6 one; null when neither came back.</param>
internal sealed record FoundService(string Instance, string Host, IPAddress? Address, int Port, IReadOnlyDictionary<string, string> Txt);

/// <summary>DNS-SD for <c>_powerledger._tcp.local</c> (households design §3).</summary>
internal interface IDiscovery : IDisposable
{
    /// <summary>Announces this PC under <paramref name="instance"/>, replacing what it announced before.</summary>
    void Register(string instance, int port, IReadOnlyDictionary<string, string> txt);

    /// <summary>Stops announcing; nothing when nothing is announced.</summary>
    void Unregister();

    /// <summary>The PCs that answer within <paramref name="timeout"/>, each resolved to where it listens.</summary>
    Task<IReadOnlyList<FoundService>> BrowseAsync(TimeSpan timeout, CancellationToken cancel = default);
}

/// <summary>Which kind of network this PC is on.</summary>
internal interface INetworkCategory
{
    /// <summary>True when this PC is on a Private network and on no Public one: only then is it announced, and only there
    /// does the installer's firewall rule let the other PCs in.</summary>
    bool IsPrivate { get; }

    /// <summary>Raised, on any thread, when the networks may have changed.</summary>
    event Action? Changed;
}

/// <summary>What to announce: the instance name, the listener's port and the TXT record.</summary>
internal sealed record Announcement(string Instance, int Port, IReadOnlyDictionary<string, string> Txt)
{
    public bool SameAs(Announcement other) =>
        Instance == other.Instance && Port == other.Port && Txt.Count == other.Txt.Count
        && Txt.All(pair => other.Txt.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

/// <summary>
/// Announces this PC only while the user lets it be found and the network is Private (households design §3), checking again
/// whenever the networks change. A registration Windows refuses is tried again at the next update.
/// </summary>
internal sealed class Announcer : IDisposable
{
    private readonly IDiscovery _discovery;
    private readonly INetworkCategory _network;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private Announcement? _wanted;
    private Announcement? _current;

    public Announcer(IDiscovery discovery, INetworkCategory network, ILogger log)
    {
        _discovery = discovery;
        _network = network;
        _log = log;
        _network.Changed += NetworkChanged;
    }

    public bool Announced
    {
        get
        {
            lock (_gate) return _current is not null;
        }
    }

    /// <summary>Announces <paramref name="wanted"/>, or withdraws the announcement when it is null or the network isn't Private.</summary>
    /// <returns>True when this PC is announced now.</returns>
    public bool Update(Announcement? wanted)
    {
        lock (_gate)
        {
            _wanted = wanted;
            return Apply();
        }
    }

    public void Dispose()
    {
        _network.Changed -= NetworkChanged;
        lock (_gate)
        {
            _wanted = null;
            Apply();
        }
    }

    private void NetworkChanged()
    {
        lock (_gate) Apply();
    }

    private bool Apply()
    {
        bool isPrivate;
        try
        {
            isPrivate = _wanted is not null && _network.IsPrivate;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            _log.LogWarning(error, "The network's category could not be read, so this PC isn't announced");
            isPrivate = false;
        }
        if (!isPrivate || _wanted is null)
        {
            if (_current is null) return false;
            try
            {
                _discovery.Unregister();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _log.LogWarning(error, "Stopping the announcement failed");
            }
            _current = null;
            return false;
        }
        if (_current is not null && _current.SameAs(_wanted)) return true;
        try
        {
            _discovery.Register(_wanted.Instance, _wanted.Port, _wanted.Txt);
            _current = _wanted;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.LogWarning(error, "This PC could not be announced on the network; trying again later");
            _current = null;
            return false;
        }
    }
}

/// <summary>
/// The network's category from Windows' Network List Manager: Private when some connected network is Private and none is
/// Public. A domain network alone counts as neither, as the firewall rule is for Private networks only. Address changes
/// raise <see cref="Changed"/>; changing a network's category in Settings changes no address, so the worker also asks
/// again with each turn.
/// </summary>
internal sealed class WindowsNetworkCategory : INetworkCategory, IDisposable
{
    private static readonly Guid NetworkListManagerClass = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private const int ConnectedNetworks = 1;
    private const int Public = 0, Private = 1;

    private readonly ILogger _log;

    public WindowsNetworkCategory(ILogger log)
    {
        _log = log;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    public event Action? Changed;

    public bool IsPrivate
    {
        get
        {
            var categories = Categories();
            return categories.Contains(Private) && !categories.Contains(Public);
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
    }

    private List<int> Categories()
    {
        var categories = new List<int>();
        object? manager = null;
        try
        {
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManagerClass, throwOnError: true)!);
            var networks = ((INetworkListManager)manager!).GetNetworks(ConnectedNetworks);
            try
            {
                while (networks.Next(1, out var network, out var fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        categories.Add(network.GetCategory());
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(network);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(networks);
            }
        }
        catch (COMException error)
        {
            _log.LogWarning(error, "The Network List Manager could not be asked about the networks");
        }
        finally
        {
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
        return categories;
    }

    private void OnAddressChanged(object? sender, EventArgs e) => Changed?.Invoke();

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Changed?.Invoke();

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        IEnumNetworks GetNetworks(int flags);
    }

    [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworks
    {
        [DispId(-4)]
        object NewEnum { [return: MarshalAs(UnmanagedType.Interface)] get; }

        [PreserveSig]
        int Next(uint count, out INetwork network, out uint fetched);
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetName();

        void SetName([MarshalAs(UnmanagedType.BStr)] string name);

        [return: MarshalAs(UnmanagedType.BStr)]
        string GetDescription();

        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);

        Guid GetNetworkId();

        int GetDomainType();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworkConnections();

        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);

        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }

        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }

        int GetConnectivity();

        int GetCategory();
    }
}

/// <summary>
/// DNS-SD through Windows' own mDNS (households design §3): <c>DnsServiceRegister</c> to announce, <c>DnsServiceBrowse</c>
/// and <c>DnsServiceResolve</c> to find, all asynchronous with callbacks on a system thread. Everything handed to Windows
/// stays alive until its callback says Windows is done with it; what Windows never finishes with is left, never freed early.
/// </summary>
internal sealed class WindowsDiscovery(ILogger log) : IDiscovery
{
    /// <summary>The service type every PowerLedger PC announces itself under.</summary>
    public const string ServiceType = "_powerledger._tcp.local";

    /// <summary>What finding PCs says on a Windows without the DNS-SD functions, added in Windows 10 version 1903.</summary>
    public const string TooOld = "Finding PCs on the network needs Windows 10 version 1903 or later.";

    private const int RequestPending = 9506;           // DNS_REQUEST_PENDING
    private const uint Cancelled = 1223;                // ERROR_CANCELLED
    private const uint RequestVersion1 = 1;             // DNS_QUERY_REQUEST_VERSION1
    private const ushort PtrType = 12;                  // DNS_TYPE_PTR
    private const int FreeRecordList = 1;               // DnsFreeRecordList
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(2);

    // Kept for the process's life, so Windows can call them whenever it likes.
    private static readonly RegisterComplete OnRegistered = Registered;
    private static readonly BrowseCallback OnBrowsed = Browsed;
    private static readonly ResolveComplete OnResolved = Resolved;
    private static readonly IntPtr RegisteredPointer = Marshal.GetFunctionPointerForDelegate(OnRegistered);
    private static readonly IntPtr BrowsedPointer = Marshal.GetFunctionPointerForDelegate(OnBrowsed);
    private static readonly IntPtr ResolvedPointer = Marshal.GetFunctionPointerForDelegate(OnResolved);

    private readonly Lock _gate = new();
    private Registration? _registration;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BrowseCallback(uint status, IntPtr context, IntPtr records);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ResolveComplete(uint status, IntPtr context, IntPtr instance);

    public void Register(string instance, int port, IReadOnlyDictionary<string, string> txt) => Supported(() => RegisterCore(instance, port, txt));

    /// <summary>Runs a call into dnsapi.dll, turning a function that Windows lacks, as before Windows 10 version 1903, into a
    /// <see cref="PlatformNotSupportedException"/> that says so in words the App can show.</summary>
    internal static void Supported(Action call)
    {
        try
        {
            call();
        }
        catch (EntryPointNotFoundException error)
        {
            throw new PlatformNotSupportedException(TooOld, error);
        }
    }

    private void RegisterCore(string instance, int port, IReadOnlyDictionary<string, string> txt)
    {
        lock (_gate)
        {
            UnregisterLocked();
            var registration = new Registration();
            var pairs = txt.ToArray();
            var keys = pairs.Select(pair => Marshal.StringToHGlobalUni(pair.Key)).ToArray();
            var values = pairs.Select(pair => Marshal.StringToHGlobalUni(pair.Value)).ToArray();
            try
            {
                registration.Instance = DnsServiceConstructInstance(
                    $"{instance}.{ServiceType}", Dns.GetHostName() + ".local", IntPtr.Zero, IntPtr.Zero, checked((ushort)port), 0, 0,
                    (uint)pairs.Length, keys, values);
            }
            finally
            {
                foreach (var pointer in keys.Concat(values)) Marshal.FreeHGlobal(pointer);
            }
            if (registration.Instance == IntPtr.Zero) throw new InvalidOperationException("DnsServiceConstructInstance failed.");

            registration.Context = GCHandle.Alloc(registration);
            registration.Request = Marshal.AllocHGlobal(Marshal.SizeOf<RegisterRequest>());
            Marshal.StructureToPtr(new RegisterRequest
            {
                Version = RequestVersion1,
                ServiceInstance = registration.Instance,
                CompletionCallback = RegisteredPointer,
                Context = GCHandle.ToIntPtr(registration.Context),
            }, registration.Request, fDeleteOld: false);
            var status = DnsServiceRegister(registration.Request, IntPtr.Zero);
            if (status != RequestPending)
            {
                registration.Free();
                throw new Win32Exception((int)status, $"DnsServiceRegister failed with {status}.");
            }
            _registration = registration;
            log.LogInformation("Announcing this PC on the network as {Instance} on port {Port}", instance, port);
        }
    }

    public void Unregister()
    {
        lock (_gate) UnregisterLocked();
    }

    public void Dispose() => Unregister();

    public async Task<IReadOnlyList<FoundService>> BrowseAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        try
        {
            return await BrowseCoreAsync(timeout, cancel).ConfigureAwait(false);
        }
        catch (EntryPointNotFoundException error)
        {
            throw new PlatformNotSupportedException(TooOld, error);
        }
    }

    private async Task<IReadOnlyList<FoundService>> BrowseCoreAsync(TimeSpan timeout, CancellationToken cancel)
    {
        var browse = new Browse();
        browse.Context = GCHandle.Alloc(browse);
        browse.QueryName = Marshal.StringToHGlobalUni(ServiceType);
        browse.CancelHandle = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(browse.CancelHandle, IntPtr.Zero);
        var request = new BrowseRequest
        {
            Version = RequestVersion1,
            QueryName = browse.QueryName,
            Callback = BrowsedPointer,
            Context = GCHandle.ToIntPtr(browse.Context),
        };
        var status = DnsServiceBrowse(ref request, browse.CancelHandle);
        if (status != RequestPending)
        {
            browse.Free();
            log.LogWarning("DnsServiceBrowse failed with {Status}", status);
            return [];
        }
        try
        {
            await Task.Delay(timeout, cancel).ConfigureAwait(false);
        }
        finally
        {
            DnsServiceBrowseCancel(browse.CancelHandle);
            if (await Finished(browse.Done.Task).ConfigureAwait(false)) browse.Free();
        }

        var resolving = browse.Names.Keys.Take(HouseholdWorker.MaxFoundKept).Select(name => ResolveAsync(name, cancel)).ToArray();   // plan 0.9
        var found = await Task.WhenAll(resolving).ConfigureAwait(false);
        return [.. found.OfType<FoundService>()];
    }

    private async Task<FoundService?> ResolveAsync(string fullName, CancellationToken cancel)
    {
        var resolve = new Resolve();
        resolve.Context = GCHandle.Alloc(resolve);
        resolve.QueryName = Marshal.StringToHGlobalUni(fullName);
        resolve.CancelHandle = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(resolve.CancelHandle, IntPtr.Zero);
        var request = new ResolveRequest
        {
            Version = RequestVersion1,
            QueryName = resolve.QueryName,
            Callback = ResolvedPointer,
            Context = GCHandle.ToIntPtr(resolve.Context),
        };
        var status = DnsServiceResolve(ref request, resolve.CancelHandle);
        if (status != RequestPending)
        {
            resolve.Free();
            log.LogDebug("DnsServiceResolve for {Name} failed with {Status}", fullName, status);
            return null;
        }
        try
        {
            await resolve.Done.Task.WaitAsync(ResolveTimeout, cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            DnsServiceResolveCancel(resolve.CancelHandle);
            if (!await Finished(resolve.Done.Task).ConfigureAwait(false)) return null;     // left for Windows
        }
        resolve.Free();
        var found = resolve.Found;
        if (found is null) return null;
        if (found.Address is null)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(found.Host, cancel).ConfigureAwait(false);
                found = found with { Address = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault() };
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }
        return found;
    }

    /// <summary>Waits a little for a cancelled request's last callback.</summary>
    /// <returns>False when it never came: what the request holds is then left, as Windows may still use it.</returns>
    private static async Task<bool> Finished(Task done)
    {
        try
        {
            await done.WaitAsync(CancelWait).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void UnregisterLocked()
    {
        if (_registration is not { } registration) return;
        _registration = null;
        var status = DnsServiceDeRegister(registration.Request, IntPtr.Zero);
        if (status != RequestPending)
        {
            log.LogWarning("DnsServiceDeRegister failed with {Status}", status);
            return;                                                    // left, in case Windows still holds it
        }
        registration.Deregistered();
        if (registration.Done.Task.Wait(CancelWait)) registration.Free();
        log.LogInformation("No longer announcing this PC on the network");
    }

    private static void Registered(uint status, IntPtr context, IntPtr instance)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not Registration registration) return;
            if (instance != IntPtr.Zero && instance != registration.Instance) DnsServiceFreeInstance(instance);
            registration.Called();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void Browsed(uint status, IntPtr context, IntPtr records)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not Browse browse) return;
            for (var record = records; record != IntPtr.Zero;)
            {
                var header = Marshal.PtrToStructure<RecordHeader>(record);
                if (header.Type == PtrType && Marshal.PtrToStringUni(header.PtrNameHost) is { } name
                    && name.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase))
                {
                    if (header.Ttl == 0) browse.Names.TryRemove(name, out _);
                    else browse.Names[name] = true;
                }
                record = header.Next;
            }
            if (status == Cancelled) browse.Done.TrySetResult();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            if (records != IntPtr.Zero) DnsRecordListFree(records, FreeRecordList);
        }
    }

    private static void Resolved(uint status, IntPtr context, IntPtr instance)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not Resolve resolve) return;
            if (status == 0 && instance != IntPtr.Zero) resolve.Found = Read(instance);
            resolve.Done.TrySetResult();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
        }
    }

    private static FoundService? Read(IntPtr pointer)
    {
        var instance = Marshal.PtrToStructure<ServiceInstance>(pointer);
        var fullName = Marshal.PtrToStringUni(instance.InstanceName) ?? "";
        var suffix = "." + ServiceType;
        if (!fullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        IPAddress? address = null;
        if (instance.Ip4Address != IntPtr.Zero)
        {
            var bytes = new byte[4];
            Marshal.Copy(instance.Ip4Address, bytes, 0, 4);
            address = new IPAddress(bytes);
        }
        else if (instance.Ip6Address != IntPtr.Zero)
        {
            var bytes = new byte[16];
            Marshal.Copy(instance.Ip6Address, bytes, 0, 16);
            address = new IPAddress(bytes, instance.InterfaceIndex);
        }
        var txt = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < instance.PropertyCount; i++)
        {
            var key = Marshal.PtrToStringUni(Marshal.ReadIntPtr(instance.Keys, i * IntPtr.Size));
            var value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(instance.Values, i * IntPtr.Size));
            if (key is not null) txt[key] = value ?? "";
        }
        return new FoundService(
            fullName[..^suffix.Length], Marshal.PtrToStringUni(instance.HostName) ?? "", address, instance.Port, txt);
    }

    /// <summary>What an announcement holds on to until Windows has finished with it: until it has called back twice, once for
    /// the registration and once for the deregistration.</summary>
    private sealed class Registration
    {
        public IntPtr Instance;
        public IntPtr Request;
        public GCHandle Context;
        private int _calls;
        private int _deregistered;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Called()
        {
            if (Interlocked.Increment(ref _calls) >= 2 && Volatile.Read(ref _deregistered) == 1) Done.TrySetResult();
        }

        public void Deregistered()
        {
            Volatile.Write(ref _deregistered, 1);
            if (Volatile.Read(ref _calls) >= 2) Done.TrySetResult();
        }

        public void Free()
        {
            if (Instance != IntPtr.Zero) DnsServiceFreeInstance(Instance);
            if (Request != IntPtr.Zero) Marshal.FreeHGlobal(Request);
            if (Context.IsAllocated) Context.Free();
            Instance = Request = IntPtr.Zero;
        }
    }

    private sealed class Browse
    {
        public IntPtr QueryName;
        public IntPtr CancelHandle;
        public GCHandle Context;

        public ConcurrentDictionary<string, bool> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Free()
        {
            Marshal.FreeHGlobal(QueryName);
            Marshal.FreeHGlobal(CancelHandle);
            if (Context.IsAllocated) Context.Free();
        }
    }

    private sealed class Resolve
    {
        public IntPtr QueryName;
        public IntPtr CancelHandle;
        public GCHandle Context;
        public FoundService? Found;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Free()
        {
            Marshal.FreeHGlobal(QueryName);
            Marshal.FreeHGlobal(CancelHandle);
            if (Context.IsAllocated) Context.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceInstance
    {
        public IntPtr InstanceName;
        public IntPtr HostName;
        public IntPtr Ip4Address;
        public IntPtr Ip6Address;
        public ushort Port;
        public ushort Priority;
        public ushort Weight;
        public uint PropertyCount;
        public IntPtr Keys;
        public IntPtr Values;
        public uint InterfaceIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr CompletionCallback;
        public IntPtr Context;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BrowseRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr Callback;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResolveRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr Callback;
        public IntPtr Context;
    }

    /// <summary>DNS_RECORDW up to the first field of its data, which for a PTR record is the name it points at.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RecordHeader
    {
        public IntPtr Next;
        public IntPtr Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
        public uint Ttl;
        public uint Reserved;
        public IntPtr PtrNameHost;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr DnsServiceConstructInstance(
        string serviceName, string hostName, IntPtr ip4, IntPtr ip6, ushort port, ushort priority, ushort weight, uint propertiesCount,
        IntPtr[] keys, IntPtr[] values);

    [DllImport("dnsapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void DnsServiceFreeInstance(IntPtr instance);

    [DllImport("dnsapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DnsServiceRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DnsServiceDeRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DnsServiceBrowse(ref BrowseRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DnsServiceBrowseCancel(IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DnsServiceResolve(ref ResolveRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DnsServiceResolveCancel(IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);
}
