using PowerLedger.Storage;

namespace PowerLedger.Service.Households;

/// <summary>A PC removed from the household, as this PC keeps it (plan 0.8).</summary>
/// <param name="Removed">When it was removed, unix milliseconds.</param>
/// <param name="LastEpoch">The newest epoch it can have sealed a key for: the one after this PC's when it heard of the
/// removal, or earlier once this PC's own new key without it was taken.</param>
internal sealed record Tombstone(long Removed, int LastEpoch);

/// <summary>A current member whose batches still come under an older epoch than this PC's (plan 0.8).</summary>
/// <param name="Since">When this PC first saw it behind, unix milliseconds.</param>
/// <param name="RotatedAt">The epoch this PC made a new key at so it would have one; null before.</param>
internal sealed record Lag(long Since, int? RotatedAt = null);

/// <summary>What learning another PC's member list changed here.</summary>
/// <param name="Removed">Members this PC had as current that the list says were removed: each means a new key.</param>
/// <param name="Added">Members this PC didn't know, or knew as removed and were added again since.</param>
internal sealed record Learned(IReadOnlyList<string> Removed, IReadOnlyList<string> Added);

/// <summary>
/// The household's members as this PC knows them (households design §1, plan 0.8): each current one, and a tombstone for
/// each that was removed, with the time of its removal, kept even once the user removes its rows. A removed PC is never
/// taken back on the word of a PC that hasn't heard of its removal: only an add later than the removal brings it back.
/// Another member's list, from a PC that proved itself on the network or in a signed batch, teaches the members added and
/// removed elsewhere; nobody's list changes this PC's own entry, a member's keys, or another member's name.
/// </summary>
internal sealed class MemberBook(HouseholdStore store, HouseholdRepository household)
{
    /// <summary>At most this many tombstones are kept, the oldest going first.</summary>
    public const int MaxTombstones = 64;

    /// <summary>The member of that ID while it is current; null when it is unknown or was removed.</summary>
    public HouseholdMember? Current(string id) => household.Member(id) is { LeftMs: null } member ? member : null;

    /// <summary>When the PC of that ID was removed, as this PC knows it; null for a current or unknown one.</summary>
    public long? RemovedAt(string id) =>
        store.Tombstones.TryGetValue(id, out var tombstone) ? tombstone.Removed : household.Member(id)?.LeftMs;

    /// <summary>Marks a member as removed at <paramref name="removedMs"/> and keeps its tombstone.</summary>
    /// <returns>True when it was current until now.</returns>
    public bool Remove(string id, long removedMs)
    {
        var wasCurrent = household.Member(id) is { LeftMs: null };
        household.MarkLeft(id, removedMs);
        Bury(id, removedMs);
        return wasCurrent;
    }

    /// <summary>Takes a removed member's rows off this PC, and its entry off the Household page; its tombstone stays while
    /// this PC is in the household.</summary>
    public void ForgetRows(string id)
    {
        if (store.HouseholdId is not null && household.Member(id)?.LeftMs is { } left) Bury(id, left);
        household.DeleteRows(id);
        household.DeleteMember(id);
    }

    /// <summary>True when a key for <paramref name="epoch"/> may have come from <paramref name="sealer"/>: it is current, or
    /// it was removed and the epoch is no newer than the last it can have sealed (plan 0.8).</summary>
    public bool MayHaveSealed(HouseholdMember sealer, int epoch) =>
        sealer.LeftMs is null || (store.Tombstones.TryGetValue(sealer.DeviceId, out var tombstone) && epoch <= tombstone.LastEpoch);

    /// <summary>This PC's own new key at <paramref name="epoch"/>, sealed without the members removed, was taken: none of them
    /// can have sealed that epoch or any after it.</summary>
    public void Excluded(int epoch)
    {
        var stones = new Dictionary<string, Tombstone>(store.Tombstones, StringComparer.Ordinal);
        foreach (var (id, tombstone) in stones.Where(pair => pair.Value.LastEpoch >= epoch).ToList())
        {
            stones[id] = tombstone with { LastEpoch = epoch - 1 };
        }
        store.Tombstones = stones;
    }

    /// <summary>A removed PC this PC's user added again: its tombstone goes.</summary>
    public void Restore(string id)
    {
        if (!store.Tombstones.ContainsKey(id)) return;
        var stones = new Dictionary<string, Tombstone>(store.Tombstones, StringComparer.Ordinal);
        stones.Remove(id);
        store.Tombstones = stones;
    }

    /// <summary>The members this PC knows, as <c>have</c> and a batch carry them: each current one with its keys and when it
    /// was added, and each removed one with when it was removed.</summary>
    public List<WireMember> Entries()
    {
        var entries = new List<WireMember>();
        var known = household.Members();
        foreach (var member in known)
        {
            entries.Add(member.LeftMs is { } left
                ? new WireMember(member.DeviceId, null, null, Removed: RemovedAt(member.DeviceId) ?? left)
                : Wire.Member(member) with { Added = member.AddedMs });
        }
        foreach (var (id, tombstone) in store.Tombstones.Where(pair => known.All(member => member.DeviceId != pair.Key)))
        {
            entries.Add(new WireMember(id, null, null, Removed: tombstone.Removed));
        }
        return entries;
    }

    /// <summary>Learns from another member's list: members removed elsewhere, members added elsewhere, and the sender's own
    /// name and kind.</summary>
    /// <param name="entries">The list, from a PC that proved itself or signed it.</param>
    /// <param name="fromId">The PC it came from.</param>
    /// <param name="selfId">This PC, which nobody's list changes.</param>
    public Learned Learn(IEnumerable<WireMember> entries, string fromId, string selfId, long nowMs)
    {
        List<string> removed = [];
        List<string> added = [];
        foreach (var entry in entries.Take(MaxTombstones + Wire.MaxMembers))
        {
            if (entry.Id == selfId || !Wire.IsDeviceId(entry.Id)) continue;
            if (entry.Removed is { } removedAt)
            {
                if (entry.Id == fromId) continue;                                   // a PC doesn't remove itself in its own list
                var known = household.Member(entry.Id);
                if (known is { LeftMs: null } && known.AddedMs <= removedAt)                // a tie counts as removed
                {
                    Remove(entry.Id, removedAt);
                    removed.Add(entry.Id);
                }
                else if (known is null && !store.Tombstones.ContainsKey(entry.Id))
                {
                    Bury(entry.Id, removedAt);                                     // never taken in later from a PC that hasn't heard
                }
                continue;
            }
            if (Wire.Member(entry) is not { } member) continue;
            var current = household.Member(member.Id);
            if (current is null && RemovedAt(member.Id) is null)
            {
                household.SaveMember(new HouseholdMember(member.Id, member.Name, member.Kind, member.Sign, member.Dh, entry.Added ?? nowMs, null, null));
                added.Add(member.Id);
            }
            else if (current is not { LeftMs: null } && RemovedAt(member.Id) is { } gone && entry.Added is { } addedAt && addedAt > gone
                && (current is null || SameKeys(current, member)))
            {
                Restore(member.Id);                                                 // added again after its removal
                household.SaveMember(new HouseholdMember(member.Id, member.Name, member.Kind, member.Sign, member.Dh, addedAt, null, null));
                added.Add(member.Id);
            }
            else if (member.Id == fromId && current is { LeftMs: null } && SameKeys(current, member) && (current.Name != member.Name || current.Kind != member.Kind))
            {
                household.SaveMember(current with { Name = member.Name, Kind = member.Kind });
            }
        }
        return new Learned(removed, added);
    }

    private static bool SameKeys(HouseholdMember known, MemberInfo member) =>
        known.SignKey.AsSpan().SequenceEqual(member.Sign) && known.DhKey.AsSpan().SequenceEqual(member.Dh);

    private void Bury(string id, long removedMs)
    {
        if (store.Tombstones.ContainsKey(id)) return;
        var stones = new Dictionary<string, Tombstone>(store.Tombstones, StringComparer.Ordinal) { [id] = new Tombstone(removedMs, store.Epoch + 1) };
        foreach (var oldest in stones.OrderBy(pair => pair.Value.Removed).Take(Math.Max(0, stones.Count - MaxTombstones)).Select(pair => pair.Key).ToList())
        {
            stones.Remove(oldest);
        }
        store.Tombstones = stones;
    }
}
