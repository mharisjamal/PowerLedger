using System.IO;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Report finding 5: the Habits saving reads the service's idle threshold and what the PC draws asleep from the
/// service's settings, as stored in its database.</summary>
public sealed class IdleSetupTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"powerledger-idle-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void The_setup_comes_from_the_service_s_settings()
    {
        IdleSetup.Of(null).ShouldBe(IdleSetup.Default);
        IdleSetup.Default.ShouldBe(new IdleSetup(TimeSpan.FromMinutes(5), IdleSetup.DesktopAsleepW));
        IdleSetup.Of(new ServiceSettings { IdleThresholdSeconds = 600, Profile = MachineProfile.DefaultLaptop })
            .ShouldBe(new IdleSetup(TimeSpan.FromMinutes(10), IdleSetup.LaptopAsleepW));
        IdleSetup.Of(new ServiceSettings { IdleThresholdSeconds = 120, Profile = MachineProfile.DefaultDesktop })
            .ShouldBe(new IdleSetup(TimeSpan.FromMinutes(2), IdleSetup.DesktopAsleepW));
    }

    [Fact]
    public void The_reader_reads_the_settings_the_service_stored_each_time()
    {
        using var writer = SqliteDatabase.OpenAndMigrate(_path);
        using var reader = new SqliteDatabase(_path, readOnly: true);
        var read = IdleSetup.Reader(reader);

        read().ShouldBe(IdleSetup.Default, "nothing stored yet");
        var settings = new SettingsRepository(writer);
        settings.Set("service.settings", PipeProtocol.SerializeSettings(new ServiceSettings { IdleThresholdSeconds = 900, Profile = MachineProfile.DefaultLaptop }));
        read().ShouldBe(new IdleSetup(TimeSpan.FromMinutes(15), IdleSetup.LaptopAsleepW));
        settings.Set("service.settings", "not settings");
        read().ShouldBe(IdleSetup.Default);
    }
}
