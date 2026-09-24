using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>The households contract (households plan, Task 0): every new message round-trips through the pipe, and a
/// status from a service without households has none.</summary>
public class HouseholdContractTests
{
    [Fact]
    public void The_new_messages_round_trip_through_the_pipe()
    {
        var at = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        PipeMessage[] messages =
        [
            new BrowsePcsRequest(1),
            new FoundPcsReply(2, [new FoundPc("a1b2", "Laptop-2", false)]),
            new AddPcRequest(3, "a1b2"),
            new StartCodePairingRequest(4),
            new JoinByCodeRequest(5, "K7QM-2XHD-9PW4-R8TA"),
            new AnswerPromptRequest(6, "p1", true),
            new RemovePcRequest(7, "0123456789abcdef0123456789abcdef"),
            new LeaveHouseholdRequest(8),
            new RenamePcRequest(9, "Study PC"),
            new SetDiscoverableRequest(10, false),
            new SignInRequest(11, "microsoft", "eyJ...", "n1", "ABCD-EFGH"),
            new SignOutRequest(12),
            new DeleteAccountRequest(13),
            new HouseholdReply(14, true, "Code made.", "K7QM-2XHD-9PW4-R8TA"),
            new HouseholdNotice(NoticeKind.JoinPrompt, "p1", "Join Desktop-7's household?", "Desktop-7", "482 913", at),
            new CancelPairingRequest(15),
            new NewRecoveryCodeRequest(16),
            new HouseholdNotice(NoticeKind.RecoveryCode, "p2", "Keep this recovery code.", null, null, null, "ABCD-EFGH-JKMN-PQRS-TVWX-YZ01"),
            new RemoveOldRowsRequest(17, "0123456789abcdef0123456789abcdef"),
            new RemoveOldRowsRequest(18),
        ];
        foreach (var message in messages)
        {
            var line = PipeProtocol.Serialize(message);
            var back = PipeProtocol.Deserialize(line.AsSpan(0, line.Length - 1));
            back.GetType().ShouldBe(message.GetType());
            PipeProtocol.Serialize(back).ShouldBe(line);   // lists compare by reference in records, so the line is compared
        }
    }

    [Fact]
    public void A_status_with_a_household_carries_its_members()
    {
        var household = new HouseholdStatus("h1", "d1", "Desktop-7", ChassisKind.Desktop, true,
            [new MemberStatus("d1", "Desktop-7", ChassisKind.Desktop, true, null, false),
             new MemberStatus("d2", "Laptop-2", ChassisKind.Laptop, false, DateTimeOffset.UnixEpoch, false)], null);
        var status = new ServiceStatus("0.7.0", DateTimeOffset.UnixEpoch, 0, [], 0, 0, new CalibrationStatus(0, 0, 0, 0), "", 0,
            null, null, null, Household: household);

        var line = PipeProtocol.Serialize(new StatusReply(1, status));
        var back = PipeProtocol.Deserialize(line.AsSpan(0, line.Length - 1)).ShouldBeOfType<StatusReply>();

        back.Status.Household.ShouldNotBeNull().Members.Count.ShouldBe(2);
        back.Status.Household.Members[1].Kind.ShouldBe(ChassisKind.Laptop);
    }

    [Fact]
    public void A_status_from_a_service_without_households_has_none()
    {
        var status = new ServiceStatus("0.6.0", DateTimeOffset.UnixEpoch, 0, [], 0, 0, new CalibrationStatus(0, 0, 0, 0), "", 0,
            null, null, null);
        var line = PipeProtocol.Serialize(new StatusReply(1, status));
        PipeProtocol.Deserialize(line.AsSpan(0, line.Length - 1)).ShouldBeOfType<StatusReply>().Status.Household.ShouldBeNull();
    }
}
