using PowerLedger.Contracts;

namespace PowerLedger.App.Tests;

/// <summary>A service link the test drives by hand.</summary>
internal sealed class FakeLink : IServiceLink
{
    public event Action<ReadingFrame>? FrameReceived;

    public event Action<bool>? ConnectionChanged;

    public event Action<HouseholdNotice>? HouseholdNoticeReceived;

    public bool IsConnected { get; private set; }

    public ServiceStatus? Status { get; set; } = Statuses.Running();

    public ServiceSettings? Settings { get; set; } = ServiceSettings.Default;

    public void Start()
    {
    }

    public void Push(ReadingFrame frame) => FrameReceived?.Invoke(frame);

    /// <summary>Pushes a household notice as the service would (households design §9), for a test to see how the App
    /// reacts to a join or approve prompt, pairing progress, or information.</summary>
    public void PushNotice(HouseholdNotice notice) => HouseholdNoticeReceived?.Invoke(notice);

    public void Connect(bool connected)
    {
        IsConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    /// <summary>How many times the App asked for the status.</summary>
    public int StatusReads { get; private set; }

    /// <summary>When set, a status read completes only once the test resolves this, to test what happens meanwhile.</summary>
    public TaskCompletionSource<ServiceStatus?>? StatusGate { get; set; }

    public Task<ServiceStatus?> GetStatusAsync(CancellationToken cancel = default)
    {
        StatusReads++;
        return StatusGate?.Task ?? Task.FromResult(IsConnected ? Status : null);
    }

    public Task<ServiceSettings?> GetSettingsAsync(CancellationToken cancel = default) => Task.FromResult(IsConnected ? Settings : null);

    /// <summary>Every change the App asked for, in order.</summary>
    public List<object> Writes { get; } = [];

    /// <summary>What every change comes back as.</summary>
    public WriteResult Answer { get; set; } = WriteResult.Done;

    public Task<WriteResult> SetSettingsAsync(ServiceSettings settings, CancellationToken cancel = default) => Write(settings);

    public Task<WriteResult> SetTariffAsync(decimal pricePerKwh, string currency, DateTimeOffset? effectiveFrom, CancellationToken cancel = default)
        => Write((pricePerKwh, currency, effectiveFrom));

    public Task<WriteResult> ResetCalibrationAsync(CancellationToken cancel = default) => Write("reset");

    /// <summary>Every brightness report the App sent, in order, with the brightnesses, the power states and the display
    /// settings it carried. They are kept apart from <see cref="Writes"/>, being reports rather than changes the user asked
    /// for.</summary>
    public List<(IReadOnlyList<MonitorBrightness> Monitors, IReadOnlyList<MonitorPowerReading> Power, IReadOnlyList<MonitorDisplayReading> Displays)> BrightnessReports { get; } = [];

    /// <summary>When set, a brightness report fails with this.</summary>
    public Exception? ReportThrows { get; set; }

    public Task<WriteResult> ReportBrightnessAsync(
        IReadOnlyList<MonitorBrightness> monitors, IReadOnlyList<MonitorPowerReading> power, IReadOnlyList<MonitorDisplayReading> displays,
        CancellationToken cancel = default)
    {
        if (ReportThrows is { } error) return Task.FromException<WriteResult>(error);
        if (!IsConnected) return Task.FromResult(WriteResult.NotConnected);
        BrightnessReports.Add((monitors, power, displays));
        return Task.FromResult(Answer);
    }

    private Task<WriteResult> Write(object change)
    {
        if (!IsConnected) return Task.FromResult(WriteResult.NotConnected);
        Writes.Add(change);
        return Task.FromResult(Answer);
    }

    public Task<WriteResult> ReportUsageAsync(UsageCounts counts, CancellationToken cancel = default) => Write(counts);

    public Task<WriteResult> ReportCrashAsync(CrashReport crash, CancellationToken cancel = default) => Write(crash);

    /// <summary>Every sharing request the App asked for, in order: a <see cref="Consent"/> for setConsent, or the string
    /// "preview", "sendNow" or "delete" for the others.</summary>
    public List<object> SharingRequests { get; } = [];

    /// <summary>What every sharing request comes back as.</summary>
    public SharingOutcome SharingAnswer { get; set; } = new(true, "Done.");

    /// <summary>When set, a sharing request completes only once the test resolves this, to test what happens meanwhile.</summary>
    public TaskCompletionSource<SharingOutcome>? SharingGate { get; set; }

    public Task<SharingOutcome> SetConsentAsync(Consent consent, CancellationToken cancel = default) => Sharing(consent);

    public Task<SharingOutcome> PreviewUploadAsync(CancellationToken cancel = default) => Sharing("preview");

    public Task<SharingOutcome> SendNowAsync(CancellationToken cancel = default) => Sharing("sendNow");

    public Task<SharingOutcome> DeleteMyDataAsync(CancellationToken cancel = default) => Sharing("delete");

    private Task<SharingOutcome> Sharing(object request)
    {
        if (!IsConnected) return Task.FromResult(SharingOutcome.NotConnected);
        SharingRequests.Add(request);
        return SharingGate?.Task ?? Task.FromResult(SharingAnswer);
    }

    /// <summary>PCs "found" on the network, answered by <see cref="BrowsePcsAsync"/> while connected.</summary>
    public IReadOnlyList<FoundPc>? FoundPcs { get; set; } = [];

    /// <summary>How many times the App asked to browse.</summary>
    public int BrowseCalls { get; private set; }

    /// <summary>When set, a browse completes only once the test resolves this, to test what a still-running browse does
    /// about a timer tick or a second call (review finding A5).</summary>
    public TaskCompletionSource<IReadOnlyList<FoundPc>?>? BrowseGate { get; set; }

    /// <summary>Every household request the App asked for, in order: the instance id for Add a PC, the code for Join by
    /// code, or "startCodePairing" for the rest.</summary>
    public List<object> HouseholdRequests { get; } = [];

    /// <summary>What every household request comes back as.</summary>
    public HouseholdOutcome HouseholdAnswer { get; set; } = new(true, "Done.");

    /// <summary>When set, a household request completes only once the test resolves this, to test what happens meanwhile.</summary>
    public TaskCompletionSource<HouseholdOutcome>? HouseholdGate { get; set; }

    public Task<IReadOnlyList<FoundPc>?> BrowsePcsAsync(CancellationToken cancel = default)
    {
        BrowseCalls++;
        if (BrowseGate is { } gate) return gate.Task;
        return Task.FromResult(IsConnected ? FoundPcs : null);
    }

    public Task<HouseholdOutcome> AddPcAsync(string instanceId, CancellationToken cancel = default) => Household(instanceId);

    public Task<HouseholdOutcome> StartCodePairingAsync(CancellationToken cancel = default) => Household("startCodePairing");

    public Task<HouseholdOutcome> JoinByCodeAsync(string code, CancellationToken cancel = default) => Household(code);

    public Task<HouseholdOutcome> AnswerPromptAsync(string promptId, bool accept, CancellationToken cancel = default) => Household((promptId, accept));

    public Task<HouseholdOutcome> RemovePcAsync(string deviceId, CancellationToken cancel = default) => Household(("remove", deviceId));

    public Task<HouseholdOutcome> LeaveHouseholdAsync(CancellationToken cancel = default) => Household("leave");

    public Task<HouseholdOutcome> RemoveOldRowsAsync(string? deviceId, CancellationToken cancel = default) => Household(("removeOldRows", deviceId));

    public Task<HouseholdOutcome> RenamePcAsync(string name, CancellationToken cancel = default) => Household(("rename", name));

    public Task<HouseholdOutcome> SetDiscoverableAsync(bool on, CancellationToken cancel = default) => Household(("discoverable", on));

    public Task<HouseholdOutcome> SignInAsync(string provider, string idToken, string nonce, string? recoveryCode, CancellationToken cancel = default)
        => Household(("signIn", provider, idToken, nonce, recoveryCode));

    public Task<HouseholdOutcome> SignOutAsync(CancellationToken cancel = default) => Household("signOut");

    public Task<HouseholdOutcome> DeleteAccountAsync(CancellationToken cancel = default) => Household("deleteAccount");

    public Task<HouseholdOutcome> CancelPairingAsync(CancellationToken cancel = default) => Household("cancelPairing");

    public Task<HouseholdOutcome> NewRecoveryCodeAsync(CancellationToken cancel = default) => Household("newRecoveryCode");

    /// <summary>When set, a household request throws this instead of answering — for testing a caller's guard against
    /// something even <see cref="SignIn"/> itself didn't turn into a failed result (review finding A7).</summary>
    public Exception? HouseholdThrows { get; set; }

    private Task<HouseholdOutcome> Household(object request)
    {
        if (HouseholdThrows is { } error) return Task.FromException<HouseholdOutcome>(error);
        if (!IsConnected) return Task.FromResult(HouseholdOutcome.NotConnected);
        HouseholdRequests.Add(request);
        return HouseholdGate?.Task ?? Task.FromResult(HouseholdAnswer);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
