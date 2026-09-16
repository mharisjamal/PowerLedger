using System.Globalization;
using System.Reflection;
using System.Text;
using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>A certified monitor from the shipped Energy Star table: what it is listed as, and what it drew when tested.</summary>
/// <param name="Panel">The panel as the list gives it ("IPS LCD"), or empty.</param>
/// <param name="OnW">On-mode watts, measured at the test's brightness.</param>
/// <param name="MaxNits">The maximum luminance in cd/m², or null where the list doesn't give one (it writes 0).</param>
public sealed record CatalogueMonitor(string Brand, string ModelNumber, string ModelName, double Inches, int Width, int Height,
    string Panel, double OnW, double SleepW, double? MaxNits = null);

/// <summary>
/// Energy Star's certified monitors (spec §5), shipped with PowerLedger: looks a detected monitor up by maker, model name
/// and size.
/// <para>
/// A listing is indexed under its model number, its model name and each of its alternative identifiers. Both sides are
/// normalised the same way (letters and digits only, in upper case, without the words "monitor" and "display" and without
/// the maker's name or code in front) and matched whole, never by substring. Dell's revision letter (U2723QEt) and Acer's
/// suffix (B196L_q) are indexed without them too, and so is the last word of an identifier of several, because the series
/// word the list puts before a model (FlexScan EV2740X, PRO MP243X) is one the monitor leaves out of its name.
/// </para>
/// <para>
/// EDID holds at most thirteen characters of a monitor's name, so a name that long may have been cut short. It also matches
/// the identifiers it is the start of, as if a placeholder stood for the rest.
/// </para>
/// <para>
/// The list writes placeholders into identifiers, and they count only at the end: a run of <c>*</c>, <c>#</c> or
/// <c>?</c>, or of three or more <c>X</c> or <c>y</c>, stands for up to that many characters, with dashes between runs
/// standing for nothing. So 27UP850-* is the 27UP850-W and the 27UP850 but not the 27UP850N-W, and the single X of the
/// U2723QX is a letter. An identifier with a placeholder anywhere else can't be matched as written and is left out, and so
/// is anything shorter than four characters or without both a letter and a digit, which names a family, not a model.
/// </para>
/// <para>
/// Only a whole identifier, with or without Dell's letter or Acer's suffix, is a model's exact name or number. The last
/// word of one, a placeholder and a name cut short can each take in other models, so a monitor whose maker code has no
/// brand here, and could be any maker's, matches exact names only; otherwise Vizio's VA220E would be taken for a ViewSonic
/// of the VA22*********** family.
/// </para>
/// <para>
/// A match must agree with the monitor's size to within an inch, which stops vague names matching the wrong panel; for a
/// monitor that doesn't give its size, the listings its name matches must agree on one among themselves. A match by
/// anything but an exact name must also be listed at the monitor's resolution, either way round, when the monitor gives
/// one, which stops a family taking in a sibling of another resolution: ViewSonic's 1080p VX2418 lists VX24***********,
/// which would otherwise take in the 4K VX2478-4K-HD.
/// </para>
/// <para>
/// An exact name keeps its listing whatever resolution the monitor gives. Over linuxhw's collection of real EDIDs, 42 of
/// 1,412 exact matches disagreed with their listing's resolution, and 37 of them, of 17 models, were the listed model all
/// the same. Most of those give a mode other than their panel's, as Dell's 5120 × 2160 U4025QW gives 2560 × 1080 and its
/// 8K UP3218K 3840 × 2160, and the list has Philips' 329P1 at 3840 × 2169; an estimate from the resolution they give would
/// be off the listing by a third at the median. The other five, of two models, matched another model's listing: ASUS's
/// 4K PA328Q names itself PA328, the list's model number for its 1440p PA328CGV, and Acer's 1366 × 768 V206HQLB is taken
/// for its 1600 × 900 V206HQL b, because names are compared in upper case.
/// </para>
/// <para>
/// Of the listings that match, those with the monitor's resolution, either way round, win; then a whole name over a
/// placeholder, and more characters before the placeholder over fewer; then the closest size. Listings still tied are one
/// monitor listed more than once, often with different figures, so the answer is the first of them with their median
/// watts.
/// </para>
/// </summary>
public sealed class MonitorCatalogue
{
    private const string ResourceName = "PowerLedger.Sensors.Monitors.energy-star-monitors.csv";

    /// <summary>Room for the rounding in sizes such as 23.8, so that 24.8 is still within an inch of it.</summary>
    private const double Rounding = 1e-9;

    /// <summary>How far a listing's size may be from the monitor's, in inches.</summary>
    private const double SizeTolerance = 1.0 + Rounding;

    /// <summary>The shortest identifier worth matching; anything shorter ("E27", "M15") names a family.</summary>
    private const int ShortestKey = 4;

    /// <summary>The shortest run of X or y that is a placeholder rather than part of a name.</summary>
    private const int ShortestLetterRun = 3;

    /// <summary>The most characters of a name EDID holds.</summary>
    private const int EdidNameLength = 13;

    private const string Placeholders = "*#?";

    /// <summary>How closely a whole identifier matches. A placeholder match ranks by the length of what comes before it.</summary>
    private const int Whole = int.MaxValue;

    private static readonly Lazy<MonitorCatalogue> Loaded = new(Load, isThreadSafe: true);

    private readonly CatalogueMonitor[] _monitors;

    /// <summary>Listings by whole identifier.</summary>
    private readonly Dictionary<string, List<int>> _whole = new(StringComparer.Ordinal);

    /// <summary>Listings by the last word of an identifier of several, where that isn't also a whole identifier of theirs.</summary>
    private readonly Dictionary<string, List<int>> _lastWords = new(StringComparer.Ordinal);

    /// <summary>Listings by the part of an identifier before its placeholders, with how many characters they stand for.</summary>
    private readonly Dictionary<string, List<(int Listing, int Run)>> _prefixes = new(StringComparer.Ordinal);

    private MonitorCatalogue(List<(CatalogueMonitor Monitor, string[] Alternatives)> listings)
    {
        _monitors = [.. listings.Select(listing => listing.Monitor)];
        for (var listing = 0; listing < listings.Count; listing++)
        {
            var (monitor, alternatives) = listings[listing];
            var keys = new HashSet<(string Key, int Run)>();
            var lastWords = new HashSet<(string Key, int Run)>();
            foreach (var identifier in alternatives.Prepend(monitor.ModelName).Prepend(monitor.ModelNumber))
            {
                keys.UnionWith(Keys(identifier, monitor.Brand));
                if (identifier.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is [_, .., var last])
                {
                    lastWords.UnionWith(Keys(last, monitor.Brand));
                }
            }
            lastWords.ExceptWith(keys);
            foreach (var (key, run) in keys)
            {
                if (run == 0) Add(_whole, key, listing);
                else Add(_prefixes, key, (listing, run));
            }
            foreach (var (key, run) in lastWords)
            {
                if (run == 0) Add(_lastWords, key, listing);
                else Add(_prefixes, key, (listing, run));
            }
        }
    }

    /// <summary>The table shipped with the app, read once.</summary>
    public static MonitorCatalogue Shipped => Loaded.Value;

    /// <summary>Every listing, in the table's order.</summary>
    public IReadOnlyList<CatalogueMonitor> Monitors => _monitors;

    /// <summary>Reads a table in the shipped format (assets/monitors/README.md): a header naming the columns, then a listing
    /// a line, with RFC 4180 quoting and either line ending.</summary>
    /// <exception cref="InvalidDataException">A column PowerLedger reads is missing, or a line has the wrong number of fields
    /// or a number that isn't one.</exception>
    public static MonitorCatalogue Parse(TextReader csv)
    {
        using var lines = Records(csv).GetEnumerator();
        List<string> header = lines.MoveNext() ? lines.Current.Fields : [];
        var brand = Column(header, "brand");
        var modelNumber = Column(header, "model_number");
        var modelName = Column(header, "model_name");
        var alternatives = Column(header, "alternatives");
        var inches = Column(header, "inches");
        var width = Column(header, "width");
        var height = Column(header, "height");
        var panel = Column(header, "panel");
        var onW = Column(header, "on_w");
        var sleepW = Column(header, "sleep_w");
        var maxNits = Column(header, "max_nits");

        var listings = new List<(CatalogueMonitor, string[])>();
        while (lines.MoveNext())
        {
            var (line, fields) = lines.Current;
            if (fields.Count != header.Count)
            {
                throw new InvalidDataException($"The monitor table's line {line} has {fields.Count} fields, not {header.Count}.");
            }

            double Number(int column) =>
                double.TryParse(fields[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                    ? value
                    : throw new InvalidDataException($"The monitor table's line {line} has \"{fields[column]}\" for {header[column]}, which isn't a number.");

            int Pixels(int column) =>
                int.TryParse(fields[column], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw new InvalidDataException($"The monitor table's line {line} has \"{fields[column]}\" for {header[column]}, which isn't a whole number.");

            var nits = fields[maxNits].Length == 0 ? 0 : Number(maxNits);
            var monitor = new CatalogueMonitor(
                fields[brand], fields[modelNumber], fields[modelName],
                Number(inches), Pixels(width), Pixels(height), fields[panel],
                Number(onW), fields[sleepW].Length == 0 ? MonitorPower.DefaultSleepW : Number(sleepW), nits > 0 ? nits : null);
            listings.Add((monitor, fields[alternatives].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }
        return new MonitorCatalogue(listings);
    }

    /// <summary>The certified monitor this one is, or null when the list doesn't know it.</summary>
    /// <param name="maker">The maker code from the monitor's EDID, such as "DEL". A code without a brand here matches any brand,
    /// but only by a model's exact name or number.</param>
    /// <param name="name">The name the monitor gives, such as "DELL U2723QE". One of thirteen characters, all EDID holds, may
    /// have been cut short.</param>
    /// <param name="inches">The monitor's diagonal, or 0 when it doesn't give one.</param>
    /// <param name="width">The native width in pixels, or 0 when unknown. The resolution may be given either way round.</param>
    /// <param name="height">The native height in pixels, or 0 when unknown.</param>
    /// <returns>The listing or, for a monitor listed more than once, the first of its listings with their median watts.</returns>
    public CatalogueMonitor? Find(string maker, string name, double inches, int width, int height)
    {
        var brand = MonitorMakers.Brand(maker);
        var key = Normalise(name, brand);
        if (!IsKey(key)) return null;
        var sized = double.IsFinite(inches) && inches > 0;
        var resolved = width > 0 && height > 0;

        var matches = Matches(key, cut: name.Trim().Length == EdidNameLength)
            .Where(match => brand is null ? match.Exact : _monitors[match.Listing].Brand.Equals(brand, StringComparison.OrdinalIgnoreCase))
            .Where(match => !sized || Math.Abs(_monitors[match.Listing].Inches - inches) <= SizeTolerance)
            .Where(match => match.Exact || !resolved || HasResolution(_monitors[match.Listing], width, height))
            .OrderBy(match => match.Listing)
            .ToList();
        if (matches.Count == 0) return null;

        if (resolved && matches.Exists(match => HasResolution(_monitors[match.Listing], width, height)))
        {
            matches = matches.FindAll(match => HasResolution(_monitors[match.Listing], width, height));
        }
        var closest = matches.Max(match => match.Closeness);
        var tied = matches.FindAll(match => match.Closeness == closest).ConvertAll(match => _monitors[match.Listing]);
        if (sized)
        {
            var nearest = tied.Min(monitor => Math.Abs(monitor.Inches - inches));
            tied = tied.FindAll(monitor => Math.Abs(monitor.Inches - inches) <= nearest + Rounding);
        }
        else if (tied.Max(monitor => monitor.Inches) - tied.Min(monitor => monitor.Inches) > SizeTolerance)
        {
            return null;
        }

        return tied.Count == 1 ? tied[0] : tied[0] with
        {
            OnW = Median(tied.Select(monitor => monitor.OnW)),
            SleepW = Median(tied.Select(monitor => monitor.SleepW)),
        };
    }

    /// <summary>A name as the catalogue compares it: letters and digits only, in upper case, without the words "monitor"
    /// and "display", and without the brand's name or maker code in front, so "DELL U2723QE" and "LEN T24i-10" become
    /// "U2723QE" and "T24I10".</summary>
    internal static string Normalise(string text, string? brand)
    {
        var normalised = new StringBuilder(text.Length);
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && char.IsAsciiLetterOrDigit(text[i]))
            {
                if (start < 0) start = i;
                continue;
            }
            if (start < 0) continue;
            var word = text.AsSpan(start, i - start);
            if (!word.Equals("MONITOR", StringComparison.OrdinalIgnoreCase) && !word.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var character in word) normalised.Append(char.ToUpperInvariant(character));
            }
            start = -1;
        }

        var key = normalised.ToString();
        if (brand is null) return key;
        foreach (var prefix in MonitorMakers.Codes(brand).Prepend(Normalise(brand, null)))
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)) return key.Length > prefix.Length ? key[prefix.Length..] : key;
        }
        return key;
    }

    /// <summary>The middle value, or the mean of the middle two.</summary>
    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>Each listing a normalised name matches, with how closely, and whether by its exact name or number: one of
    /// its whole identifiers.</summary>
    /// <param name="cut">Whether the name may have been cut short, so that it also matches, as far as it goes, the
    /// identifiers it is the start of.</param>
    private List<(int Listing, int Closeness, bool Exact)> Matches(string key, bool cut)
    {
        var found = new Dictionary<int, (int Closeness, bool Exact)>();
        void Match(int listing, int howClosely, bool exact)
        {
            var (closeness, wasExact) = found.GetValueOrDefault(listing);
            found[listing] = (Math.Max(closeness, howClosely), wasExact || exact);
        }

        foreach (var listing in _whole.GetValueOrDefault(key) ?? [])
        {
            Match(listing, Whole, exact: true);
        }
        foreach (var listing in _lastWords.GetValueOrDefault(key) ?? [])
        {
            Match(listing, Whole, exact: false);
        }
        for (var length = ShortestKey; length <= key.Length; length++)
        {
            foreach (var (listing, run) in _prefixes.GetValueOrDefault(key[..length]) ?? [])
            {
                if (key.Length - length <= run) Match(listing, length, exact: false);
            }
        }
        if (cut)
        {
            foreach (var (identifier, listings) in _whole.Concat(_lastWords))
            {
                if (identifier.Length <= key.Length || !identifier.StartsWith(key, StringComparison.Ordinal)) continue;
                foreach (var listing in listings) Match(listing, key.Length, exact: false);
            }
            foreach (var (stem, listings) in _prefixes)
            {
                if (stem.Length <= key.Length || !stem.StartsWith(key, StringComparison.Ordinal)) continue;
                foreach (var (listing, _) in listings) Match(listing, key.Length, exact: false);
            }
        }
        return [.. found.Select(pair => (pair.Key, pair.Value.Closeness, pair.Value.Exact))];
    }

    /// <summary>What one of a listing's identifiers is indexed under: each key, with how many characters its trailing
    /// placeholders stand for (0 for none).</summary>
    private static IEnumerable<(string Key, int Run)> Keys(string identifier, string brand)
    {
        var (stem, run) = WithoutPlaceholders(identifier);
        if (stem.AsSpan().IndexOfAny(Placeholders) >= 0) yield break;

        var forms = new List<string> { stem };
        if (run == 0)
        {
            // Dell lists the U2723QE as the U2723QEt: a lower-case revision letter after a capital.
            if (stem.Length > 1 && char.IsAsciiLetterLower(stem[^1]) && char.IsAsciiLetterUpper(stem[^2])) forms.Add(stem[..^1]);

            // Acer lists the B196L as the B196L_q: an underscore and one or two lower-case letters.
            var underscore = stem.LastIndexOf('_');
            if (underscore > 0 && stem.Length - underscore - 1 is 1 or 2 && stem[(underscore + 1)..].All(char.IsAsciiLetterLower))
            {
                forms.Add(stem[..underscore]);
            }
        }

        foreach (var form in forms)
        {
            var key = Normalise(form, brand);
            if (IsKey(key)) yield return (key, run);
        }
    }

    /// <summary>An identifier without its trailing placeholders, and how many characters they stand for.</summary>
    private static (string Stem, int Run) WithoutPlaceholders(string identifier)
    {
        var text = identifier.AsSpan().Trim();
        var run = 0;
        while (true)
        {
            // A dash or a space before a run stands for nothing: 27UP850-* and 40B990##-#.
            var end = text.Length;
            while (end > 0 && !char.IsAsciiLetterOrDigit(text[end - 1]) && !Placeholders.Contains(text[end - 1])) end--;

            var start = end;
            while (start > 0 && Placeholders.Contains(text[start - 1])) start--;
            if (start == end && end > 0 && (char.ToUpperInvariant(text[end - 1]) is 'X' or 'Y'))
            {
                var letter = char.ToUpperInvariant(text[end - 1]);
                while (start > 0 && char.ToUpperInvariant(text[start - 1]) == letter) start--;
                if (end - start < ShortestLetterRun) start = end;
            }

            if (start == end) return (text[..end].ToString(), run);
            run += end - start;
            text = text[..start];
        }
    }

    private static bool IsKey(string key) => key.Length >= ShortestKey && key.Any(char.IsAsciiLetter) && key.Any(char.IsAsciiDigit);

    private static bool HasResolution(CatalogueMonitor monitor, int width, int height)
        => (monitor.Width == width && monitor.Height == height) || (monitor.Width == height && monitor.Height == width);

    private static int Column(List<string> header, string name)
    {
        var index = header.IndexOf(name);
        return index >= 0 ? index : throw new InvalidDataException($"The monitor table has no {name} column.");
    }

    private static void Add<T>(Dictionary<string, List<T>> index, string key, T value)
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(value);
    }

    /// <summary>A table's records, each with the line it starts on. A quoted field may hold commas, doubled quotes and line
    /// breaks; blank lines are skipped.</summary>
    private static IEnumerable<(int Line, List<string> Fields)> Records(TextReader reader)
    {
        var text = reader.ReadToEnd();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;
        var start = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character == '\n') line++;
            if (quoted)
            {
                if (character != '"')
                {
                    field.Append(character);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (character == '\n')
            {
                if (fields.Count > 0 || field.Length > 0)
                {
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return (start, fields);
                    fields = [];
                }
                start = line;
            }
            else if (character != '\r')
            {
                field.Append(character);
            }
        }
        if (fields.Count > 0 || field.Length > 0)
        {
            fields.Add(field.ToString());
            yield return (start, fields);
        }
    }

    private static MonitorCatalogue Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("energy-star-monitors.csv is missing from the assembly");
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }
}
