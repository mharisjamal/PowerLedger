using PowerLedger.Core.Households;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;

namespace PowerLedger.Service.Households;

/// <summary>A PC as the server's member list last gave it (plan 0.10): the household's epoch when the server added it, and the
/// epoch and time it removed it at, if it did.</summary>
/// <param name="Unknown">When this PC first saw it listed as current with no introduction, unix milliseconds.</param>
internal sealed record ServerEntry(int Added, int? Removed = null, long? RemovedMs = null, long? Unknown = null)
{
    public bool Current => Removed is null;
}

/// <summary>A current member whose batches still come under an older epoch than this PC's (plan 0.8).</summary>
/// <param name="Since">When this PC first saw it behind, unix milliseconds.</param>
/// <param name="RotatedAt">The epoch this PC made a new key at so it would have one; null before.</param>
internal sealed record Lag(long Since, int? RotatedAt = null);

/// <summary>What learning a member list changed here.</summary>
/// <param name="Removed">PCs shown as in until now that are shown as left from now on, so no longer synced with on the network.</param>
/// <param name="Added">PCs the list introduced.</param>
internal sealed record Learned(IReadOnlyList<string> Removed, IReadOnlyList<string> Added);

/// <summary>What reading the server's member list changed here.</summary>
/// <param name="Gone">PCs current here until now that the server has removed: each means a new key.</param>
/// <param name="Left">PCs shown as in until now that are shown as left from now on.</param>
/// <param name="Unknown">PCs the server has listed as current for <see cref="MemberBook.IntroductionWait"/> or more that no
/// introduction has reached: each is to be taken out.</param>
internal sealed record ServerLearned(IReadOnlyList<string> Gone, IReadOnlyList<string> Left, IReadOnlyList<string> Unknown);

/// <summary>
/// The household's members as this PC knows them (households design §6, plan 0.10). The server's member list says who is in,
/// and introductions say whose keys to trust: this PC's own pairings and approvals, the member list in its welcome, approval
/// or recovery, and the sealed lists of PCs current here. A PC is current here when both agree: the server's latest list has
/// it as current, or it is this PC's own add still waiting for the server; and its keys, kept with its row, came in an
/// introduction. Before this PC has read the server's list for its household, the introductions alone decide. A list's
/// entry about its own sender counts for nothing, and nobody's list says anything about this PC. Removals in lists, this
/// PC's own among them, only stop sync on the network with that PC and show it as left: they never override the server's
/// list. The server's list also gives the epochs, which decide who may hand this PC a key.
/// </summary>
internal sealed class MemberBook(HouseholdStore store, HouseholdRepository household)
{
    /// <summary>At most this many removals are kept from each list, the latest first.</summary>
    public const int MaxRemoved = 64;

    /// <summary>How long a PC the server lists as current may go without an introduction before a member takes it out.</summary>
    public static readonly TimeSpan IntroductionWait = TimeSpan.FromDays(3);

    /// <summary>Whose removals are this PC's own, among the lists' removals kept.</summary>
    internal const string Own = "own";

    /// <summary>The member of that ID while it is current here; null when it isn't.</summary>
    public HouseholdMember? Current(string id)
    {
        var view = View();
        return view.Row(id) is { } row && view.IsCurrent(id) ? row : null;
    }

    /// <summary>True when the server's latest list has the PC as current, or it is this PC's own add waiting for the server.</summary>
    public bool ServerCurrent(string id) => View().ServerCurrent(id);

    /// <summary>True when a current member's list, this PC's own removals among them, has the PC as removed since the server
    /// last added it.</summary>
    public bool ShownRemoved(string id) => View().ShownRemoved(id);

    /// <summary>True when this PC may sync with the PC on the network (plan 0.10): it is current here and no current member's
    /// list has it as removed. It is shown as in on the Household page just when this is true.</summary>
    public bool MaySync(string id) => View().MaySync(id);

    /// <summary>The PC as the server's list last gave it; null when that list didn't have it, or none was read.</summary>
    public ServerEntry? ServerEntryOf(string id) => store.ServerMembers?.GetValueOrDefault(id);

    /// <summary>The highest epoch the server's list shows, at an add or a removal; 0 when none was read.</summary>
    public int ServerEpoch => store.ServerMembers?.Values.Select(entry => Math.Max(entry.Added, entry.Removed ?? 0)).DefaultIfEmpty().Max() ?? 0;

    /// <summary>
    /// True when <paramref name="sealerId"/> may hand this PC the key of <paramref name="epoch"/> (plan 0.10): its keys were
    /// introduced here, and by the server's list it was added before that epoch and not removed before it. So a PC that
    /// removed another and then left is still the sealer of the key it made.
    /// </summary>
    public bool MaySeal(string sealerId, int epoch) =>
        household.Member(sealerId) is not null && ServerEntryOf(sealerId) is { } entry && entry.Added < epoch && (entry.Removed is not { } removed || removed >= epoch);

    /// <summary>True when the rows of a batch from <paramref name="senderId"/> under <paramref name="epoch"/> may be kept: it is
    /// current here, or the server removed it at that epoch or later.</summary>
    public bool MayHavePosted(string senderId, int epoch) =>
        Current(senderId) is not null || (household.Member(senderId) is not null && ServerEntryOf(senderId) is { Removed: { } removed } && epoch <= removed);

    /// <summary>
    /// Takes the server's member list (plan 0.10): who is current, and each PC's epochs. A PC listed as current that no
    /// introduction has reached is noted, and after <see cref="IntroductionWait"/> it is to be taken out.
    /// </summary>
    public ServerLearned TakeServerList(IEnumerable<ServerMember> members, string selfId, long nowMs)
    {
        if (store.HouseholdId is null) return new ServerLearned([], [], []);
        var before = View();
        var wasCurrent = before.Rows.Select(row => row.DeviceId).Where(id => id != selfId && before.IsCurrent(id)).ToList();
        var taken = new Dictionary<string, ServerEntry>(StringComparer.Ordinal);
        List<string> unknown = [];
        foreach (var member in members.Where(member => Wire.IsDeviceId(member.Device)))
        {
            int? removed = member.Removed is null ? null : member.RemovedEpoch ?? member.AddedEpoch;
            long? since = null;
            if (removed is null && member.Device != selfId && before.Row(member.Device) is null && !before.Adds.Contains(member.Device))
            {
                since = before.Server?.GetValueOrDefault(member.Device)?.Unknown ?? nowMs;
                if (nowMs - since >= (long)IntroductionWait.TotalMilliseconds) unknown.Add(member.Device);
            }
            taken[member.Device] = new ServerEntry(member.AddedEpoch, removed, member.Removed, since);
        }
        store.ServerMembers = taken;
        var after = View();
        var gone = wasCurrent.Where(id => !after.ServerCurrent(id) && taken.ContainsKey(id)).ToList();
        return new ServerLearned(gone, Refresh(nowMs), unknown);
    }

    /// <summary>The server took this PC's add or approval of the PC (plan 0.10): current from now on, as the next list says.</summary>
    public void ServerAdded(string id, int epoch)
    {
        if (store.ServerMembers is not { } server) return;
        store.ServerMembers = new Dictionary<string, ServerEntry>(server, StringComparer.Ordinal) { [id] = new ServerEntry(epoch) };
    }

    /// <summary>The server took a removal this PC made (plan 0.10): removed at this PC's epoch at the least, as the next list says.</summary>
    public void ServerRemoved(string id, int epoch, long nowMs)
    {
        if (store.ServerMembers is not { } server) return;
        var added = server.GetValueOrDefault(id)?.Added ?? 0;
        store.ServerMembers = new Dictionary<string, ServerEntry>(server, StringComparer.Ordinal) { [id] = new ServerEntry(added, Math.Max(epoch, added), nowMs) };
        Refresh(nowMs);
    }

    /// <summary>This PC recovered the household (plan 0.9): the server removed every other member at <paramref name="epoch"/>.</summary>
    public void RemoveAllBut(string selfId, int epoch, long nowMs)
    {
        var view = View();
        var server = new Dictionary<string, ServerEntry>(view.Server ?? new Dictionary<string, ServerEntry>(), StringComparer.Ordinal);
        foreach (var id in view.Rows.Select(row => row.DeviceId).Concat(server.Keys).Distinct(StringComparer.Ordinal).Where(id => id != selfId).ToList())
        {
            if (server.GetValueOrDefault(id) is not { Current: false }) server[id] = new ServerEntry(server.GetValueOrDefault(id)?.Added ?? 0, epoch, nowMs);
        }
        server[selfId] = server.GetValueOrDefault(selfId) is { Current: true } self ? self : new ServerEntry(epoch);
        store.ServerMembers = server;
        Refresh(nowMs);
    }

    /// <summary>A PC this PC's own pairing or approval brings in: its keys are introduced here, and any removal of it this PC
    /// made is forgotten. Its add goes to the server first, so it is current.</summary>
    public void Introduce(MemberInfo member, long nowMs)
    {
        household.SaveMember(new HouseholdMember(member.Id, member.Name, member.Kind, member.Sign, member.Dh, nowMs, null, null));
        var own = new Dictionary<string, int>(Claims(Own), StringComparer.Ordinal);
        if (own.Remove(member.Id)) SetClaims(Own, own);
        store.SnapshotWanted = true;                                           // the new member reads the history (plan 0.9)
        Refresh(nowMs);
    }

    /// <summary>This PC's user removes a member (plan 0.10): sync on the network with it stops at once, and it shows as left.
    /// The removal goes to the server, which decides the rest.</summary>
    /// <returns>True when it was shown as in until now.</returns>
    public bool Remove(string id, long nowMs)
    {
        var view = View();
        var wasIn = view.MaySync(id);
        var own = new Dictionary<string, int>(Claims(Own), StringComparer.Ordinal)
        {
            [id] = Math.Max(store.Epoch, view.Server?.GetValueOrDefault(id)?.Added ?? 0),
        };
        SetClaims(Own, own);
        Refresh(nowMs);
        return wasIn;
    }

    /// <summary>Takes a PC's rows off this PC, and its entry off the Household page. What the server's list and the lists'
    /// removals say of it stays, so no list brings it back.</summary>
    public void ForgetRows(string id)
    {
        household.DeleteRows(id);
        household.DeleteMember(id);
    }

    /// <summary>The members this PC knows, as a welcome, an approval, a recovery, a <c>have</c> and a batch carry them: each PC
    /// shown as in with its keys, name and kind, and the epoch the server added it at; then at most <see cref="MaxRemoved"/>
    /// PCs shown as removed, the latest removed first, by ID with their epochs. With <paramref name="compact"/>, as an
    /// approval's or a recovery's sealed body carries them, a removed one has its epochs alone.</summary>
    public List<WireMember> Entries(bool compact = false)
    {
        var view = View();
        var current = new List<WireMember>();
        var removed = new List<WireMember>();
        foreach (var row in view.Rows.Where(row => view.MaySync(row.DeviceId)))
        {
            current.Add(Wire.Member(row) with { Added = row.AddedMs, AddedEpoch = view.Server?.GetValueOrDefault(row.DeviceId)?.Added });
        }
        var known = view.Rows.Select(row => row.DeviceId).Concat(view.Server?.Keys ?? []).Concat(view.Claims.Values.SelectMany(claims => claims.Keys));
        foreach (var id in known.Distinct(StringComparer.Ordinal).Where(id => current.All(entry => entry.Id != id)))
        {
            var entry = view.Server?.GetValueOrDefault(id);
            if ((entry?.Removed ?? view.RemovedAt(id)) is not { } at) continue;
            removed.Add(new WireMember(id, null, null, Removed: compact ? null : entry?.RemovedMs ?? view.Row(id)?.LeftMs, AddedEpoch: entry?.Added, RemovedEpoch: at));
        }
        return [.. current, .. removed.OrderByDescending(entry => entry.RemovedEpoch).Take(MaxRemoved)];
    }

    /// <summary>
    /// Learns from a member list (plan 0.10): a welcome's, an approval's or a recovery's, or a PC's current here. A PC this
    /// PC doesn't know comes in with its keys, unless this PC or the server removed it; a member's keys never change. The
    /// list's removals are kept as its PC's, in place of those its last list had, and count while that PC is current here.
    /// The list's entry about its own PC gives its name and kind, and nothing else; nobody's list says anything about this PC.
    /// </summary>
    /// <param name="fromId">The PC whose list it is; null for a recovery's, which the code vouches for as a whole.</param>
    /// <param name="selfId">This PC.</param>
    public Learned Learn(IEnumerable<WireMember> entries, string? fromId, string selfId, long nowMs)
    {
        if (store.HouseholdId is null) return new Learned([], []);
        var list = entries.Take(MaxRemoved + Wire.MaxMembers).Where(entry => Wire.IsDeviceId(entry.Id) && entry.Id != selfId).ToList();
        if (fromId is not null)
        {
            SetClaims(fromId, list.Where(entry => entry.Id != fromId && entry.RemovedEpoch is >= 0)
                .GroupBy(entry => entry.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Max(entry => entry.RemovedEpoch!.Value), StringComparer.Ordinal));
        }
        var view = View();
        List<string> added = [];
        foreach (var entry in list.Where(entry => entry.Id != fromId && entry.RemovedEpoch is null && entry.Removed is null))
        {
            if (view.Row(entry.Id) is not null || Wire.Member(entry) is not { } member) continue;   // a member's keys never change
            if (view.Server?.GetValueOrDefault(entry.Id) is { Current: false } || view.Claims.GetValueOrDefault(Own)?.ContainsKey(entry.Id) == true)
            {
                continue;                                                      // removed here: not brought back on a list's word
            }
            household.SaveMember(new HouseholdMember(member.Id, member.Name, member.Kind, member.Sign, member.Dh, entry.Added ?? nowMs, null, null));
            added.Add(entry.Id);
        }
        if (fromId is not null && list.FirstOrDefault(entry => entry.Id == fromId && entry.RemovedEpoch is null) is { } own && Wire.Member(own) is { } ownInfo
            && household.Member(fromId) is { } sender && sender.SignKey.AsSpan().SequenceEqual(ownInfo.Sign) && sender.DhKey.AsSpan().SequenceEqual(ownInfo.Dh)
            && (sender.Name != ownInfo.Name || sender.Kind != ownInfo.Kind))
        {
            household.SaveMember(sender with { Name = ownInfo.Name, Kind = ownInfo.Kind });   // for the Household page only
        }
        if (added.Count > 0) store.SnapshotWanted = true;
        return new Learned(Refresh(nowMs), added);
    }

    /// <summary>Marks each member as left, or as back, as it is shown now.</summary>
    /// <returns>The PCs shown as in until now that are shown as left from now on.</returns>
    private List<string> Refresh(long nowMs)
    {
        if (store.HouseholdId is null) return [];
        var view = View();
        List<string> left = [];
        foreach (var row in view.Rows)
        {
            var shownIn = view.MaySync(row.DeviceId);
            if (shownIn && row.LeftMs is not null)
            {
                household.SaveMember(row with { LeftMs = null });
                store.SnapshotWanted = true;
            }
            else if (!shownIn && row.LeftMs is null)
            {
                household.MarkLeft(row.DeviceId, view.Server?.GetValueOrDefault(row.DeviceId)?.RemovedMs ?? nowMs);
                left.Add(row.DeviceId);
            }
        }
        return left;
    }

    private IReadOnlyDictionary<string, int> Claims(string claimant) =>
        store.RemovalClaims.TryGetValue(claimant, out var claims) ? claims : new Dictionary<string, int>();

    /// <summary>Keeps <paramref name="claimant"/>'s removals in place of those it had, at most <see cref="MaxRemoved"/>, the
    /// latest; those of PCs whose rows went are dropped.</summary>
    private void SetClaims(string claimant, IReadOnlyDictionary<string, int> removals)
    {
        var all = store.RemovalClaims
            .Where(pair => pair.Key != claimant && (pair.Key == Own || household.Member(pair.Key) is not null))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (removals.Count > 0)
        {
            all[claimant] = removals.OrderByDescending(pair => pair.Value).Take(MaxRemoved).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        store.RemovalClaims = all;
    }

    /// <summary>This PC's own adds the server hasn't taken yet, by device ID.</summary>
    private HashSet<string> AddsWaiting() => store.HouseholdId is not { } householdId
        ? []
        : [.. store.Pending.Where(op => op.Kind == PendingOp.Add && op.Household == householdId).Select(op => Wire.PublicKey(op.Sign)).OfType<byte[]>()
            .Select(HouseholdCrypto.DeviceIdOf)];

    private Snapshot View() => new(
        store.HouseholdId is null ? [] : household.Members(), store.ServerMembers, store.RemovalClaims, AddsWaiting());

    /// <summary>What decides membership, read once for one question.</summary>
    private sealed class Snapshot(
        IReadOnlyList<HouseholdMember> rows, IReadOnlyDictionary<string, ServerEntry>? server,
        IReadOnlyDictionary<string, Dictionary<string, int>> claims, HashSet<string> adds)
    {
        public IReadOnlyList<HouseholdMember> Rows => rows;

        public IReadOnlyDictionary<string, ServerEntry>? Server => server;

        public IReadOnlyDictionary<string, Dictionary<string, int>> Claims => claims;

        public HashSet<string> Adds => adds;

        public HouseholdMember? Row(string id) => rows.FirstOrDefault(row => row.DeviceId == id);

        public bool ServerCurrent(string id) => adds.Contains(id) || server?.GetValueOrDefault(id) is { Current: true };

        /// <summary>Current here: introduced, and current by the server's list; before any list, not shown as removed.</summary>
        public bool IsCurrent(string id) => Row(id) is not null && (server is null ? !ShownRemoved(id) : ServerCurrent(id));

        public bool MaySync(string id) => IsCurrent(id) && !ShownRemoved(id);

        public bool ShownRemoved(string id) => RemovedAt(id) is not null;

        /// <summary>The latest epoch a removal of the PC that counts was made at: one by this PC, or in the list of a PC current
        /// here, made no earlier than the server's latest add of it. Null when there is none.</summary>
        public int? RemovedAt(string id)
        {
            int? at = null;
            foreach (var (claimant, removals) in claims)
            {
                if (claimant == id || !removals.TryGetValue(id, out var epoch)) continue;
                if (server?.GetValueOrDefault(id) is { } entry && epoch < entry.Added) continue;   // an older removal: added again since
                if (!Counts(claimant)) continue;
                at = Math.Max(at ?? epoch, epoch);
            }
            return at;
        }

        /// <summary>This PC's own removals count; another PC's while it is introduced and current by the server's list, or before
        /// any list while this PC hasn't removed it.</summary>
        private bool Counts(string claimant) =>
            claimant == Own
            || (Row(claimant) is not null && (server is null ? claims.GetValueOrDefault(Own)?.ContainsKey(claimant) != true : ServerCurrent(claimant)));
    }
}
