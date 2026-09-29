using System.Globalization;
using System.Text;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors.Nut;

/// <summary>
/// The plain-text protocol of Network UPS Tools' upsd (RFC 9271), as far as reading goes: one command per line, replies
/// one line each, a list between BEGIN LIST and END LIST lines, and arguments separated by spaces with double quotes around
/// any that hold spaces, a backslash escaping a quote or a backslash inside them. Only facts of the protocol are used here;
/// none of NUT's own code.
/// </summary>
internal static class NutProtocol
{
    /// <summary>The port upsd listens on unless told otherwise.</summary>
    public const int DefaultPort = 3493;

    /// <summary>A reply line longer than this is not upsd talking; a value is a few hundred bytes at most.</summary>
    public const int MaxLineBytes = 4096;

    /// <summary>A UPS lists a few hundred variables at most; a list longer than this is not one.</summary>
    public const int MaxVariables = 2000;

    /// <summary>An argument quoted, with a quote or a backslash inside it escaped.</summary>
    public static string Quote(string argument)
    {
        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        foreach (var c in argument)
        {
            if (c is '"' or '\\') quoted.Append('\\');
            quoted.Append(c);
        }
        return quoted.Append('"').ToString();
    }

    /// <summary>A reply line split into its words, quotes removed and escapes undone.</summary>
    public static List<string> Words(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '\\' && i + 1 < line.Length) word.Append(line[++i]);
                else if (c == '"') quoted = false;
                else word.Append(c);
                continue;
            }
            if (c == '"')
            {
                quoted = true;
                inWord = true;
            }
            else if (c is ' ' or '\t')
            {
                if (inWord) words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
            else
            {
                if (c == '\\' && i + 1 < line.Length) c = line[++i];
                word.Append(c);
                inWord = true;
            }
        }
        if (inWord) words.Add(word.ToString());
        return words;
    }

    /// <summary>
    /// A UPS's output watts from its variables, from the most exact figure it offers: its real power (ups.realpower); its
    /// apparent power (ups.power) times the power factor it reports (output.powerfactor); its load (ups.load) of its rated
    /// real power; its apparent power at an assumed power factor of 0.8; and its load of its rated apparent power at its own
    /// factor or that assumed one. Only watts above nought count, as with a UPS read over USB.
    /// </summary>
    public static (double? Watts, UpsPowerSource Source) Power(IReadOnlyDictionary<string, string> variables)
    {
        double? Number(string name)
            => variables.TryGetValue(name, out var text)
               && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                ? value
                : null;

        var factor = Number("output.powerfactor") is { } f && f > 0 && f <= 1 ? f : (double?)null;
        var apparent = Number("ups.power");
        var load = Number("ups.load") is { } percent && percent is >= 0 and <= 200 ? percent / 100 : (double?)null;

        if (Number("ups.realpower") is { } real && Sane(real)) return (real, UpsPowerSource.ActivePower);
        if (apparent is { } va && factor is { } known && Sane(va * known)) return (va * known, UpsPowerSource.ApparentPower);
        if (load is { } share && Number("ups.realpower.nominal") is { } ratedWatts && Sane(share * ratedWatts))
            return (share * ratedWatts, UpsPowerSource.LoadOfRatedWatts);
        if (apparent is { } guessed && Sane(guessed * Ups.PowerFactor))
            return (guessed * Ups.PowerFactor, UpsPowerSource.ApparentPowerAssumedFactor);
        if (load is { } part && Number("ups.power.nominal") is { } ratedVa && Sane(part * ratedVa * (factor ?? Ups.PowerFactor)))
            return (part * ratedVa * (factor ?? Ups.PowerFactor), UpsPowerSource.LoadOfRatedVoltAmps);
        return (null, UpsPowerSource.None);
    }

    /// <summary>Finite, above nought and no more than a UPS could deliver.</summary>
    private static bool Sane(double watts) => double.IsFinite(watts) && watts > 0 && watts <= 100_000;
}

/// <summary>upsd answered ERR: <see cref="Code"/> is its word for why, e.g. UNKNOWN-UPS or ACCESS-DENIED.</summary>
internal sealed class NutRefusedException(string code) : Exception($"the UPS server said {code}")
{
    public string Code { get; } = code;
}
