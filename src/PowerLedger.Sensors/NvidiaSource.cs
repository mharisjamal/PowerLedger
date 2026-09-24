namespace PowerLedger.Sensors;

/// <summary>One NVIDIA card as the source reads it.</summary>
/// <param name="Name">The card's name as NVML gives it; empty when it did not.</param>
/// <param name="DeviceId">The PCI device id, so Windows' load counters recognise the card; 0 when unknown.</param>
/// <param name="Rating">The card's rating from the table, or from its memory when the table does not know it; null when
/// neither is known, which leaves it to the machine's figure.</param>
/// <param name="Read">Asks NVML for the card's power and load.</param>
/// <param name="PoweredOff">Asks Windows, without waking the card, whether it has switched it off.</param>
internal sealed record NvidiaCard(string Name, uint DeviceId, GpuRating? Rating, Func<GpuReading> Read, Func<bool> PoweredOff);

/// <summary>
/// Every NVIDIA card's power, load and presence, through NVML. When Windows has switched a card off, as a switchable-graphics
/// laptop does almost all the time, it draws next to nothing and NVML is not asked about it at all. When it is on, power is
/// null on the many cards with no measurement hardware, laptop GPUs and Fermi cards among them, and the power model works
/// the card's watts out from its load and its own rating instead; a card that gives no load either, as Fermi GeForce cards
/// do not, has it filled from Windows' GPU load counters by <see cref="GpuLoadSource"/>.
/// </summary>
public sealed class NvidiaSource : ISensorSource
{
    private readonly IReadOnlyList<NvidiaCard> _cards;
    private readonly Nvml? _nvml;

    public NvidiaSource()
    {
        var nvml = new Nvml();
        _nvml = nvml;
        var devices = nvml.Devices;
        _cards = [.. devices.Select((device, index) => new NvidiaCard(
            device.Name, device.DeviceId, TdpTable.Bundled.GpuOrGuess(device.Name, device.MemoryBytes),
            () => nvml.Read(index), PowerState(device, devices.Count)))];
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
    internal NvidiaSource(IReadOnlyList<NvidiaCard> cards, bool present, string? unavailable, bool loadsEveryCard)
    {
        _cards = cards;
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
        foreach (var card in _cards)
        {
            var reading = card.PoweredOff() ? new GpuReading(true, 0, 0) : card.Read();
            if (reading.Present) readings.Add((card, reading));
        }

        foreach (var (card, reading) in readings)
        {
            var found = draft.AddGpu(DiscreteGpu.NvidiaVendor, card.DeviceId);
            if (card.Name.Length > 0) found.Name = card.Name;
            found.Rating = card.Rating;
            found.Watts = reading.PowerWatts;
            found.Load = reading.LoadFraction;
        }
    }

    public void Dispose() => _nvml?.Dispose();

    /// <summary>Whether Windows has the card switched off, asked of the display adapter with the card's PCI ids. With
    /// no device id only a machine's single NVIDIA card can be told apart; a card Windows cannot find counts as on,
    /// which never hides real draw.</summary>
    private static Func<bool> PowerState(NvmlDevice device, int cards)
    {
        var found = device.DeviceId != 0
            ? DevicePowerState.FindDisplayAdapter(FormattableString.Invariant($@"PCI\VEN_10DE&DEV_{device.DeviceId:X4}"))
            : cards == 1 ? DevicePowerState.FindNvidiaGpu() : null;
        return found is null ? static () => false : () => DevicePowerState.IsPoweredOff(found);
    }

    /// <summary>True when the card gives its utilisation, or is switched off and not to be woken to ask.</summary>
    private static bool GivesLoad(NvidiaCard card)
    {
        try
        {
            return card.PoweredOff() || card.Read().LoadFraction is not null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }
}
