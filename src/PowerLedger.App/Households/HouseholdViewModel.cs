using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>One currency's cost for a period, formatted (households design §2): amounts in different currencies are
/// shown side by side and never added together.</summary>
internal sealed record HouseholdCostLine(string Currency, string Cost);

/// <summary>One period's totals, formatted and ready for the page: one energy figure, since a watt-hour needs no
/// currency, and a cost line per currency in play.</summary>
internal sealed record HouseholdPeriod(string Title, string Energy, IReadOnlyList<HouseholdCostLine> Costs);

/// <summary>One member row, ready for the page: its name and kind ("Laptop" or "Desktop"), whether it is this PC, this
/// month's energy for its label, its share of the busiest member's energy for its bar (0 to 1), the status words
/// ("synced 2 minutes ago", "last seen 3 days ago", "left"), and whether it has left (review finding follow-up, task
/// 0.8: a left row offers Remove its rows).</summary>
internal sealed record HouseholdMemberDisplay(string DeviceId, string Name, string Kind, bool IsThisPc, string Energy, double Share, string Status, bool IsLeft)
{
    /// <summary>Another current member: offers Remove.</summary>
    public bool CanRemove => !IsThisPc && !IsLeft;

    /// <summary>A left row still on file: offers Remove its rows (task 0.8's removeOldRows) instead.</summary>
    public bool CanRemoveRows => !IsThisPc && IsLeft;
}

/// <summary>What Remove, Leave or a rows-removal is waiting to be told to go ahead with (households design §2: removing
/// and leaving ask first; task 0.8's removeOldRows the same way).</summary>
internal enum PendingAction
{
    None,
    RemovePc,
    LeaveHousehold,
    RemoveOldRows,
    RemoveAllOldRows,
}

/// <summary>
/// The Household page (households design §2, Plan N task A1): today's, this week's and this month's energy and cost, a
/// bar per PC sized by its share of this month, and each PC's name, kind and sync status. It reads the service's status,
/// for whether a household exists and which PC is this one, and the household's stored rows, when shown and every minute
/// until hidden, off the UI thread. Before a household exists it shows the explanation and Add a PC instead.
/// </summary>
internal sealed class HouseholdViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    public const string Explanation = "Pair this PC with the others in your home to see what all of them use together.";

    private const string CantRead = "History can't be read right now. It comes back when the service is running.";

    private readonly IServiceLink _link;
    private readonly IHouseholdHistory _history;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private ITimer? _timer;
    private int _reads;
    private bool _hasHousehold;
    private HouseholdPeriod _today;
    private HouseholdPeriod _week;
    private HouseholdPeriod _month;
    private IReadOnlyList<HouseholdMemberDisplay> _members = [];
    private string? _message = Explanation;
    private string _nameInput = "";
    private string? _lastServerName;
    private string? _nameMessage;
    private PendingAction _pending = PendingAction.None;
    private HouseholdMemberDisplay? _pendingMember;
    private string? _confirmText;
    private string? _actionMessage;
    private string? _problem;
    private bool _recoveryMissing;
    private string? _recoveryMessage;
    private bool _hasOldRows;

    public HouseholdViewModel(
        IServiceLink link, IHouseholdHistory history, UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture,
        SignInViewModel account)
    {
        _link = link;
        _history = history;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        Account = account;
        _today = Empty("Today");
        _week = Empty("This week");
        _month = Empty("This month");
        AddPc = new RelayCommand(() => AddPcRequested?.Invoke());
        SaveName = new RelayCommand(() => _ = SaveNameAsync(), () => IsValidName(_nameInput));
        AskRemove = new RelayCommand<HouseholdMemberDisplay>(member =>
        {
            if (member is null || member.IsThisPc) return;
            // Task 0.8: a left row's rows can be deleted (removeOldRows); a current member is removed instead.
            if (member.IsLeft) BeginConfirm(PendingAction.RemoveOldRows, member, $"Remove {member.Name}'s rows? This can't be undone.");
            else BeginConfirm(PendingAction.RemovePc, member, $"Remove {member.Name} from your household? It will need to be added again to rejoin.");
        });
        AskLeave = new RelayCommand(() => BeginConfirm(PendingAction.LeaveHousehold, null, "Leave this household? You can join or start another one later."));
        AskRemoveAllOldRows = new RelayCommand(
            () => BeginConfirm(PendingAction.RemoveAllOldRows, null, "Remove the old household's rows? This can't be undone."));
        ConfirmPending = new RelayCommand(() => _ = ConfirmPendingAsync());
        CancelPending = new RelayCommand(EndConfirm);
        MakeRecoveryCode = new RelayCommand(() => _ = MakeRecoveryCodeAsync());
    }

    /// <summary>N2's sign-in section (Plan N tasks A6, A7): works whether or not this PC is in a household.</summary>
    public SignInViewModel Account { get; }

    /// <summary>False before a household exists, or once this PC has left one; the page shows the explanation instead.</summary>
    public bool HasHousehold { get => _hasHousehold; private set => SetProperty(ref _hasHousehold, value); }

    /// <summary>Review finding A11: household_rows or household_members still names this PC although it is in no
    /// household right now — from one it has since left — always false while it is in one. The App never writes these
    /// tables, so this only points the rows out; it offers no way to remove them.</summary>
    public bool HasOldRows { get => _hasOldRows; private set => SetProperty(ref _hasOldRows, value); }

    public HouseholdPeriod Today { get => _today; private set => SetProperty(ref _today, value); }

    public HouseholdPeriod Week { get => _week; private set => SetProperty(ref _week, value); }

    public HouseholdPeriod Month { get => _month; private set => SetProperty(ref _month, value); }

    /// <summary>Every member, in the order the household holds them; each with this month's energy for its bar.</summary>
    public IReadOnlyList<HouseholdMemberDisplay> Members { get => _members; private set => SetProperty(ref _members, value); }

    /// <summary>The explanation before a household exists, or why nothing can be shown; null once totals are showing.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message is not null;

    /// <summary>The last syncing problem, in the service's own words, shown alongside whatever totals it still has; null
    /// while syncing goes well or before a household exists (households design §1).</summary>
    public string? Problem
    {
        get => _problem;
        private set
        {
            if (SetProperty(ref _problem, value)) OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => Problem is not null;

    /// <summary>N2, task 0.8: the account's recovery code no longer works and Make a new recovery code offers a
    /// replacement (review finding A6). The new code itself arrives as its own pushed notice, which App.xaml.cs opens a
    /// window from; this button only starts that off.</summary>
    public bool RecoveryMissing { get => _recoveryMissing; private set => SetProperty(ref _recoveryMissing, value); }

    /// <summary>Why Make a new recovery code didn't go through, or null.</summary>
    public string? RecoveryMessage { get => _recoveryMessage; private set => SetProperty(ref _recoveryMessage, value); }

    public IRelayCommand MakeRecoveryCode { get; }

    /// <summary>Opens Add a PC (Plan N task A2 wires the window up to this).</summary>
    public ICommand AddPc { get; }

    public event Action? AddPcRequested;

    /// <summary>Rename this PC's box: follows the service's name until the user types a different one (households design
    /// §2, 1 to 40 characters).</summary>
    public string NameInput
    {
        get => _nameInput;
        set
        {
            if (SetProperty(ref _nameInput, value)) SaveName.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Why the last rename didn't stick, or null.</summary>
    public string? NameMessage { get => _nameMessage; private set => SetProperty(ref _nameMessage, value); }

    public IRelayCommand SaveName { get; }

    /// <summary>Remove or Leave is waiting on Confirm or Cancel.</summary>
    public bool IsConfirming => _pending != PendingAction.None;

    /// <summary>What Remove or Leave is asking, or null.</summary>
    public string? ConfirmText
    {
        get => _confirmText;
        private set
        {
            if (SetProperty(ref _confirmText, value)) OnPropertyChanged(nameof(IsConfirming));
        }
    }

    /// <summary>Why the last Remove or Leave didn't go through, or null.</summary>
    public string? ActionMessage { get => _actionMessage; private set => SetProperty(ref _actionMessage, value); }

    /// <summary>Asks first: another PC's row offers this, never this PC's own — Remove for a current member, Remove its
    /// rows (task 0.8's removeOldRows) for one that has left.</summary>
    public IRelayCommand<HouseholdMemberDisplay> AskRemove { get; }

    /// <summary>Asks first.</summary>
    public IRelayCommand AskLeave { get; }

    /// <summary>Review finding follow-up, task 0.8: with no household but old rows still on file, removes them all
    /// (removeOldRows with no device named). Asks first.</summary>
    public IRelayCommand AskRemoveAllOldRows { get; }

    public IRelayCommand ConfirmPending { get; }

    public IRelayCommand CancelPending { get; }

    /// <summary>The page is shown: read now, and every minute until it is hidden. Call on the UI thread.</summary>
    public void Show()
    {
        Refresh();
        _timer ??= _clock.CreateTimer(_ => _threads.Post(Refresh), null, RefreshEvery, RefreshEvery);
    }

    public void Hide()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Reads the service's status and, only while in a household, the stored rows, off the UI thread. A read a
    /// newer one overtook is dropped. Call on the UI thread.</summary>
    internal void Refresh()
    {
        var read = ++_reads;
        _threads.Background(() => _ = ReadAsync(read));
    }

    public void Dispose() => Hide();

    private async Task ReadAsync(int read)
    {
        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        var household = status?.Household;
        var now = _clock.GetUtcNow();
        // Review finding A11: read even with no current household, so old rows left behind by one this PC has since
        // left (households design §1: a left member's rows stay until the user removes them) can still be pointed out.
        var snapshot = _history.Read(now, _zone);
        _threads.Post(() =>
        {
            if (read != _reads) return;
            Apply(household, snapshot, now);
        });
    }

    private void Apply(HouseholdStatus? household, HouseholdSnapshot? snapshot, DateTimeOffset now)
    {
        Account.Apply(household);   // sign-in works whether or not this PC is in a household
        RecoveryMissing = household?.RecoveryMissing ?? false;   // task 0.8: can matter with or without a household
        HasHousehold = household?.HouseholdId is not null;
        Problem = HasHousehold ? household!.Problem : null;
        if (!HasHousehold)
        {
            Today = Empty("Today");
            Week = Empty("This week");
            Month = Empty("This month");
            Members = [];
            Message = Explanation;
            // Review finding A11: this PC never writes the database, so there is no Remove button here — only pointing
            // the rows out, which a member row still on file, from a household this PC has since left, is a sign of.
            HasOldRows = snapshot is { Members.Count: > 0 };
            return;
        }
        HasOldRows = false;
        if (snapshot is null)
        {
            Message = CantRead;
            return;
        }
        Today = PeriodOf("Today", snapshot.Today);
        Week = PeriodOf("This week", snapshot.Week);
        Month = PeriodOf("This month", snapshot.Month);
        Members = Rows(household!, snapshot, now);
        Message = null;
        // Follows the service's name, unless the user has typed one it hasn't sent yet.
        if (_lastServerName is null || _nameInput == _lastServerName) NameInput = household!.Name;
        _lastServerName = household!.Name;
    }

    private HouseholdPeriod PeriodOf(string title, HouseholdRangeTotals totals) => new(
        title, Format.Kwh(totals.EnergyKwh, _culture),
        [.. totals.Costs.Select(c => new HouseholdCostLine(c.Currency, Money.Format(c.Cost, c.Currency, _culture)))]);

    private IReadOnlyList<HouseholdMemberDisplay> Rows(HouseholdStatus household, HouseholdSnapshot snapshot, DateTimeOffset now)
    {
        var energyByDevice = snapshot.Month.ByDevice.ToDictionary(d => d.DeviceId, d => d.EnergyKwh, StringComparer.Ordinal);
        var busiest = snapshot.Month.ByDevice.Count > 0 ? snapshot.Month.ByDevice.Max(d => d.EnergyKwh) : 0;
        return snapshot.Members
            .Select(m =>
            {
                var energy = energyByDevice.GetValueOrDefault(m.DeviceId);
                var isThisPc = m.DeviceId == household.DeviceId;
                return new HouseholdMemberDisplay(
                    m.DeviceId, m.Name, m.Kind == ChassisKind.Laptop ? "Laptop" : "Desktop", isThisPc,
                    Format.Kwh(energy, _culture), busiest > 0 ? energy / busiest : 0, isThisPc ? "" : StatusOf(m, now), m.Left is not null);
            })
            .ToList();
    }

    /// <summary>"left" once removed or gone by choice; otherwise when it last synced, worded as recent ("synced … ago")
    /// or stale ("last seen … ago") at a day, and "not synced yet" for one that never has (households design §2). This
    /// PC's own row shows none of this (households design §2, review finding A9): it has no "last synced" of its own to
    /// report, and <see cref="Rows"/> never calls this for it.</summary>
    internal string StatusOf(HouseholdMemberRow member, DateTimeOffset now)
    {
        if (member.Left is not null) return "left";
        if (member.LastSynced is not { } at) return "not synced yet";
        var since = now - at;
        return since < TimeSpan.FromHours(24) ? $"synced {Format.Ago(since, _culture)} ago" : $"last seen {Format.Ago(since, _culture)} ago";
    }

    private HouseholdPeriod Empty(string title) => new(title, Format.Kwh(0, _culture), []);

    private static bool IsValidName(string input) => input.Trim().Length is >= 1 and <= 40;

    /// <summary>Sends the trimmed name (households design §2, RenamePcRequest: 1 to 40 characters). Call on the UI thread.</summary>
    private async Task SaveNameAsync()
    {
        var name = _nameInput.Trim();
        var result = await _link.RenamePcAsync(name).ConfigureAwait(false);
        _threads.Post(() =>
        {
            if (result.Ok) _lastServerName = name;
            NameMessage = result.Ok ? null : result.Message;
        });
    }

    private void BeginConfirm(PendingAction action, HouseholdMemberDisplay? member, string text)
    {
        _pending = action;
        _pendingMember = member;
        ActionMessage = null;
        ConfirmText = text;
    }

    private void EndConfirm()
    {
        _pending = PendingAction.None;
        _pendingMember = null;
        ConfirmText = null;
    }

    /// <summary>Removing and leaving ask first (households design §2); this sends the one that was confirmed. Call on the
    /// UI thread.</summary>
    private async Task ConfirmPendingAsync()
    {
        var action = _pending;
        var member = _pendingMember;
        var result = action switch
        {
            PendingAction.RemovePc when member is not null => await _link.RemovePcAsync(member.DeviceId).ConfigureAwait(false),
            PendingAction.LeaveHousehold => await _link.LeaveHouseholdAsync().ConfigureAwait(false),
            PendingAction.RemoveOldRows when member is not null => await _link.RemoveOldRowsAsync(member.DeviceId).ConfigureAwait(false),
            PendingAction.RemoveAllOldRows => await _link.RemoveOldRowsAsync(null).ConfigureAwait(false),
            _ => HouseholdOutcome.NoAnswer,
        };
        _threads.Post(() =>
        {
            EndConfirm();
            ActionMessage = result.Ok ? null : result.Message;
            if (result.Ok) Refresh();
        });
    }

    /// <summary>Review finding A6: only starts the new code off — it arrives as its own pushed RecoveryCode notice,
    /// which App.xaml.cs opens a window from once it comes back.</summary>
    private async Task MakeRecoveryCodeAsync()
    {
        var result = await _link.NewRecoveryCodeAsync().ConfigureAwait(false);
        _threads.Post(() => RecoveryMessage = result.Ok ? null : result.Message);
    }
}
