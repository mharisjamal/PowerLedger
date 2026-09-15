using System.IO;
using System.Security.Cryptography;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class SetupRunnerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"powerledger-setup-{Guid.NewGuid():N}.exe");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void Setup_is_told_to_update_quietly_and_open_the_App_again()
        => SetupRunner.Arguments(@"C:\Users\a\AppData\Local\PowerLedger\Updates\PowerLedger-0.2.0-setup.log")
            .ShouldBe("/SILENT /NORESTART /UPDATE=1 /LOG=\"C:\\Users\\a\\AppData\\Local\\PowerLedger\\Updates\\PowerLedger-0.2.0-setup.log\"");

    [Fact]
    public async Task An_installer_that_changed_since_its_download_is_not_run()
    {
        File.WriteAllBytes(_file, [1, 2, 3]);
        (await Should.ThrowAsync<UpdateException>(new SetupRunner().RunAsync(_file, 3, SHA256.HashData([9, 9, 9]), _file + ".log", CancellationToken.None)))
            .Message.ShouldBe("The downloaded update changed on disk, so it wasn't run. PowerLedger downloads it again.");
        await Should.ThrowAsync<UpdateException>(new SetupRunner().RunAsync(_file, 4, SHA256.HashData([1, 2, 3]), _file + ".log", CancellationToken.None));
    }

    [Fact]
    public async Task An_installer_that_is_gone_is_downloaded_again()
        => (await Should.ThrowAsync<UpdateException>(new SetupRunner().RunAsync(_file, 3, new byte[32], _file + ".log", CancellationToken.None)))
            .Message.ShouldBe("The downloaded update is gone. PowerLedger downloads it again.");
}
