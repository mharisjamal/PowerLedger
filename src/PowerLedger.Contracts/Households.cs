namespace PowerLedger.Contracts;

/// <summary>A PC in the household, as this PC knows it (households design §1).</summary>
/// <param name="DeviceId">Its device ID: the first 16 bytes of SHA-256 of its signing key, as hex.</param>
/// <param name="Name">Its name in the household.</param>
/// <param name="Kind">Laptop or desktop.</param>
/// <param name="IsThisPc">True for this PC's own entry.</param>
/// <param name="LastSyncedAt">When its rows last arrived here; null for this PC and for one never heard from.</param>
/// <param name="Left">True once it has left or been removed; its rows stay until the user removes them.</param>
public sealed record MemberStatus(
    string DeviceId, string Name, ChassisKind Kind, bool IsThisPc, DateTimeOffset? LastSyncedAt, bool Left);

/// <summary>Where the household stands, in <see cref="ServiceStatus.Household"/>.</summary>
/// <param name="HouseholdId">Null while this PC is in no household.</param>
/// <param name="DeviceId">This PC's device ID, made on first use.</param>
/// <param name="Name">This PC's name in the household.</param>
/// <param name="Kind">This PC's kind.</param>
/// <param name="Discoverable">Whether other PCs on a Private network can find this one.</param>
/// <param name="Members">Every PC in the household, this one included; empty while in none.</param>
/// <param name="Problem">Null while syncing goes well; otherwise the last problem, in words the App can show.</param>
/// <param name="SignedIn">N2: true while this PC holds a session.</param>
/// <param name="PendingApprovals">N2: PCs signed in as this account waiting for a member to approve them.</param>
public sealed record HouseholdStatus(
    string? HouseholdId,
    string DeviceId,
    string Name,
    ChassisKind Kind,
    bool Discoverable,
    IReadOnlyList<MemberStatus> Members,
    string? Problem,
    bool SignedIn = false,
    int PendingApprovals = 0);

/// <summary>A PowerLedger PC found on this network.</summary>
/// <param name="InstanceId">The random name it announces itself under, which <see cref="AddPcRequest"/> names.</param>
/// <param name="Name">Its display name.</param>
/// <param name="InThisHousehold">True when its household tag shows it is already in this PC's household.</param>
public sealed record FoundPc(string InstanceId, string Name, bool InThisHousehold);

public enum NoticeKind
{
    /// <summary>This PC is being added: show Join / Don't join, with the comparison code when there is one.</summary>
    JoinPrompt = 0,

    /// <summary>A pairing this PC started moved on: its comparison code, or its outcome in the text.</summary>
    PairingProgress = 1,

    /// <summary>N2: a PC signed in as this account asks to join; show Approve / Don't approve.</summary>
    ApprovePrompt = 2,

    /// <summary>Something the user should know, in the text: a PC was removed, this PC was removed, and so on.</summary>
    Info = 3,
}

/// <summary>Pushed by the service to the App in the console session, the one at the screen (households design §9).</summary>
/// <param name="Kind">What it is.</param>
/// <param name="PromptId">For a prompt: what <see cref="AnswerPromptRequest"/> names; null otherwise.</param>
/// <param name="Text">What to show, in words.</param>
/// <param name="FromName">The other PC's name, when there is one.</param>
/// <param name="ComparisonCode">"482 913" for a pairing on the network; null for a pairing by code.</param>
/// <param name="ExpiresAt">When an unanswered prompt counts as Don't join.</param>
public sealed record HouseholdNotice(
    NoticeKind Kind, string? PromptId, string Text, string? FromName, string? ComparisonCode, DateTimeOffset? ExpiresAt)
    : PipeMessage;
