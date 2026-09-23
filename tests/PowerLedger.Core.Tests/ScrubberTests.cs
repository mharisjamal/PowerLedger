using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class ScrubberTests
{
    private static readonly ScrubNames Names = new("alice", "DESKTOP-4F2K9", "CONTOSO");

    private static string Scrub(string text) => Scrubber.Scrub(text, Names);

    [Theory]
    [InlineData(@"Could not find file 'C:\Users\alice\AppData\Local\PowerLedger\ui.json'.",
        @"Could not find file '%USERPROFILE%\AppData\Local\PowerLedger\ui.json'.")]
    [InlineData("Could not find file 'c:/users/Bob Smith/ui.json'.", "Could not find file '%USERPROFILE%/ui.json'.")]
    public void AProfilePathLosesItsUsersName(string text, string scrubbed) => Scrub(text).ShouldBe(scrubbed);

    [Fact]
    public void ADevicePathIsTakenOutWhole() =>
        Scrub(@"Failed to open \\?\hid#vid_1b1c&pid_1c05&mi_00#8&2d0f1a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030} (error 5)")
            .ShouldBe("Failed to open <device> (error 5)");

    [Theory]
    [InlineData(@"Monitor DISPLAY\GSM5B7F\5&1a2b3c&0&UID4352 did not answer", "Monitor <id> did not answer")]
    [InlineData(@"USB\VID_1B1C&PID_1C05\7&2D0F1A&0&1 went away", "<id> went away")]
    public void AWindowsDeviceInstanceIdIsTakenOut(string text, string scrubbed) => Scrub(text).ShouldBe(scrubbed);

    [Theory]
    [InlineData("mail alice.smith@example.com now", "mail <email> now")]
    [InlineData("connect to 192.168.1.20:443 failed", "connect to <ip>:443 failed")]
    [InlineData("from fe80::1c2a:3b4c%12 refused", "from <ip> refused")]
    [InlineData("via 2001:db8::1 refused", "via <ip> refused")]
    [InlineData("via 2001:0db8:0000:0000:0000:ff00:0042:8329 refused", "via <ip> refused")]
    public void AddressesAreTakenOut(string text, string scrubbed) => Scrub(text).ShouldBe(scrubbed);

    [Theory]
    [InlineData("logged on as alice.", "logged on as <user>.")]
    [InlineData("logged on as ALICE.", "logged on as <user>.")]
    [InlineData(@"on DESKTOP-4F2K9 in CONTOSO", "on <machine> in <domain>")]
    public void TheUserMachineAndDomainNamesAreTakenOut(string text, string scrubbed) => Scrub(text).ShouldBe(scrubbed);

    [Fact]
    public void ANameInsideAnotherWordIsLeft() => Scrub("malice aforethought").ShouldBe("malice aforethought");

    [Fact]
    public void AServiceAccountsNamesAreLeftSoTypeNamesSurvive() =>
        Scrubber.Scrub("System.InvalidOperationException at NT AUTHORITY", new ScrubNames("SYSTEM", "PC-1", "NT AUTHORITY"))
            .ShouldBe("System.InvalidOperationException at NT AUTHORITY");

    [Fact]
    public void AShortNameIsLeftSinceItWouldMatchOrdinaryWords() =>
        Scrubber.Scrub("al is at the top", new ScrubNames("al", null, null)).ShouldBe("al is at the top");

    [Theory]
    [InlineData("at 10:11:12 the reading stopped")]
    [InlineData("in std::vector<abc::def>")]
    [InlineData("   at PowerLedger.Service.SamplingLoop.OnMinute() in D:\\PowerLedger\\src\\SamplingLoop.cs:line 274")]
    public void OrdinaryTextIsLeftAlone(string text) => Scrub(text).ShouldBe(text);

    [Fact]
    public void ScrubbingTwiceChangesNothingMore()
    {
        const string text = @"alice on DESKTOP-4F2K9: C:\Users\alice\x \\?\hid#1&2 USB\VID_1&PID_2\3&4 a@b.cd 10.0.0.1";
        var once = Scrub(text);
        Scrub(once).ShouldBe(once);
    }

    [Fact]
    public void NothingGivesNothing()
    {
        Scrub("").ShouldBe("");
        Scrubber.Scrub(null, Names).ShouldBe("");
    }
}
