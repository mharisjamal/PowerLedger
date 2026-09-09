using Microsoft.Data.Sqlite;
using PowerLedger.Core;

namespace PowerLedger.Storage;

/// <summary>Learned baselines, keyed by hardware inventory hash so a hardware change never reuses stale numbers.</summary>
public sealed class CalibrationRepository(SqliteDatabase db)
{
    /// <summary>Replaces everything stored for this hardware set. An empty state clears it.</summary>
    public void Save(string inventoryHash, CalibrationState state, DateTimeOffset now)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand())
        {
            del.CommandText = "DELETE FROM calibration WHERE inventory_hash = $hash";
            Rows.Add(del, "$hash", inventoryHash);
            del.ExecuteNonQuery();
        }
        using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT INTO calibration(inventory_hash, bucket, baseline_w, samples, updated_ms) VALUES ($hash, $bucket, $baseline, $samples, $updated)";
            var hash = ins.Parameters.Add("$hash", SqliteType.Text);
            var bucket = ins.Parameters.Add("$bucket", SqliteType.Integer);
            var baseline = ins.Parameters.Add("$baseline", SqliteType.Real);
            var samples = ins.Parameters.Add("$samples", SqliteType.Integer);
            var updated = ins.Parameters.Add("$updated", SqliteType.Integer);
            hash.Value = inventoryHash;
            updated.Value = Rows.Ms(now);
            foreach (var b in state.Buckets)
            {
                bucket.Value = b.Bucket;
                baseline.Value = b.BaselineW;
                samples.Value = b.Samples;
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>The stored buckets for this hardware set, ordered by bucket; empty when nothing was learned. Values are not validated here; CalibrationLearner.Import filters corrupt rows.</summary>
    public CalibrationState Load(string inventoryHash)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT bucket, baseline_w, samples FROM calibration WHERE inventory_hash = $hash ORDER BY bucket";
        Rows.Add(cmd, "$hash", inventoryHash);
        using var r = cmd.ExecuteReader();
        var buckets = new List<BucketState>();
        while (r.Read()) buckets.Add(new BucketState(r.GetInt32(0), r.GetDouble(1), r.GetInt32(2)));
        return new CalibrationState(buckets);
    }

    /// <summary>Forgets everything learned for this hardware set.</summary>
    public void Clear(string inventoryHash)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM calibration WHERE inventory_hash = $hash";
        Rows.Add(cmd, "$hash", inventoryHash);
        cmd.ExecuteNonQuery();
    }
}
