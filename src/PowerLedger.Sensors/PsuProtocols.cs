using System.Text;

namespace PowerLedger.Sensors;

/// <summary>Which protocol a power supply speaks. Nothing is shared between them but the HID transport underneath.</summary>
internal enum PsuFamily
{
    /// <summary>Corsair HXi and RMi, read as the Linux corsair-psu driver reads them.</summary>
    Corsair,

    /// <summary>NZXT E500, E650 and E850, which are Seasonic units with a PMBus-to-HID bridge.</summary>
    Nzxt,

    /// <summary>Thermaltake Toughpower DPS G.</summary>
    Thermaltake,
}

/// <param name="Name">What the supply is called before, or unless, it says its own name.</param>
internal sealed record PsuModel(ushort VendorId, ushort ProductId, PsuFamily Family, string Name);

/// <summary>
/// Every power supply PowerLedger reads over USB. The Corsair ids and names are the Linux corsair-psu driver's device
/// table, the NZXT ids are liquidctl's for the Seasonic-built E series, and the Thermaltake id is TTController's for the
/// DPS G. Corsair's AXi units need a vendor driver of their own and no protocol is known for the ASUS ROG Thor, so
/// neither is here (spec §5).
/// </summary>
internal static class PsuModels
{
    private const ushort Corsair = 0x1B1C;
    private const ushort Nzxt = 0x7793;
    private const ushort Thermaltake = 0x264A;

    public static IReadOnlyList<PsuModel> All { get; } =
    [
        new(Corsair, 0x1C03, PsuFamily.Corsair, "Corsair HX550i"),
        new(Corsair, 0x1C04, PsuFamily.Corsair, "Corsair HX650i"),
        new(Corsair, 0x1C05, PsuFamily.Corsair, "Corsair HX750i"),
        new(Corsair, 0x1C06, PsuFamily.Corsair, "Corsair HX850i"),
        new(Corsair, 0x1C07, PsuFamily.Corsair, "Corsair HX1000i"),
        new(Corsair, 0x1C08, PsuFamily.Corsair, "Corsair HX1200i"),
        new(Corsair, 0x1C09, PsuFamily.Corsair, "Corsair RM550i"),
        new(Corsair, 0x1C0A, PsuFamily.Corsair, "Corsair RM650i"),
        new(Corsair, 0x1C0B, PsuFamily.Corsair, "Corsair RM750i"),
        new(Corsair, 0x1C0C, PsuFamily.Corsair, "Corsair RM850i"),
        new(Corsair, 0x1C0D, PsuFamily.Corsair, "Corsair RM1000i"),
        new(Corsair, 0x1C1E, PsuFamily.Corsair, "Corsair HX1000i"),
        new(Corsair, 0x1C1F, PsuFamily.Corsair, "Corsair HX1500i"),
        new(Corsair, 0x1C23, PsuFamily.Corsair, "Corsair HX1200i"),
        new(Corsair, 0x1C27, PsuFamily.Corsair, "Corsair HX1200i"),
        new(Nzxt, 0x5911, PsuFamily.Nzxt, "NZXT E500"),
        new(Nzxt, 0x5912, PsuFamily.Nzxt, "NZXT E650"),
        new(Nzxt, 0x2500, PsuFamily.Nzxt, "NZXT E850"),
        new(Thermaltake, 0x2329, PsuFamily.Thermaltake, "Thermaltake Toughpower DPS G"),
    ];

    /// <summary>What to say when the machine has none of them.</summary>
    public const string NoneFound = "no Corsair HXi or RMi, NZXT E series or Thermaltake DPS G power supply on USB";

    public static PsuModel? Find(ushort vendorId, ushort productId)
    {
        foreach (var model in All)
        {
            if (model.VendorId == vendorId && model.ProductId == productId) return model;
        }

        return null;
    }

    public static bool Known(ushort vendorId, ushort productId) => Find(vendorId, productId) is not null;
}

/// <summary>The number formats the three protocols answer in, and what counts as a believable answer.</summary>
internal static class PsuMath
{
    /// <summary>No supply PowerLedger reads is rated above 1500 W, so anything past this is a misread, not a reading.</summary>
    private const double MostWatts = 2000;

    /// <summary>PMBus LINEAR11: an 11-bit two's-complement mantissa under a 5-bit two's-complement exponent, low byte
    /// first. Corsair's and NZXT's supplies both answer in it.</summary>
    public static double Linear11(byte low, byte high)
    {
        var raw = (ushort)(low | (high << 8));
        var mantissa = raw & 0x7FF;
        return (mantissa > 1023 ? mantissa - 2048 : mantissa) * Math.Pow(2, Exponent(raw >> 11));
    }

    /// <summary>The same with the mantissa read unsigned, which is how TTController reads a DPS G. Volts and amps are
    /// never negative, and a mantissa above 1023 there means a large reading rather than a negative one.</summary>
    public static double Linear11Unsigned(byte low, byte high)
    {
        var raw = (ushort)(low | (high << 8));
        return (raw & 0x7FF) * Math.Pow(2, Exponent(raw >> 11));
    }

    /// <summary>PMBus ULINEAR16: an unsigned 16-bit mantissa whose exponent is the low five bits of VOUT_MODE. Null when
    /// VOUT_MODE says the supply reports its output voltage some other way, which none of these is known to do.</summary>
    public static double? ULinear16(byte low, byte high, byte voutMode)
        => (voutMode >> 5) != 0 ? null : (ushort)(low | (high << 8)) * Math.Pow(2, Exponent(voutMode & 0x1F));

    /// <summary>Watts a power supply could really be putting out; anything else is treated as no reading at all.</summary>
    public static double? PlausibleWatts(double? watts)
        => watts is { } value && double.IsFinite(value) && value is >= 0 and <= MostWatts ? value : null;

    /// <summary>A five-bit two's-complement exponent.</summary>
    private static int Exponent(int bits) => bits > 15 ? bits - 32 : bits;
}

/// <summary>The shape of a report on the wire: the report ID Windows puts first, the command, then zero padding.</summary>
internal static class PsuFrames
{
    /// <summary>True when the report is exactly this command, with report ID zero and nothing after it but zeros.</summary>
    public static bool Is(byte[] report, byte[] command)
    {
        if (report.Length < command.Length + 1 || report[0] != 0) return false;
        for (var index = 0; index < command.Length; index++)
        {
            if (report[index + 1] != command[index]) return false;
        }

        return RestIsZero(report, command.Length + 1);
    }

    /// <summary>True when every byte from <paramref name="from"/> onwards is zero.</summary>
    public static bool RestIsZero(byte[] report, int from)
    {
        for (var index = from; index < report.Length; index++)
        {
            if (report[index] != 0) return false;
        }

        return true;
    }
}

/// <summary>
/// One open power supply's wire. It pads each command into a report, turns down any report its family's rules do not
/// allow before it can reach the device, and waits for the reply. The first command that goes unanswered, or a cycle
/// that runs past its deadline, breaks the wire: nothing more is said to the device until the next read cycle opens it
/// afresh, so a supply that has stopped answering costs one timeout rather than a tick.
/// </summary>
internal sealed class PsuWire(IHidLink link, int reportLength, Func<byte[], bool> allowed, Func<TimeSpan> clock, Action<TimeSpan> pause) : IDisposable
{
    /// <summary>How long one command waits for its reply. The Linux driver waits 250 ms; this is twice that.</summary>
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(500);

    private TimeSpan _deadline = TimeSpan.MaxValue;
    private bool _broken;

    /// <summary>Starts a read cycle, which may run until <paramref name="deadline"/>.</summary>
    public void Begin(TimeSpan deadline)
    {
        _deadline = deadline;
        _broken = false;
    }

    /// <summary>Leaves the device the time its bridge needs between commands.</summary>
    public void Wait(TimeSpan delay) => pause(delay);

    /// <summary>Sends a command and returns the first report <paramref name="isReply"/> accepts, passing over anything
    /// else the device has to say; null when no reply comes before the timeout. There is no overload that takes the
    /// first report to arrive: a supply one reply behind, or one still answering its maker's program, would otherwise
    /// pair one register's number with another's and give a wattage that is plausible and wrong.</summary>
    public byte[]? Ask(byte[] command, Func<byte[], bool> isReply)
    {
        if (_broken) return null;
        if (!Send(command))
        {
            _broken = true;
            return null;
        }

        var until = clock() + ReplyTimeout;
        while (true)
        {
            var left = until - clock();
            if (left <= TimeSpan.Zero || link.Read(left) is not { } reply)
            {
                _broken = true;
                return null;
            }

            if (isReply(reply)) return reply;
        }
    }

    public void Dispose() => link.Dispose();

    private bool Send(byte[] command)
    {
        if (command.Length + 1 > reportLength || clock() > _deadline) return false;
        var report = new byte[reportLength];
        command.CopyTo(report, 1);
        // The rules, not the caller, decide what may reach a device, so no mistake above can write to a supply.
        if (!allowed(report)) return false;
        link.Flush();
        return link.Write(report, ReplyTimeout);
    }
}

/// <summary>What a power supply's own figure for its whole self covers.</summary>
internal enum PsuWatts
{
    /// <summary>The DC its rails put out. The wall draw is this over the supply's efficiency.</summary>
    DcOutput,

    /// <summary>The AC it draws from the wall, which already holds the supply's own losses and is divided by nothing.</summary>
    AcInput,
}

/// <summary>One power supply that is open, for as long as reading it keeps going well.</summary>
internal abstract class PsuSession(PsuWire wire) : IDisposable
{
    /// <summary>How long one read cycle may take, so a supply that answers slowly cannot hold up a tick.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    protected PsuWire Wire { get; } = wire;

    /// <summary>The name the device gave, once it has given one; null while only its model's name is known.</summary>
    public string? Name { get; protected set; }

    /// <summary>What <see cref="ReadWatts"/> answers with. Summing rails gives the DC output; a supply's own figure for
    /// the whole unit need not, and Corsair's is what it draws from the wall.</summary>
    public virtual PsuWatts Covers => PsuWatts.DcOutput;

    /// <summary>The supply's watts, as many as its protocol gives; null when it didn't answer as that protocol says.</summary>
    public double? ReadWatts(TimeSpan deadline)
    {
        Wire.Begin(deadline);
        return PsuMath.PlausibleWatts(Read());
    }

    public void Dispose() => Wire.Dispose();

    protected abstract double? Read();
}

/// <summary>
/// Corsair HXi and RMi. A command is a 64-byte output report of [length][command][parameter] and the reply starts with
/// the first two echoed back (the Linux corsair-psu driver, and liquidctl's notes on the same supplies). Three commands
/// are ever sent: the 0xFE handshake the driver sends before it reads anything, the supply's own total in watts, and
/// the product name. Nothing that writes to the supply — a rail to select, a fan to set, a protection mode to change —
/// is in the rules below, so no mistake here can send one.
/// </summary>
internal sealed class CorsairSession(PsuWire wire) : PsuSession(wire)
{
    private static readonly byte[] Hello = [0xFE, 0x03, 0x00];
    private static readonly byte[] AskTotalWatts = [0x03, 0xEE, 0x00];
    private static readonly byte[] AskName = [0x03, 0x9A, 0x00];

    private bool _greeted;
    private bool _named;

    /// <summary>
    /// 0xEE is read here as the AC the supply draws from the wall, not as the DC its rails put out, so nothing divides
    /// it by an efficiency. The evidence, since the two drivers PowerLedger's notes come from disagree — liquidctl
    /// calls it "total power output" and the Linux corsair-psu driver only "total watts":
    ///
    /// <list type="bullet">
    /// <item>It reads above the sum of the supply's own rail watts, by about the losses of the tier these units are
    /// built to. A figure for the DC output would have to equal that sum; only the input can sit above it.</item>
    /// <item>These units measure their AC input volts and amps as well (0x88 and 0x89), which is what a supply needs
    /// to work out the input power, and what lets iCUE and HWiNFO show an efficiency for them at all.</item>
    /// <item>PMBus has a per-rail READ_POUT and no command for a whole unit's input, so a maker who wanted the input
    /// over one command would have to add one of its own, as 0xEE is.</item>
    /// </list>
    ///
    /// The one conclusive check — 0xEE against the sum of the rails read one by one — cannot be made from here: a rail
    /// is chosen by writing the supply's page register, which these rules turn down, and PowerLedger writes to no
    /// supply. If the reading is really the DC output after all, the wall figure is low by the losses instead of high
    /// by them; treating it as the input is the way round that cannot quietly overstate what the meter will show.
    /// </summary>
    public override PsuWatts Covers => PsuWatts.AcInput;

    /// <summary>The only reports this protocol may put on the wire.</summary>
    public static bool Allows(byte[] report)
        => PsuFrames.Is(report, Hello) || PsuFrames.Is(report, AskTotalWatts) || PsuFrames.Is(report, AskName);

    protected override double? Read()
    {
        // Without the handshake the supply answers zeros; the Linux driver sends it at probe and after every resume.
        if (!_greeted)
        {
            if (Reply(Hello) is null) return null;
            _greeted = true;
        }

        // The supply's own total, which is what it draws from the wall: see the note on Covers above.
        var total = Reply(AskTotalWatts);

        // The name is asked for once a session and after the watts, so a supply that will not say keeps its model's
        // name and costs a reading nothing.
        if (!_named)
        {
            _named = true;
            if (Reply(AskName) is { } reply) Name = NameFrom(reply);
        }

        return total is null ? null : PsuMath.Linear11(total[3], total[4]);
    }

    /// <summary>The reply to one command: the report whose first two bytes are the ones that were sent. A supply that
    /// does not know a command answers with the command byte zeroed, and one that was never greeted with 0xFE, so
    /// neither is mistaken for an answer.</summary>
    private byte[]? Reply(byte[] command)
        => Wire.Ask(command, reply => reply.Length >= 5 && reply[1] == command[0] && reply[2] == command[1]);

    private static string? NameFrom(byte[] reply)
    {
        var text = new StringBuilder();
        for (var index = 3; index < reply.Length && reply[index] is >= 0x20 and < 0x7F; index++) text.Append((char)reply[index]);
        var product = text.ToString().Trim();

        // The supplies made since 2023 answer with the maker's name in the string, which would otherwise be said twice.
        if (product.StartsWith("Corsair", StringComparison.OrdinalIgnoreCase)) product = product["Corsair".Length..].Trim();
        return product.Length == 0 ? null : $"Corsair {product}";
    }
}

/// <summary>
/// NZXT E500, E650 and E850. A command goes to the supply's PMBus bridge as [0xAD][0][length][step][0x60], and a reply
/// starts 0xAA and echoes the length. Every read here is a PMBus paged read, which names the rail for that one command
/// and leaves the supply's own page alone. Each of the five rails is asked for its volts, amps and watts, and the watts
/// are summed; a rail that will not give its watts falls back to volts times amps. The bridge answers a command it was
/// too busy for with a reply that is not one, so each is asked up to three times (liquidctl's nzxt_epsu driver).
/// </summary>
internal sealed class NzxtSession(PsuWire wire) : PsuSession(wire)
{
    private const int Rails = 5;
    private const int Attempts = 3;
    private const byte Start = 0xAD;
    private const byte Answer = 0xAA;
    private const byte PagedRead = 0x06;
    private const byte VoutMode = 0x20;
    private const byte RailVolts = 0x8B;
    private const byte RailAmps = 0x8C;
    private const byte RailWatts = 0x96;

    /// <summary>liquidctl leaves the bridge's microcontroller this long between commands, which this rounds up.</summary>
    private static readonly TimeSpan Between = TimeSpan.FromMilliseconds(3);

    private readonly byte?[] _format = new byte?[Rails];

    /// <summary>The only reports this protocol may put on the wire: paged reads of the five rails, and nothing else.</summary>
    public static bool Allows(byte[] report)
    {
        if (report.Length < 10 || report[0] != 0 || !PsuFrames.RestIsZero(report, 10)) return false;
        if (report[1] != Start || report[2] != 0 || report[4] != 4 || report[5] != 0x60 || report[6] != PagedRead || report[7] != 2) return false;
        if (report[8] >= Rails) return false;
        return report[9] switch
        {
            VoutMode => report[3] == 3,
            RailVolts or RailAmps or RailWatts => report[3] == 4,
            _ => false,
        };
    }

    protected override double? Read()
    {
        double total = 0;
        for (byte rail = 0; rail < Rails; rail++)
        {
            var volts = Volts(rail);
            var amps = Linear11Of(rail, RailAmps);
            var watts = Linear11Of(rail, RailWatts);

            // The supply's own figure for the rail, or volts times amps when it didn't give one.
            var rails = NonNegative(watts) ?? (volts is { } v && amps is { } a ? NonNegative(v * a) : null);
            if (rails is null) return null;
            total += rails.Value;
        }

        return total;
    }

    private static double? NonNegative(double? value) => value is >= 0 ? value : null;

    private double? Volts(byte rail)
    {
        // The output voltage format cannot change while the supply is on, so it is asked for once a session.
        _format[rail] ??= Data(rail, VoutMode, 1)?[0];
        return _format[rail] is { } format && Data(rail, RailVolts, 2) is { } data
            ? PsuMath.ULinear16(data[0], data[1], format)
            : null;
    }

    private double? Linear11Of(byte rail, byte command)
        => Data(rail, command, 2) is { } data ? PsuMath.Linear11(data[0], data[1]) : null;

    /// <summary>One paged read, asked again while the bridge answers something that is not an answer.</summary>
    private byte[]? Data(byte rail, byte command, int length)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            Wire.Wait(Between);

            // Whatever comes back is taken here and judged below, because the bridge's "I was busy" report is not a
            // reply and has to be counted as one of the three attempts rather than waited past.
            if (Wire.Ask([Start, 0, (byte)(length + 2), 4, 0x60, PagedRead, 2, rail, command], static _ => true) is not { } reply) return null;
            if (reply.Length >= 4 + length && reply[1] == Answer && reply[2] == length + 2 && reply[3] == length)
            {
                return reply[4..(4 + length)];
            }
        }

        return null;
    }
}

/// <summary>
/// Thermaltake Toughpower DPS G. A read is [0x31][register], and the value comes back in the reply's fourth and fifth
/// bytes, low byte first (TTController's DpsgControllerProxy, which is MIT). The supply reports no total of its own, so
/// the three rails' volts and amps are read and multiplied. Only the model query TTController sends before it reads
/// anything and the six rail registers are in the rules; the 0x30 commands, which set the fan, the lighting and the
/// supply's stored profile, are not.
/// </summary>
internal sealed class DpsgSession(PsuWire wire) : PsuSession(wire)
{
    private const int Rails = 3;
    private const byte ReadRegister = 0x31;
    private const byte Volts12V = 0x34;
    private const byte Amps12V = 0x37;
    private const byte Amps3V3 = 0x39;

    private static readonly byte[] Hello = [0xFE, 0x31];

    private bool _greeted;

    /// <summary>The only reports this protocol may put on the wire.</summary>
    public static bool Allows(byte[] report)
        => PsuFrames.Is(report, Hello)
        || (report.Length >= 3 && report[0] == 0 && report[1] == ReadRegister
            && report[2] is >= Volts12V and <= Amps3V3 && PsuFrames.RestIsZero(report, 3));

    protected override double? Read()
    {
        // TTController asks the supply for its model before it reads anything, so PowerLedger opens the same way.
        if (!_greeted)
        {
            if (Reply(Hello[0], Hello[1]) is null) return null;
            _greeted = true;
        }

        double total = 0;
        for (byte rail = 0; rail < Rails; rail++)
        {
            if (Value((byte)(Volts12V + rail)) is not { } volts || Value((byte)(Amps12V + rail)) is not { } amps) return null;
            total += volts * amps;
        }

        return total;
    }

    private double? Value(byte register)
        => Reply(ReadRegister, register) is { } reply ? PsuMath.Linear11Unsigned(reply[3], reply[4]) : null;

    /// <summary>The reply to one read: the report that says which register it carries, and says the one that was asked
    /// for. The supply echoes the command and the register back, as Corsair echoes its command and NZXT its lengths,
    /// so a report that is one behind, or meant for another program, is passed over instead of decoded as this one.</summary>
    private byte[]? Reply(byte command, byte register)
        => Wire.Ask([command, register], reply => reply.Length >= 5 && reply[1] == command && reply[2] == register);
}

/// <summary>
/// The programs a power supply's maker ships to talk to it. Such a supply answers one program at a time, so PowerLedger
/// leaves the device alone while one of these is running (spec §5). The names are the ones Windows lists the processes
/// under, without ".exe": iCUE and the device plugin host it runs devices through, and Corsair's older Link 4; NZXT CAM
/// and the helper CAM 4 does its hardware work in; and Thermaltake's TT RGB Plus and DPS G App. Spaces are ignored, so
/// "TT RGB Plus" and "TTRGBPlus", which are the same program at different versions, both match.
/// </summary>
internal static class PsuPrograms
{
    private static readonly (PsuFamily Family, string Name, bool Whole, string Called)[] Programs =
    [
        (PsuFamily.Corsair, "icue", false, "iCUE"),
        (PsuFamily.Corsair, "corsairlink", false, "Corsair Link"),
        (PsuFamily.Nzxt, "nzxtcam", false, "NZXT CAM"),
        (PsuFamily.Nzxt, "cam", true, "NZXT CAM"),
        (PsuFamily.Nzxt, "cam_helper", true, "NZXT CAM"),
        (PsuFamily.Thermaltake, "ttrgbplus", false, "TT RGB Plus"),
        (PsuFamily.Thermaltake, "thermaltakedps", false, "the DPS G App"),
        (PsuFamily.Thermaltake, "dpsapp", true, "DPSApp"),
    ];

    /// <summary>The maker's program that is running and has this supply to itself, or null when none of them is.</summary>
    public static string? Holding(PsuFamily family, IEnumerable<string> processNames)
    {
        foreach (var process in processNames)
        {
            var name = process.Replace(" ", string.Empty).ToLowerInvariant();
            foreach (var program in Programs)
            {
                if (program.Family != family) continue;
                var matches = program.Whole
                    ? name == program.Name
                    : name.StartsWith(program.Name, StringComparison.Ordinal);
                if (matches) return program.Called;
            }
        }

        return null;
    }
}
