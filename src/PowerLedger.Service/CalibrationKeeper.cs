using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// The calibration learner for the machine in use, keyed by its hardware hash (spec §5). Saved on a timer, on suspend,
/// on stop and before switching machines; never by comparing states, which compare by reference (Plan A handoff).
/// </summary>
internal sealed class CalibrationKeeper(CalibrationRepository repository, CalibrationOptions options, TimeSpan saveEvery)
{
    private DateTimeOffset _lastSave;

    public CalibrationLearner Learner { get; private set; } = new(options);

    public string? Hash { get; private set; }

    /// <summary>Switches to the learner for <paramref name="hash"/>, saving the current one first. A no-op for the same hash.</summary>
    public void Use(string hash, DateTimeOffset now)
    {
        if (hash == Hash) return;
        if (Hash is not null) Save(now);
        var learner = new CalibrationLearner(options);
        learner.Import(repository.Load(hash));
        Learner = learner;
        Hash = hash;
        _lastSave = now;
    }

    public void SaveIfDue(DateTimeOffset now)
    {
        if (now - _lastSave >= saveEvery) Save(now);
    }

    public void Save(DateTimeOffset now)
    {
        if (Hash is null) return;
        repository.Save(Hash, Learner.Export(), now);
        _lastSave = now;
    }

    /// <summary>Forgets what was learned for this machine, in memory and in storage.</summary>
    public void Reset(DateTimeOffset now)
    {
        Learner.Reset();
        if (Hash is not null) repository.Clear(Hash);
        _lastSave = now;
    }

    public CalibrationStatus Status()
    {
        var trusted = 0;
        for (var bucket = CalibrationBuckets.DisplayOff; bucket <= CalibrationBuckets.MaxBucket; bucket++)
        {
            if (Learner.GetBaseline(bucket) is not null) trusted++;
        }
        return new CalibrationStatus(Learner.TotalSamples, options.MinTotalSamples, trusted, CalibrationBuckets.Count);
    }
}
