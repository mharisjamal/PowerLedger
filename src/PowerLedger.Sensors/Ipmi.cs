using System.Management;

namespace PowerLedger.Sensors;

/// <param name="CompletionCode">The IPMI completion code: 0 is success, 0xC1 a command the BMC doesn't know.</param>
/// <param name="Data">The response bytes Windows handed back, which may begin with the completion code again.</param>
internal readonly record struct BmcAnswer(byte CompletionCode, byte[] Data);

/// <summary>A baseboard management controller, asked one IPMI request at a time, so a test can stand in for it.</summary>
internal interface IBmc
{
    /// <summary>Sends one request to the BMC and returns its answer. Throws what WMI throws when it can't ask.</summary>
    BmcAnswer Send(byte networkFunction, byte command, byte[] data);
}

/// <summary>
/// The Data Center Manageability Interface's power reading, from a BMC's answer to Get Power Reading (network function
/// 0x2C, command 0x02, group extension 0xDC, mode 0x01 for the system's power statistics). The answer carries the group
/// extension 0xDC, then the current, least, most and average watts as 16-bit little-endian numbers, a timestamp, the
/// reporting period and a state byte whose bit 6 says the measurement is running.
/// </summary>
internal static class Dcmi
{
    public const byte GroupExtension = 0xDC;
    public const byte NetworkFunction = 0x2C;
    public const byte GetPowerReading = 0x02;

    /// <summary>System power statistics.</summary>
    private const byte SystemPower = 0x01;

    /// <summary>The group extension and 17 bytes after it.</summary>
    private const int ReadingLength = 18;

    /// <summary>The request body: group extension, mode, mode attributes and a reserved byte.</summary>
    public static byte[] Request() => [GroupExtension, SystemPower, 0x00, 0x00];

    /// <summary>The system's current input watts from an answer, or null and why there are none. Windows' IPMI class may
    /// put the completion code in front of the data, so the reading starts at the group extension wherever it is.</summary>
    public static double? Watts(BmcAnswer answer, out string? why)
    {
        why = null;
        if (answer.CompletionCode == 0xC1)
        {
            why = "the BMC doesn't report its power reading (no DCMI)";
            return null;
        }
        if (answer.CompletionCode != 0)
        {
            why = $"the BMC answered with code 0x{answer.CompletionCode:X2}";
            return null;
        }
        var data = answer.Data ?? [];
        var at = data.Length > 0 && data[0] == GroupExtension ? 0
            : data.Length > 1 && data[0] == 0 && data[1] == GroupExtension ? 1
            : -1;
        if (at < 0 || data.Length < at + ReadingLength)
        {
            why = "the BMC's power reading couldn't be read";
            return null;
        }
        if ((data[at + 17] & 0x40) == 0)
        {
            why = "the BMC's power measurement is switched off";
            return null;
        }
        var watts = data[at + 1] | (data[at + 2] << 8);
        return watts > 0 ? watts : null;
    }
}

/// <summary>
/// Windows' own IPMI driver (ipmidrv.sys), reached through its WMI class Microsoft_IPMI in root\wmi, whose method
/// RequestResponse sends one request to the BMC. The class has an instance only where the driver found a BMC, and it is
/// open to administrators, which the service's LocalSystem account is. No driver of ours.
/// </summary>
internal sealed class WmiBmc : IBmc
{
    private const string Scope = @"root\wmi";

    /// <summary>The BMC's own address on the IPMB, where requests to it go.</summary>
    private const byte BmcAddress = 0x20;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly string _path;

    private WmiBmc(string path) => _path = path;

    /// <summary>The machine's BMC, or null where Windows' IPMI driver found none. Throws what WMI throws when it can't say.</summary>
    public static WmiBmc? Find()
        => Wmi.ReadInstances(Scope, "SELECT * FROM Microsoft_IPMI", rows => rows.Count > 0 && rows[0] is ManagementObject found
            ? new WmiBmc(found.Path.Path)
            : null);

    public BmcAnswer Send(byte networkFunction, byte command, byte[] data)
    {
        using var ipmi = new ManagementObject(new ManagementScope(Scope), new ManagementPath(_path), null);
        using var input = ipmi.GetMethodParameters("RequestResponse");
        input["NetworkFunction"] = networkFunction;
        input["Lun"] = (byte)0;
        input["ResponderAddress"] = BmcAddress;
        input["Command"] = command;
        input["RequestDataSize"] = (uint)data.Length;
        input["RequestData"] = data;
        using var output = ipmi.InvokeMethod("RequestResponse", input, new InvokeMethodOptions(null, Timeout));
        var code = Convert.ToByte(output["CompletionCode"], System.Globalization.CultureInfo.InvariantCulture);
        return new BmcAnswer(code, output["ResponseData"] as byte[] ?? []);
    }
}
