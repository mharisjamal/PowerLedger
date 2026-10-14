namespace PowerLedger.Sensors;

/// <summary>One NVIDIA card as the source reads it.</summary>
/// <param name="Name">The card's name as NVML gives it; empty when it did not.</param>
/// <param name="DeviceId">The PCI device id, so Windows' load counters recognise the card; 0 when unknown.</param>
/// <param name="Rating">The card's rating from the table, or from its memory when the table does not know it; null when
/// neither is known, which leaves it to the machine's figure.</param>
/// <param name="Read">Asks NVML for the card's power and load.</param>
/// <param name="Power">Asks Windows, without waking the card, whether it has switched it on or off, or can't say.</param>
internal sealed record NvidiaCard(string Name, uint DeviceId, GpuRating? Rating, Func<GpuReading> Read, Func<DevicePower> Power)
{
    /// <summary>A card Windows always says is on or off.</summary>
    public NvidiaCard(string name, uint deviceId, GpuRating? rating, Func<GpuReading> read, Func<bool> poweredOff)
        : this(name, deviceId, rating, read, () => poweredOff() ? DevicePower.Off : DevicePower.On)
    {
    }
}

/// <summary>
/// Every NVIDIA card's power, load and presence, through NVML. When Windows has switched a card off, as a switchable-graphics
/// laptop does almost all the time, it draws next to nothing and NVML is not asked about it at all. When it is on, power is
/// null on the many cards with no measurement hardware, laptop GPUs and Fermi cards among them, and the power model works
/// the card's watts out from its load and its own rating instead; a card that gives no load either, as Fermi GeForce cards
/// do not, has it filled from Windows' GPU load counters by <see cref="GpuLoadSource"/>.
/// Whether a card is switched off is asked of Windows as seldom as is safe (<see cref="DevicePowerWatch"/>). A card whose
/// state Windows can't give, because its device can't be found, is read through NVML only every ten seconds, with the last
/// reading carrying the ticks between, since asking NVML every tick would keep a switchable-graphics laptop's GeForce awake.
/// </summary>
public sealed class NvidiaSource : ISensorSource
{
    /// <summary>How often a card whose power state is unknown is read.</summary>
    public const long UnknownReadMilliseconds = 10_000;

    private readonly IReadOnlyList<NvidiaCard> _cards;
    private readonly Nvml? _nvml;
    private readonly Func<long> _clock;
    private readonly (GpuReading Reading, long At)?[] _unknownReads;

    public NvidiaSource()
    {
        var nvml = new Nvml();
        _nvml = nvml;
        _clock = static () => Environment.TickCount64;
        var devices = nvml.Devices;
        _cards = [.. devices.Select((device, index) => new NvidiaCard(
            device.Name, device.DeviceId, TdpTable.Bundled.GpuOrGuess(device.Name, device.MemoryBytes),
            () => nvml.Read(index), PowerState(device, devices.Count, _clock)))];
        _unknownReads = new (GpuReading, long)?[_cards.Count];
        Supported = nvml.Available;
        Unavailable = nvml.Unavailable;
        LoadsEveryCard = !Supported || _cards.All(GivesLoad);
    }

    /// <summary>Test seam: one card from any source of readings and power state.</summary>
    internal NvidiaSource(Func<GpuReading> read, bool present, string? unavailable, Func<bool>? poweredOff = null)
        : this([new NvidiaCard("", 0, null, read, poweredOff ?? (static () => false))], present, unavailable, loadsEveryCard: true)
    {
    }

    /// <summary>Test seam: any cards.</summary>
    /// <param name="clockMilliseconds">A monotonic clock, for the cards whose power state is unknown.</param>
    internal NvidiaSource(IReadOnlyList<NvidiaCard> cards, bool present, string? unavailable, bool loadsEveryCard, Func<long>? clockMilliseconds = null)
    {
        _cards = cards;
        _clock = clockMilliseconds ?? (static () => Environment.TickCount64);
        _unknownReads = new (GpuReading, long)?[cards.Count];
        Supported = present;
        Unavailable = unavailable;
        LoadsEveryCard = loadsEveryCard;
    }

    public string Name => "nvidia-gpu";

    public bool Supported { get; }

    public string? Unavailable { get; }

    /// <summary>True when every card that was on when the source was built gave NVML's utilisation, so Windows' load
    /// counters are not needed for them. A card that was switched off is taken to give it, rather than woken to ask.</summary>
    internal bool LoadsEveryCard { get; }

    /// <summary>Adds each card, or none: every card is asked before any is added, so a card that throws leaves this tick
    /// with nothing from NVML and the sampler backs the whole source off.</summary>
    public void Contribute(SampleDraft draft)
    {
        var readings = new List<(NvidiaCard Card, GpuReading Reading)>(_cards.Count);
        for (var index = 0; index < _cards.Count; index++)
        {
            var card = _cards[index];
            var reading = card.Power() switch
            {
                DevicePower.Off => new GpuReading(true, 0, 0),
                DevicePower.Unknown => ReadSeldom(index),
                _ => card.Read(),
            };
            if (reading.Present) readings.Add((card, reading));
        }

        foreach (var (card, reading) in readings)
        {
            var found = draft.AddGpu(DiscreteGpu.NvidiaVendor, card.DeviceId);
            if (card.Name.Length > 0) found.Name = card.Name;
            found.Rating = card.Rating;
            found.Watts = reading.PowerWatts;
            found.Exact = reading.Exact;
            found.Load = reading.LoadFraction;
        }
    }

    public void Dispose() => _nvml?.Dispose();

    /// <summary>A card whose power state is unknown, read through NVML once every ten seconds; the last reading between.</summary>
    private GpuReading ReadSeldom(int index)
    {
        var now = _clock();
        if (_unknownReads[index] is { } last && now - last.At < UnknownReadMilliseconds) return last.Reading;
        var reading = _cards[index].Read();
        _unknownReads[index] = (reading, now);
        return reading;
    }

    /// <summary>Whether Windows has the card switched off, asked of the display adapter with the card's PCI ids. With
    /// no device id only a machine's single NVIDIA card can be told apart; any other is unknown, which never hides real
    /// draw and never polls the card every tick.</summary>
    private static Func<DevicePower> PowerState(NvmlDevice device, int cards, Func<long> clock)
    {
        Func<string?> find = device.DeviceId != 0
            ? () => DevicePowerState.FindDisplayAdapter(FormattableString.Invariant($@"PCI\VEN_10DE&DEV_{device.DeviceId:X4}"))
            : cards == 1 ? DevicePowerState.FindNvidiaGpu : static () => null;
        return new DevicePowerWatch(find, DevicePowerState.Nodes, clock).State;
    }

    /// <summary>True when the card gives its utilisation, or is switched off and not to be woken to ask.</summary>
    private static bool GivesLoad(NvidiaCard card)
    {
        try
        {
            return card.Power() == DevicePower.Off || card.Read().LoadFraction is not null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }
}
