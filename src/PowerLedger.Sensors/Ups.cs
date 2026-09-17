using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <param name="Answered">False only when the UPS was asked for reports and answered none of them, which is what a
/// UPS that has been unplugged looks like.</param>
/// <param name="Watts">The output watts this round found, or null when the UPS gave nothing usable.</param>
/// <param name="Source">Which of the three ways <paramref name="Watts"/> came from.</param>
internal readonly record struct UpsRead(bool Answered, double? Watts, UpsPowerSource Source);

/// <summary>
/// One UPS attached over USB, as the HID Power Device class describes it: the collections Windows opened for it, the
/// fields of its descriptor that say what its outlets draw, and the watts those fields add up to.
///
/// Which field means what is decided by the collection holding it, because the same usage means different things in
/// different places: ActivePower under Output is the whole output, while the same usage under an Outlet is one socket
/// group and under Input is what the mains delivers. The places real UPSes use come from NUT's subdrivers, which map
/// UPS.Output.ActivePower and UPS.Output.PercentLoad (CyberPower), UPS.PowerConverter.PercentLoad and
/// UPS.Output.ConfigActivePower (APC), and UPS.PowerSummary.PercentLoad, UPS.PowerConverter.Output.ActivePower and
/// UPS.Flow.[4].ConfigActivePower (MGE and Eaton).
/// </summary>
internal sealed class Ups : IDisposable
{
    /// <summary>The power device usage page, which every usage below is on.</summary>
    public const ushort PowerPage = 0x84;

    /// <summary>The usage of a top-level collection that is a UPS.</summary>
    public const ushort UpsUsage = 0x04;

    /// <summary>The usage of a top-level collection that is a power summary, which some devices publish instead.</summary>
    public const ushort SummaryUsage = 0x24;

    /// <summary>What a load percentage is multiplied by when only the volt-ampere rating is known (spec §4). NUT's
    /// Eaton subdriver assumes the same 0.8 where a UPS reports no real power.</summary>
    public const double PowerFactor = 0.8;

    private const ushort PowerConverter = 0x16;
    private const ushort Output = 0x1C;
    private const ushort Flow = 0x1E;
    private const ushort ActivePower = 0x34;
    private const ushort PercentLoad = 0x35;
    private const ushort ConfigApparentPower = 0x43;
    private const ushort ConfigActivePower = 0x44;

    /// <summary>The collection type of MGE's output flow, the one NUT writes "UPS.Flow.[4]"; its other flows are the
    /// mains input and the bypass, whose ratings say nothing about what the outlets can deliver.</summary>
    private const byte OutputFlow = 0x84;

    /// <summary>More than this is a descriptor PowerLedger has misread, not a UPS: the largest units that speak HID
    /// over USB are tens of kilowatts.</summary>
    private const double MaxWatts = 100_000;

    /// <summary>A load this far above the rating is nonsense; a real UPS drops its outlets long before it.</summary>
    private const double MaxPercent = 200;

    /// <summary>Where the output's own power may sit for it to mean the whole output, best first.</summary>
    private static readonly ushort[] OutputOnly = [Output];

    /// <summary>Where the load percentage may sit, best first; the last is the top-level collection itself.</summary>
    private static readonly ushort[] LoadPlaces = [Output, PowerConverter, SummaryUsage, UpsUsage];

    /// <summary>Where a rating may sit, best first. Flow is last and only MGE's output flow counts.</summary>
    private static readonly ushort[] RatingPlaces = [Output, PowerConverter, SummaryUsage, UpsUsage, Flow];

    private readonly IReadOnlyList<IHidCollection> _collections;
    private readonly Field? _activePower;
    private readonly Field? _percentLoad;
    private readonly Field? _wattRating;
    private readonly Field? _voltAmpRating;
    private double? _ratedWatts;
    private double? _ratedVoltAmps;

    /// <param name="collections">The collections of one device, in the order Windows listed them.</param>
    public Ups(IReadOnlyList<IHidCollection> collections)
    {
        _collections = collections;
        _activePower = Choose(collections, ActivePower, OutputOnly);
        _percentLoad = Choose(collections, PercentLoad, LoadPlaces);
        _wattRating = Choose(collections, ConfigActivePower, RatingPlaces);
        _voltAmpRating = Choose(collections, ConfigApparentPower, RatingPlaces);
        Name = collections.Count > 0 ? NameOf(collections[0]) : "UPS";
    }

    /// <summary>What to call it on the status screen, from the names the device reports.</summary>
    public string Name { get; }

    /// <summary>True when the descriptor offers some way to the output's watts; a UPS that offers none is still a UPS.</summary>
    public bool Offers => _activePower is not null || (_percentLoad is not null && (_wattRating is not null || _voltAmpRating is not null));

    /// <summary>
    /// One round of reads, taking the watts from the most exact source this UPS has (spec §4): its output's own active
    /// power, else its load percentage of its rated watts, else of its rated volt-amperes at an assumed power factor.
    /// Each report is read once however many of the fields it holds, and the ratings, which never change, are read
    /// once and kept.
    /// </summary>
    public UpsRead Read()
    {
        var round = new Round();
        var (watts, source) = Watts(round);
        return new UpsRead(!round.Asked || round.Answered, watts, source);
    }

    public void Dispose()
    {
        foreach (var collection in _collections) collection.Dispose();
    }

    private (double? Watts, UpsPowerSource Source) Watts(Round round)
    {
        if (round.Value(_activePower) is { } active && Sane(active)) return (active, UpsPowerSource.ActivePower);

        // A load of zero is read like any other; the nought watts it makes are what Sane then turns down.
        if (round.Value(_percentLoad) is not { } percent || percent < 0 || percent > MaxPercent) return (null, UpsPowerSource.None);
        var load = percent / 100;

        _ratedWatts ??= Rating(round, _wattRating);
        if (_ratedWatts is { } rated && Sane(load * rated)) return (load * rated, UpsPowerSource.LoadOfRatedWatts);

        _ratedVoltAmps ??= Rating(round, _voltAmpRating);
        if (_ratedVoltAmps is { } voltAmps && Sane(load * voltAmps * PowerFactor))
        {
            return (load * voltAmps * PowerFactor, UpsPowerSource.LoadOfRatedVoltAmps);
        }
        return (null, UpsPowerSource.None);
    }

    private static double? Rating(Round round, Field? field)
        => round.Value(field) is { } rating && rating > 0 && rating <= MaxWatts ? rating : null;

    /// <summary>Watts that can be believed: finite, above zero, and no more than a UPS could deliver. The outlets of a UPS
    /// holding a running PC never draw nothing, so nought watts is a load below the first percent of the rating or a sensor
    /// that has died, and the model would otherwise take it as the machine's whole total.</summary>
    private static bool Sane(double watts) => double.IsFinite(watts) && watts > 0 && watts <= MaxWatts;

    /// <summary>The field for a usage, from the best place any of the device's collections has it in.</summary>
    private static Field? Choose(IReadOnlyList<IHidCollection> collections, ushort usage, ushort[] places)
    {
        Field? chosen = null;
        var best = int.MaxValue;
        foreach (var collection in collections)
        {
            foreach (var value in collection.Values)
            {
                // Windows reads single values only; a usage spread over several fields is a value array and is left alone.
                if (value.UsagePage != PowerPage || value.Usage != usage || value.ReportCount != 1) continue;
                if (value.Collection >= collection.Collections.Count) continue;

                var holder = collection.Collections[value.Collection];
                if (holder.UsagePage != PowerPage) continue;
                if (holder.Usage == Flow && holder.Type != OutputFlow) continue;

                var rank = Array.IndexOf(places, holder.Usage);
                if (rank < 0 || rank >= best) continue;
                chosen = new Field(collection, value);
                best = rank;
            }
        }
        return chosen;
    }

    /// <summary>The device's two names joined, e.g. "American Power Conversion Back-UPS ES 850G2".</summary>
    private static string NameOf(IHidCollection collection)
    {
        var maker = Tidy(collection.Manufacturer);
        var product = Tidy(collection.Product);

        // APC puts its firmware versions in the product string, as "Back-UPS ES 850G2 FW:931.a10.D USB FW:a10", and
        // NUT's APC subdriver cuts the model at the same "FW:".
        var firmware = product.IndexOf(" FW:", StringComparison.OrdinalIgnoreCase);
        if (firmware > 0) product = product[..firmware].TrimEnd();

        if (product.Length == 0) return maker.Length == 0 ? "UPS" : maker;
        if (maker.Length == 0 || product.StartsWith(maker, StringComparison.OrdinalIgnoreCase)) return product;
        return $"{maker} {product}";
    }

    private static string Tidy(string? name)
        => string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <param name="Collection">The collection whose device answers for this field.</param>
    /// <param name="Value">The field itself.</param>
    private readonly record struct Field(IHidCollection Collection, HidValue Value);

    /// <summary>One round of reads. Each report is read at most once, however many of the fields wanted it holds.</summary>
    private sealed class Round
    {
        private readonly Dictionary<(IHidCollection Collection, byte Report), byte[]?> _reports = [];

        /// <summary>Whether the UPS was asked for anything at all this round.</summary>
        public bool Asked { get; private set; }

        /// <summary>Whether any report came back.</summary>
        public bool Answered { get; private set; }

        public double? Value(Field? field)
        {
            if (field is not { } wanted) return null;

            var key = (wanted.Collection, wanted.Value.ReportId);
            if (!_reports.TryGetValue(key, out var report))
            {
                Asked = true;
                report = wanted.Collection.Feature(wanted.Value.ReportId);
                Answered |= report is not null;
                _reports[key] = report;
            }
            if (report is null) return null;
            return wanted.Collection.Raw(wanted.Value, report) is { } raw ? wanted.Value.Physical(raw) : null;
        }
    }
}
