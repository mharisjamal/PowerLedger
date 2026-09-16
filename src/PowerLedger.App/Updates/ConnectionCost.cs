using System.Runtime.InteropServices;

namespace PowerLedger.App;

/// <summary>Whether the connection this PC uses charges for what it carries.</summary>
internal interface IConnectionCost
{
    /// <summary>True when Windows says the connection is metered. A cost Windows won't give counts as unmetered, which is
    /// how PowerLedger behaved before it asked at all.</summary>
    bool Metered { get; }
}

/// <summary>
/// Windows' network list (spec §13): the cost of the connection the machine uses now. Everything but "unrestricted" and
/// "unknown" counts as metered — a fixed allowance, paying by the byte, roaming, or near or over the limit — because
/// Windows itself treats those as data the user pays for. Congestion alone is not about money.
/// </summary>
internal sealed class ConnectionCost : IConnectionCost
{
    private const uint Fixed = 0x2;
    private const uint Variable = 0x4;
    private const uint OverDataLimit = 0x10000;
    private const uint Roaming = 0x40000;
    private const uint ApproachingDataLimit = 0x80000;

    public bool Metered
    {
        get
        {
            try
            {
                var manager = (INetworkCostManager)new NetworkListManager();
                manager.GetCost(out var cost, IntPtr.Zero);
                return IsMetered(cost);
            }
            catch (Exception error) when (error is COMException or InvalidCastException or NotSupportedException)
            {
                return false;   // no answer: treat it as a connection nobody pays by the byte for
            }
        }
    }

    /// <summary>NLM_CONNECTION_COST's flags, as money rather than speed.</summary>
    internal static bool IsMetered(uint cost) => (cost & (Fixed | Variable | OverDataLimit | Roaming | ApproachingDataLimit)) != 0;

    /// <summary>netlistmgr.h's NetworkListManager, which <c>new</c> creates through COM. It is not sealed because the cast
    /// to the interface it answers with is the QueryInterface, which the compiler allows only on a class that could have
    /// implemented it.</summary>
    [ComImport]
    [Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManager;

    /// <summary>netlistmgr.h's INetworkCostManager. Every method has to be declared, in order, for the vtable to line up,
    /// even though only the first is called.</summary>
    [ComImport]
    [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, IntPtr destination);

        void GetDataPlanStatus(IntPtr status, IntPtr destination);

        void SetDestinationAddresses(uint length, IntPtr addresses, [MarshalAs(UnmanagedType.VariantBool)] bool append);
    }
}
