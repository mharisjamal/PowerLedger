using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

/// <summary>Review item 5: an app keeps one key across its updates, the folders a new version renames left out.</summary>
public class AppKeysTests
{
    [Theory]
    [InlineData(@"C:\Users\Ann\AppData\Local\Discord\app-1.0.9001\Discord.exe", @"c:\users\ann\appdata\local\discord\discord.exe")]
    [InlineData(@"C:\Users\Ann\AppData\Local\slack\app-4.33.73\slack.exe", @"c:\users\ann\appdata\local\slack\slack.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0.2849.80\msedgewebview2.exe", @"c:\program files (x86)\microsoft\edgewebview\application\msedgewebview2.exe")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.24090.11-0\MsMpEng.exe", @"c:\programdata\microsoft\windows defender\platform\msmpeng.exe")]
    [InlineData(@"C:\Program Files\JetBrains\IntelliJ IDEA 2024.2.1\bin\idea64.exe", @"c:\program files\jetbrains\intellij idea\bin\idea64.exe")]
    [InlineData(@"C:\Users\Ann\AppData\Roaming\Claude\claude-code\2.1.295\d2e29494ec83\claude.exe", @"c:\users\ann\appdata\roaming\claude\claude-code\claude.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", @"c:\program files\google\chrome\application\chrome.exe")]
    [InlineData(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", @"c:\program files\microsoft office\root\office16\winword.exe")]
    [InlineData(@"D:\Games\game2\v2.exe", @"d:\games\game2\v2.exe")]
    public void A_path_s_key_leaves_out_its_version_folders(string path, string key)
        => AppKeys.ForPath(path).ShouldBe(key);

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe", true)]
    [InlineData(@"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.24090.11-0\MsMpEng.exe", true)]
    [InlineData(@"C:\Program Files\Windows Defender Advanced Threat Protection\MsSense.exe", true)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    public void Windows_own_images_are_those_under_its_folder_and_its_defenders(string path, bool windows)
        => AppKeys.IsWindows(path.ToLowerInvariant(), @"c:\windows\").ShouldBe(windows);
}
