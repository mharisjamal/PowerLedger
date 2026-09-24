using System.Runtime.InteropServices;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class ScrubberTests
{
    private static readonly ScrubNames Names = new("alice", "DESKTOP-4F2K9", "CONTOSO");

    private static string Scrub(string text) => Scrubber.Scrub(text, Names);

    [Theory]
    [InlineData(@"Could not find file 'C:\Users\alice\AppData\Local\PowerLedger\ui.json'.", "Could not find file '<path>'.")]
    [InlineData("Could not find file 'c:/users/Bob Smith/ui.json'.", "Could not find file '<path>'.")]
    [InlineData(@"Could not open 'C:\Users\alice\Documents\Alice Smith - bank statement 2025.pdf'.", "Could not open '<path>'.")]
    [InlineData(@"Access to the path 'D:\Clients\Acme Corp\invoice.pdf' is denied.", "Access to the path '<path>' is denied.")]
    [InlineData(@"Access to the path D:\Clients\Acme Corp\invoice.pdf is denied.", "Access to the path <path>")]
    [InlineData(@"Could not find '\\fileserver\share\Alice\notes.docx'.", "Could not find '<path>'.")]
    [InlineData("Could not load file:///C:/Users/alice/x.dll", "Could not load file:///<path>")]
    public void AFilePathIsTakenOutWhole(string text, string scrubbed) => Scrub(text).ShouldBe(scrubbed);

    [Theory]
    [InlineData(@"Could not open 'D:\Backups\Storage\2025\Alice Smith tax return.pdf'.", "Could not open '<path>'.")]
    [InlineData(@"Could not open 'C:\Users\alice\Work\R&D\Bob Jones review.docx'.", "Could not open '<path>'.")]
    [InlineData(@"Failed at D:\Display\SW\USB\file.txt now", "Failed at <path>")]
    public void AFolderNamedLikeADeviceDoesntCutAPathShort(string text, string scrubbed) => Scrub(text).ShouldBe(scrubbed);

    [Theory]
    [InlineData(@"Could not open '\\?\C:\Users\alice\OneDrive - Contoso Ltd\Clients\Acme Corp\secret.pdf'.", "Could not open '<path>'.")]
    [InlineData(@"Could not open \\?\UNC\server\share\Alice\x.docx", "Could not open <path>")]
    [InlineData(@"Could not open \\?\Volume{12345678-1234-1234-1234-123456789abc}\Alice\x.docx", "Could not open <path>")]
    public void ALongPathIsTakenOutWhole(string text, string scrubbed) => Scrub(text).ShouldBe(scrubbed);

    [Theory]
    [InlineData(@"Could not find file 'C:\Users\o'brien\Documents\x.pdf'.", "Could not find file '<path>'.")]
    [InlineData(@"Access to the path 'D:\Clients\Bob O'Neil\medical.pdf' is denied.", "Access to the path '<path>' is denied.")]
    [InlineData(@"Could not find '\\fileserver\share\O'Hara\notes.docx'.", "Could not find '<path>'.")]
    public void AQuotedPathRunsToItsClosingQuoteThroughAnyApostropheInIt(string text, string scrubbed) => Scrub(text).ShouldBe(scrubbed);

    [Theory]
    [InlineData("Could not find //fileserver/share/Alice/notes.docx", "Could not find <path>")]
    [InlineData("Could not load file://server/share/Alice/x.pdf", "Could not load file://<path>")]
    public void AShareWrittenWithForwardSlashesIsTakenOut(string text, string scrubbed) => Scrub(text).ShouldBe(scrubbed);

    [Fact]
    public void AUsersSecurityIdIsTakenOutAndAWellKnownOneKept() =>
        Scrub(@"HKEY_USERS\S-1-5-21-3623811015-3361044348-30300820-1013\Software as S-1-5-18")
            .ShouldBe(@"HKEY_USERS\<sid>\Software as S-1-5-18");

    /// <summary>Guards against catastrophic backtracking: before 6eace44, text of one repeated unit made a pattern try
    /// every start against every end, so its work grew with the square of the length (39 million cycles for 3,000
    /// characters of "a#{", 606 million for 12,000). A limit on the time itself failed once on a host at 100% CPU, so
    /// this asserts the growth instead, in the cycles this thread spends, which waiting behind other threads doesn't add
    /// to: four times the text may cost at most eight times the cycles (about four for a linear pattern, sixteen or far
    /// more for a backtracking one). Each count is the fewest of seven tries, taken turn about, so a garbage collection
    /// or the compiler's tiering in one try doesn't count.</summary>
    [Theory]
    [InlineData("a#{")]
    [InlineData(@"a\")]
    [InlineData(@"'C:\")]
    [InlineData(@"\\?\")]
    [InlineData("a@b.")]
    public void LongRepetitiveTextIsScrubbedInLinearTime(string unit)
    {
        var shorter = string.Concat(Enumerable.Repeat(unit, 3_000 / unit.Length));
        var longer = string.Concat(Enumerable.Repeat(unit, 12_000 / unit.Length));
        Scrub(longer);   // the first call compiles the patterns

        var (shorterCycles, longerCycles) = (ulong.MaxValue, ulong.MaxValue);
        for (var i = 0; i < 7; i++)
        {
            shorterCycles = Math.Min(shorterCycles, Cycles(() => Scrub(shorter)));
            longerCycles = Math.Min(longerCycles, Cycles(() => Scrub(longer)));
        }

        ((double)longerCycles / shorterCycles).ShouldBeLessThan(8,
            $"3,000 characters cost {shorterCycles:N0} cycles at the least and 12,000 cost {longerCycles:N0}");
    }

    /// <summary>The CPU cycles the current thread spends on <paramref name="work"/>.</summary>
    private static ulong Cycles(Action work)
    {
        QueryThreadCycleTime(GetCurrentThread(), out var before);
        work();
        QueryThreadCycleTime(GetCurrentThread(), out var after);
        return after - before;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [Fact]
    public void AStackFrameKeepsOnlyItsSourceFilesName() =>
        Scrub(@"   at PowerLedger.App.Now.NowViewModel.Refresh() in D:\PowerLedger\src\PowerLedger.App\Now\NowViewModel.cs:line 42")
            .ShouldBe(@"   at PowerLedger.App.Now.NowViewModel.Refresh() in <path>\NowViewModel.cs:line 42");

    [Fact]
    public void ADevicePathIsTakenOutWhole() =>
        Scrub(@"Failed to open \\?\hid#vid_1b1c&pid_1c05&mi_00#8&2d0f1a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030} (error 5)")
            .ShouldBe("Failed to open <device> (error 5)");

    [Theory]
    [InlineData(@"Monitor DISPLAY\GSM5B7F\5&1a2b3c&0&UID4352 did not answer", "Monitor <id> did not answer")]
    [InlineData(@"USB\VID_1B1C&PID_1C05\7&2D0F1A&0&1 went away", "<id> went away")]
    [InlineData(@"USB\VID_0764&PID_0501\CR7GR2000123 went away", "<id> went away")]
    [InlineData(@"HID\VID_1B1C&PID_1C05&MI_00\8&2D0F1A&0&0000 failed", "<id> failed")]
    public void AWindowsDeviceInstanceIdIsTakenOut(string text, string scrubbed) => Scrub(text).ShouldBe(scrubbed);

    [Theory]
    [InlineData(@"PNPDeviceID=HID\VID_04F2&PID_0001\6&1a2b3c4d&0&0000", "PNPDeviceID=<id>")]
    [InlineData(@"id:HID\VID_04F2&PID_0001\6&1a2b3c4d&0&0000", "id:<id>")]
    [InlineData(@"dev=XYZBUS\DEV_01&REV_02\3&abc&0&1", "dev=<id>")]
    [InlineData("(hid#vid_1b1c&pid_1c05&mi_00#8&2d0f1a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030})", "(<device>)")]
    [InlineData("path=usb#vid_046d&pid_c52b#5&1c2d3e4f&0&2#{a5dcbf10-6530-11d2-901f-00c04fb951ed}", "path=<device>")]
    public void AnIdRightAfterPunctuationIsTakenOut(string text, string scrubbed) => Scrub(text).ShouldBe(scrubbed);

    [Fact]
    public void ALongPathInAStackFrameKeepsItsSourceFilesName() =>
        Scrub(@"   at X.Y() in \\?\C:\Users\alice\repo\NowViewModel.cs:line 42")
            .ShouldBe(@"   at X.Y() in <path>\NowViewModel.cs:line 42");

    [Fact]
    public void AnInterfacePathWithoutItsPrefixIsTakenOut() =>
        Scrub("open hid#vid_1b1c&pid_1c05&mi_00#8&2d0f1a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030} failed")
            .ShouldBe("open <device> failed");

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
    [InlineData(@"relative PowerLedger\src\App stays")]
    [InlineData(@"the pipe \\.\pipe\PowerLedger.v1 closed")]
    [InlineData("fetch https://api.github.com/repos/x/releases/latest failed")]
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
