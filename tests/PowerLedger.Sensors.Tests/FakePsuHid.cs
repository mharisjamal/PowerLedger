using PowerLedger.Sensors;

namespace PowerLedger.Sensors.Tests;

/// <summary>
/// A HID device under the test's control. Every report written to it is kept, so a test can say exactly what went on
/// the wire, and the device answers as the real protocol does.
/// </summary>
internal sealed class FakeHidDevice
{
    private readonly Func<byte[], byte[]?> _answer;
    private readonly Queue<byte[]> _waiting = new();

    public FakeHidDevice(ushort vendorId, ushort productId, Func<byte[], byte[]?> answer, string? path = null)
    {
        _answer = answer;
        Listing = new HidDevice(
            path ?? $@"\\?\hid#vid_{vendorId:x4}&pid_{productId:x4}", vendorId, productId,
            InputReportLength: 65, OutputReportLength: 65);
    }

    /// <summary>The device as Windows would list it.</summary>
    public HidDevice Listing { get; }

    /// <summary>Whether Windows lists it, so a test can have it turn up late or not at all.</summary>
    public bool Present { get; set; } = true;

    /// <summary>Every report written, report ID first.</summary>
    public List<byte[]> Written { get; } = [];

    public int Opens { get; private set; }

    public int Closes { get; private set; }

    /// <summary>A device that answers nothing, so every read runs out of time.</summary>
    public bool Deaf { get; set; }

    /// <summary>A report the device sends before its answer, as one still answering another program would.</summary>
    public Func<byte[], byte[]>? Noise { get; set; }

    /// <summary>Windows refusing to take the report.</summary>
    public bool RefusesWrites { get; set; }

    public Exception? ReadThrows { get; set; }

    public IHidLink Connect()
    {
        Opens++;
        _waiting.Clear();
        return new Link(this);
    }

    private sealed class Link(FakeHidDevice device) : IHidLink
    {
        public void Flush() => device._waiting.Clear();

        public bool Write(byte[] report, TimeSpan timeout)
        {
            device.Written.Add((byte[])report.Clone());
            if (device.RefusesWrites) return false;
            if (device.Deaf) return true;
            if (device.Noise is { } noise) device._waiting.Enqueue(noise(report));
            if (device._answer(report) is { } reply) device._waiting.Enqueue(reply);
            return true;
        }

        public byte[]? Read(TimeSpan timeout)
        {
            if (device.ReadThrows is { } error) throw error;
            return device._waiting.Count > 0 ? device._waiting.Dequeue() : null;
        }

        public void Dispose() => device.Closes++;
    }
}

/// <summary>The HID layer under the test's control: which collections Windows lists, and whether it will open them.</summary>
internal sealed class FakeHidPort(params FakeHidDevice[] devices) : IHidPort
{
    public int Finds { get; private set; }

    public int Opens { get; private set; }

    public Exception? FindThrows { get; set; }

    public Exception? OpenThrows { get; set; }

    /// <summary>Windows refusing the handle, as it does when another program has the device open for itself.</summary>
    public bool RefusesToOpen { get; set; }

    public IReadOnlyList<HidDevice> Find(Func<ushort, ushort, bool> wanted)
    {
        Finds++;
        if (FindThrows is { } error) throw error;
        return devices
            .Where(fake => fake.Present && wanted(fake.Listing.VendorId, fake.Listing.ProductId))
            .Select(fake => fake.Listing)
            .ToList();
    }

    public IHidLink? Open(HidDevice device)
    {
        Opens++;
        if (OpenThrows is { } error) throw error;
        if (RefusesToOpen) return null;
        return devices.First(fake => fake.Listing.Path == device.Path).Connect();
    }
}

/// <summary>
/// Stand-ins for the three power supplies, answering as the drivers' documentation and the traffic captured from real
/// devices say they do. The numbers they answer with are encoded here the way a supply encodes them, so a test that
/// gets the right watts has been through the same decode the real device's bytes go through.
/// </summary>
internal static class FakePsus
{
    public const ushort CorsairVendor = 0x1B1C;
    public const ushort NzxtVendor = 0x7793;
    public const ushort ThermaltakeVendor = 0x264A;

    /// <summary>ULINEAR16 exponent a Seasonic-built bridge reports in VOUT_MODE.</summary>
    public const byte VoutMode = 0x14;

    /// <summary>PMBus LINEAR11: an 11-bit mantissa under a 5-bit exponent, low byte first.</summary>
    public static byte[] Linear11(double value, int exponent)
    {
        var mantissa = (int)Math.Round(value / Math.Pow(2, exponent));
        var raw = (ushort)(((exponent & 0x1F) << 11) | (mantissa & 0x7FF));
        return [(byte)(raw & 0xFF), (byte)(raw >> 8)];
    }

    /// <summary>PMBus ULINEAR16: an unsigned mantissa whose exponent comes from VOUT_MODE.</summary>
    public static byte[] ULinear16(double value, int exponent)
    {
        var raw = (ushort)Math.Round(value / Math.Pow(2, exponent));
        return [(byte)(raw & 0xFF), (byte)(raw >> 8)];
    }

    /// <summary>A Corsair HXi or RMi: 64-byte reports of [length][command], echoed back at the start of the reply. Before
    /// the 0xFE handshake it answers with the command byte replaced by 0xFE, which is what a real one does.</summary>
    public static FakeHidDevice Corsair(double watts, string product = "RM1000i", ushort productId = 0x1C0D)
    {
        var greeted = false;
        return new FakeHidDevice(CorsairVendor, productId, report =>
        {
            var reply = new byte[65];
            var length = report[1];
            var command = report[2];
            if (length == 0xFE && command == 0x03)
            {
                greeted = true;
                reply[1] = 0xFE;
                reply[2] = 0x03;
                Ascii(reply, 3, product);
                return reply;
            }

            reply[1] = length;
            if (!greeted)
            {
                reply[2] = 0xFE;
                return reply;
            }

            reply[2] = command;
            switch (command)
            {
                case 0xEE:
                    Copy(reply, 3, Linear11(watts, 1));
                    break;
                case 0x9A:
                    Ascii(reply, 3, product);
                    break;
                default:
                    reply[2] = 0;                       // a command this supply does not know
                    break;
            }

            return reply;
        });
    }

    /// <summary>An NZXT E series: a PMBus bridge that answers a paged read with 0xAA, the length it was asked with and
    /// the number of data bytes. A rail whose watts are null answers a reply that is not one, as the bridge does when it
    /// is busy, so the read has to fall back to volts times amps.</summary>
    public static FakeHidDevice Nzxt(double?[] watts, double[] volts, double[] amps, ushort productId = 0x5911, int busyReplies = 0)
        => new(NzxtVendor, productId, report =>
        {
            var reply = new byte[65];
            if (report[1] != 0xAD || report[6] != 0x06) return reply;
            var length = report[3];
            var rail = report[8];
            var command = report[9];
            byte[]? data = command switch
            {
                0x20 => [VoutMode],
                0x8B => ULinear16(volts[rail], -12),
                0x8C => Linear11(amps[rail], -4),
                0x96 => watts[rail] is { } value ? Linear11(value, -2) : null,
                _ => null,
            };

            reply[1] = 0xAA;
            reply[2] = length;
            if (data is null || busyReplies-- > 0)
            {
                reply[3] = 0xFF;                        // the bridge signalling that its answer is not one
                return reply;
            }

            reply[3] = (byte)data.Length;
            Copy(reply, 4, data);
            return reply;
        });

    /// <summary>A Thermaltake DPS G: a read of [0x31][register] whose value comes back in the fourth and fifth bytes. The
    /// volts are encoded with a mantissa above 1023, which only a decode that reads the mantissa unsigned gets right.</summary>
    /// <param name="answersRegister">The register the supply echoes, whatever it was asked: a supply one reply behind,
    /// or one answering another program, sends a report for a register nobody here asked for.</param>
    public static FakeHidDevice Dpsg(double[] volts, double[] amps, byte? answersRegister = null)
        => new(ThermaltakeVendor, 0x2329, report =>
        {
            var reply = new byte[65];
            if (report[1] == 0xFE && report[2] == 0x31)
            {
                reply[1] = 0xFE;
                reply[2] = 0x31;
                Ascii(reply, 3, "DPS G");
                return reply;
            }

            if (report[1] != 0x31) return reply;
            var register = answersRegister ?? report[2];
            byte[]? value = register switch
            {
                >= 0x34 and <= 0x36 => Linear11(volts[register - 0x34], -7),
                >= 0x37 and <= 0x39 => Linear11(amps[register - 0x37], -4),
                _ => null,
            };
            if (value is null) return reply;

            reply[1] = 0x31;
            reply[2] = register;
            Copy(reply, 3, value);
            return reply;
        });

    private static void Ascii(byte[] report, int at, string text)
    {
        for (var index = 0; index < text.Length && at + index < report.Length; index++) report[at + index] = (byte)text[index];
    }

    private static void Copy(byte[] report, int at, byte[] bytes) => bytes.CopyTo(report, at);
}
