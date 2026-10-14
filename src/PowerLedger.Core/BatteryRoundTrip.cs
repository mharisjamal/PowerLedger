namespace PowerLedger.Core;

/// <summary>
/// What energy drawn from a laptop's battery cost at the wall (accuracy A9). The battery was filled through the adapter, the
/// charger and the cells, each losing some on the way, so the wall supplied the discharge over adapter x charger x cell
/// efficiency. Energy from the battery is counted at that wall equivalent, its discharge times <see cref="Factor"/>, about
/// 1.22; the live watts stay at the pack. Nothing is counted twice: charging is never counted (the battery rule, see the
/// model), so energy that goes into the battery is counted once, as it comes out, and a UPS's or input meter's total has
/// the charge taken off it. A discharge on AC, when the adapter can't carry the load, is a floor under the total and stays
/// at the pack. A desktop's UPS on its battery through a power cut, which Windows may not mark as a UPS, counts the same
/// way: it too was filled through a charger.
/// </summary>
public static class BatteryRoundTrip
{
    /// <summary>The share of what it is given that a laptop's charger puts into the cells: 92 to 96% at the currents a laptop
    /// charges at.</summary>
    public const double ChargerEfficiency = 0.94;

    /// <summary>The share of what goes into lithium-ion cells that comes back out at a laptop's rates: 95 to 99%.</summary>
    public const double CellEfficiency = 0.97;

    /// <summary>The adapter at the load charging puts on it, about half its rating (<see cref="AdapterEfficiency"/>).</summary>
    public static double ChargingAdapterEfficiency { get; } = AdapterEfficiency.At(0.5);

    /// <summary>What each watt-hour the battery gives cost at the wall: about 1.22.</summary>
    public static double Factor { get; } = 1 / (ChargingAdapterEfficiency * ChargerEfficiency * CellEfficiency);
}
