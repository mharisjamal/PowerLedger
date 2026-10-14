namespace PowerLedger.Core;

/// <summary>A laptop's learned "rest of system": a level, plus a share of each watt the CPU package draws, for the
/// platform's own draw that rises with the processor's (its voltage regulators, memory and fabric).</summary>
/// <param name="InterceptW">The rest at a CPU drawing nothing; may be below zero, as a fitted level can be.</param>
/// <param name="PerCpuWatt">Watts of rest for each watt of CPU package power.</param>
public readonly record struct RestOfSystem(double InterceptW, double PerCpuWatt)
{
    /// <summary>The rest while the CPU package draws <paramref name="cpuW"/>, never below zero.</summary>
    public double At(double cpuW) => Math.Max(0, InterceptW + PerCpuWatt * cpuW);
}

/// <summary>Learned "rest of system" watts for a calibration bucket, or null when nothing was learned yet.</summary>
public interface IBaselineProvider
{
    /// <summary>The rest's level for the bucket (what it is at a CPU drawing nothing), or null when nothing was learned.</summary>
    double? GetBaseline(int bucket);

    /// <summary>The rest for the bucket as a level and a share of each CPU watt; a provider that learns only a level
    /// gives it with no share.</summary>
    RestOfSystem? GetRest(int bucket) => GetBaseline(bucket) is { } watts ? new RestOfSystem(watts, 0) : null;
}
