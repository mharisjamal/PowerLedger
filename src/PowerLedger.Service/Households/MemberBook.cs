using PowerLedger.Storage;

namespace PowerLedger.Service.Households;

/// <summary>A member's place in the household's epochs (plan 0.9), which order membership where clocks can't.</summary>
/// <param name="Added">The household epoch the PC that added it was at.</param>
/// <param name="Removed">The epoch the PC that removed it was at; null while it never was.</param>
internal sealed record MemberEpochs(int Added, int? Removed = null)
{
    /// <summary>Never removed, or added again after its removal.</summary>
    public bool Current => Removed is not { } removed || Added > removed;

    /// <summary>Two accounts of one member together: the higher value of each field.</summary>
    public MemberEpochs Merge(MemberEpochs other) =>
        new(Math.Max(Added, other.Added), Removed is { } mine && other.Removed is { } theirs ? Math.Max(mine, theirs) : Removed ?? other.Removed);
}

/// <summary>A current member whose batches still come under an older epoch than this PC's (plan 0.8).</summary>
/// <param name="Since">When this PC first saw it behind, unix milliseconds.</param>
/// <param name="RotatedAt">The epoch this PC made a new key at so it would have one; null before.</param>
internal sealed record Lag(long Since, int? RotatedAt = null);

/// <summary>What learning a member list changed here.</summary>
/// <param name="Removed">Members this PC had as current that the list says were removed: each means a new key.</param>
/// <param name="Added">Members this PC didn't know, or knew as removed and were added again since.</param>
internal sealed record Learned(IReadOnlyList<string> Removed, IReadOnlyList<string> Added);

/// <summary>
/// The household's members as this PC knows them (households design §1, plan 0.9). Membership is ordered by epochs, not
/// clocks: each member carries the epoch the PC that added it was at, and once removed the epoch the PC that removed it
/// was at. It is current while never removed, or added again at a higher epoch than its removal. Two accounts of a member
/// merge by taking the higher of each, so a removal stands against every add as old as it, and only a newer add, which
/// the adding PC makes above the removal, brings a PC back: by pairing or approval, never by gossip. The times kept with
/// them are for display only. A removed PC's epochs outlive its rows while this PC is in the household, so no list brings
/// it back; each change of membership is also kept in the member's row, which is left while the PC isn't current.
/// </summary>
internal sealed class MemberBook(HouseholdStore store, HouseholdRepository household)
{
    /// <summary>At most this many removed PCs are kept without their rows, those removed earliest going first.</summary>
    public const int MaxRemoved = 64;

    /// <summary>A member's epochs as this PC knows them; null for a PC it never knew. A member kept before it had any counts as
    /// added at epoch 0, and removed then if it has left.</summary>
    public MemberEpochs? EpochsOf(string id) =>
        store.MemberEpochs.TryGetValue(id, out var epochs) ? epochs
        : household.Member(id) is { } member ? new MemberEpochs(0, member.LeftMs is null ? null : 0)
        : null;

    /// <summary>The member of that ID while it is current; null when it is unknown or was removed.</summary>
    public HouseholdMember? Current(string id) => household.Member(id) is { } member && EpochsOf(id) is { Current: true } ? member : null;

    /// <summary>A PC this PC's user adds, by pairing or approval, at <paramref name="epoch"/>, this PC's own (plan 0.9). It must
    /// be above any removal of the PC known here: a PC removed at this PC's epoch comes back only once the key has moved on.</summary>
    /// <returns>False when it was removed at that epoch or later, and so isn't added.</returns>
    public bool Add(MemberInfo member, int epoch, long nowMs)
    {
        var known = EpochsOf(member.Id);
        if (known?.Removed is { } removed && epoch <= removed) return false;
        household.SaveMember(new HouseholdMember(member.Id, member.Name, member.Kind, member.Sign, member.Dh, nowMs, null, null));
        Set(member.Id, (known ?? new MemberEpochs(epoch)).Merge(new MemberEpochs(epoch)));
        store.SnapshotWanted = true;                                           // the new member reads the history (plan 0.9)
        return true;
    }

    /// <summary>This PC's own place in the household it enters: added at the epoch it entered at.</summary>
    public void Self(string id, int epoch) => Set(id, new MemberEpochs(epoch));

    /// <summary>This PC removes a member: removed at its own epoch, or at the member's add if that is higher, so the removal
    /// stands (plan 0.9). The new key follows.</summary>
    /// <returns>True when it was current until now.</returns>
    public bool Remove(string id, long nowMs)
    {
        var known = EpochsOf(id) ?? new MemberEpochs(0);
        Set(id, known.Merge(new MemberEpochs(0, Math.Max(store.Epoch, known.Added))));
        household.MarkLeft(id, nowMs);
        return known.Current;
    }

    /// <summary>
    /// The server lists a member as removed (plan 0.9): removed at the higher of its add and this PC's epoch, unless a higher
    /// removal is known. The server's word only ever removes. A removal the server made before an add known here, at an
    /// epoch this PC can place, is an old one, and changes nothing; a PC this PC never knew is kept as removed at the
    /// server's epoch, so no list that hasn't heard brings it in.
    /// </summary>
    /// <param name="serverEpoch">The household's epoch at the removal, as the server gives it.</param>
    /// <param name="serverAdded">The household's epoch when the server added it.</param>
    /// <returns>True when it was current until now.</returns>
    public bool RemovedByServer(string id, long removedMs, int? serverEpoch, int? serverAdded = null)
    {
        var placed = serverEpoch is { } at && at >= 0 && at <= store.Epoch + 1 ? at : (int?)null;
        if (EpochsOf(id) is not { } known)
        {
            var removedAt = placed ?? store.Epoch;
            if (store.HouseholdId is not null && Wire.IsDeviceId(id)) Set(id, new MemberEpochs(Math.Clamp(serverAdded ?? 0, 0, removedAt), removedAt));
            return false;
        }
        if (!known.Current || (placed is { } before && known.Added > before)) return false;   // removed here already, or an old removal
        Set(id, known.Merge(new MemberEpochs(0, Math.Max(Math.Max(known.Added, store.Epoch), placed ?? 0))));
        household.MarkLeft(id, removedMs);
        return true;
    }

    /// <summary>Takes a PC's rows off this PC, and its entry off the Household page; its epochs stay while this PC is in the
    /// household, so it is never taken back on another PC's word.</summary>
    public void ForgetRows(string id)
    {
        if (store.HouseholdId is not null && EpochsOf(id) is { } epochs) Set(id, epochs);
        household.DeleteRows(id);
        household.DeleteMember(id);
    }

    /// <summary>True when <paramref name="sealerId"/> may hand this PC the key for <paramref name="epoch"/> (plan 0.9): it is
    /// current here, and was added before that epoch. There is no other allowance: a removed PC holds every key it sealed.</summary>
    public bool MaySeal(string sealerId, int epoch) => Current(sealerId) is not null && EpochsOf(sealerId)!.Added < epoch;

    /// <summary>True when the rows of a batch from <paramref name="senderId"/> under <paramref name="epoch"/> may be kept: it is
    /// current, or the batch's epoch is no later than its removal (plan 0.9).</summary>
    public bool MayHavePosted(string senderId, int epoch) => EpochsOf(senderId) is { } epochs && (epochs.Current || epoch <= epochs.Removed);

    /// <summary>The members this PC knows, as a welcome, an approval, a recovery, a <c>have</c> and a batch carry them, each
    /// with its epochs: every current one with its keys, name and kind, then at most <see cref="MaxRemoved"/> removed ones,
    /// the most recently removed first, by ID. With <paramref name="compact"/>, as an approval's or a recovery's sealed body
    /// carries them, a removed one has its epochs alone.</summary>
    public List<WireMember> Entries(bool compact = false)
    {
        var current = new List<WireMember>();
        var removed = new List<WireMember>();
        var known = household.Members();
        foreach (var member in known)
        {
            var epochs = EpochsOf(member.DeviceId)!;
            if (epochs.Current) current.Add(Wire.Member(member) with { Added = member.AddedMs, AddedEpoch = epochs.Added });
            else removed.Add(new WireMember(member.DeviceId, null, null, Removed: compact ? null : member.LeftMs, AddedEpoch: epochs.Added, RemovedEpoch: epochs.Removed));
        }
        foreach (var (id, epochs) in store.MemberEpochs.Where(pair => !pair.Value.Current && known.All(member => member.DeviceId != pair.Key)))
        {
            removed.Add(new WireMember(id, null, null, AddedEpoch: epochs.Added, RemovedEpoch: epochs.Removed));
        }
        return [.. current, .. removed.OrderByDescending(entry => entry.RemovedEpoch).Take(MaxRemoved)];
    }

    /// <summary>
    /// Learns from a member list this PC may take it from (plan 0.9): a welcome's, an approval's or a recovery's, a batch's
    /// whose sender is current here, or a current member's on the network. Each entry merges with what is known here, and
    /// one with an epoch above this PC's own + 1 is passed over; a PC this PC didn't know comes in only with its keys, and
    /// the list's own PC gives its name and kind. Nobody's list changes this PC's own entry, or a member's keys.
    /// </summary>
    /// <param name="fromId">The PC whose list it is.</param>
    /// <param name="selfId">This PC.</param>
    public Learned Learn(IEnumerable<WireMember> entries, string? fromId, string selfId, long nowMs)
    {
        List<string> removed = [];
        List<string> added = [];
        var ceiling = store.Epoch + 1;
        foreach (var entry in entries.Take(MaxRemoved + Wire.MaxMembers))
        {
            if (entry.Id == selfId || !Wire.IsDeviceId(entry.Id)) continue;
            var incoming = new MemberEpochs(entry.AddedEpoch ?? 0, entry.RemovedEpoch);
            if (incoming.Added is < 0 || incoming.Added > ceiling || incoming.Removed is < 0 || incoming.Removed > ceiling) continue;
            var before = EpochsOf(entry.Id);
            var merged = before?.Merge(incoming) ?? incoming;
            var row = household.Member(entry.Id);
            if (merged.Current && (row is null || before is not { Current: true }))
            {
                if (row is null)
                {
                    if (Wire.Member(entry) is not { } member) continue;             // current, but without keys to know it by
                    household.SaveMember(new HouseholdMember(member.Id, member.Name, member.Kind, member.Sign, member.Dh, entry.Added ?? nowMs, null, null));
                }
                else
                {
                    household.SaveMember(row with { LeftMs = null });
                }
                added.Add(entry.Id);
            }
            else if (!merged.Current && before is { Current: true })
            {
                household.MarkLeft(entry.Id, entry.Removed is > 0 and var at ? at : nowMs);
                removed.Add(entry.Id);
            }
            if (merged != before) Set(entry.Id, merged);
            if (entry.Id == fromId && merged.Current && household.Member(entry.Id) is { } sender && Wire.Member(entry) is { } own
                && sender.SignKey.AsSpan().SequenceEqual(own.Sign) && sender.DhKey.AsSpan().SequenceEqual(own.Dh) && (sender.Name != own.Name || sender.Kind != own.Kind))
            {
                household.SaveMember(sender with { Name = own.Name, Kind = own.Kind });
            }
        }
        if (added.Count > 0) store.SnapshotWanted = true;
        return new Learned(removed, added);
    }

    private void Set(string id, MemberEpochs epochs)
    {
        var all = new Dictionary<string, MemberEpochs>(store.MemberEpochs, StringComparer.Ordinal) { [id] = epochs };
        var rowless = all.Where(pair => !pair.Value.Current && household.Member(pair.Key) is null).ToList();
        foreach (var (earliest, _) in rowless.OrderBy(pair => pair.Value.Removed).ThenBy(pair => pair.Value.Added).Take(Math.Max(0, rowless.Count - MaxRemoved)))
        {
            all.Remove(earliest);
        }
        store.MemberEpochs = all;
    }
}
