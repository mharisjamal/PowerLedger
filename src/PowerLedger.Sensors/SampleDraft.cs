using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// One tick under construction. Each source fills only the fields it owns and leaves the rest alone,
/// so a source that fails contributes nothing instead of corrupting the tick.
/// </summary>
public sealed class SampleDraft
{
    private readonly List<GpuCardDraft> _gpus = [];

    public double? CpuPackageW { get; set; }
    public double? IGpuW { get; set; }
    public double CpuLoad { get; set; }
    public double? BatteryRateW { get; set; }
    public bool OnBattery { get; set; }
    public double? Brightness { get; set; }

    /// <summary>Assumed on until the display source says otherwise, so a missing source never invents a dark screen.</summary>
    public bool DisplayOn { get; set; } = true;

    public int MonitorCount { get; set; }
    public double UserIdleSeconds { get; set; }
    public bool SessionLocked { get; set; }

    public double? UpsOutputW { get; set; }
    public UpsPowerSource UpsSource { get; set; }
    public string? UpsName { get; set; }
    public double? PsuOutputW { get; set; }

    /// <summary>What the supply says it draws from the wall, for the units that report that instead of their DC
    /// output. It is wall power already and is never divided by an efficiency.</summary>
    public double? PsuWallW { get; set; }

    public string? PsuName { get; set; }

    /// <summary>The discrete graphics cards found so far this tick, in the order the sources added them.</summary>
    public IReadOnlyList<GpuCardDraft> Gpus => _gpus;

    /// <summary>True once any source has found a discrete card.</summary>
    public bool DGpuPresent => _gpus.Count > 0;

    /// <summary>The cards' watts together, only when every card measured its own (see <see cref="GpuCard.Totals"/>).</summary>
    public double? DGpuW => Totals.Watts;

    /// <summary>The busiest card's load.</summary>
    public double? DGpuLoad => Totals.Load;

    /// <summary>The least a measured card's watts cover.</summary>
    public GpuPowerScope DGpuScope => Totals.Scope;

    private GpuTotals Totals => GpuCard.Totals(Cards());

    /// <summary>Adds a card a source has found. The card's own library adds it first; Windows' load counters then fill in
    /// what that library could not, and add each card no library reads.</summary>
    /// <param name="vendorId">The PCI vendor, e.g. 0x10DE for NVIDIA.</param>
    /// <param name="deviceId">The PCI device id, or 0 when the source doesn't know it.</param>
    public GpuCardDraft AddGpu(uint vendorId, uint deviceId = 0)
    {
        var card = new GpuCardDraft(vendorId, deviceId);
        _gpus.Add(card);
        return card;
    }

    /// <summary>Freezes the draft. Suspect is always false here; only the validator sets it.</summary>
    public Sample ToSample(DateTimeOffset timestamp, double deltaSeconds)
    {
        var cards = Cards();
        var totals = GpuCard.Totals(cards);
        return new(
            timestamp, deltaSeconds,
            CpuPackageW, IGpuW, CpuLoad,
            totals.Watts, totals.Load, totals.Present,
            BatteryRateW, OnBattery,
            Brightness, DisplayOn, MonitorCount,
            UserIdleSeconds, SessionLocked, Suspect: false,
            totals.Scope, UpsOutputW, UpsSource, UpsName, PsuOutputW, PsuName, PsuWallW,
            cards.Count > 0 ? cards : null);
    }

    private List<GpuCard> Cards() => _gpus.ConvertAll(card => card.ToCard());
}

/// <summary>One discrete graphics card as this tick's sources found it. The PCI ids let a later source recognise a card an
/// earlier one already added, so no card is counted twice.</summary>
public sealed class GpuCardDraft
{
    internal GpuCardDraft(uint vendorId, uint deviceId)
    {
        VendorId = vendorId;
        DeviceId = deviceId;
    }

    /// <summary>The PCI vendor, e.g. 0x10DE for NVIDIA.</summary>
    public uint VendorId { get; }

    /// <summary>The PCI device id, or 0 when the source that added the card doesn't know it.</summary>
    public uint DeviceId { get; }

    /// <summary>The card's name, e.g. "Quadro 6000"; null until a source knows it.</summary>
    public string? Name { get; set; }

    /// <summary>What the card's own library measured; 0 for a card Windows has switched off; null when nothing measured it.</summary>
    public double? Watts { get; set; }

    /// <summary>0..1, or null when unknown.</summary>
    public double? Load { get; set; }

    /// <summary>What <see cref="Watts"/> covers.</summary>
    public GpuPowerScope Scope { get; set; } = GpuPowerScope.Board;

    /// <summary>The card's rating for working its watts out from its load; null until a source knows the card's name or memory.</summary>
    public GpuRating? Rating { get; set; }

    internal GpuCard ToCard() => new(Name ?? "", Watts, Load, Scope, Rating?.Watts);
}
