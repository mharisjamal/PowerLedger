using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>The owner's UPS on another computer (Network UPS Tools): its settings, its password, and what the sensors see.</summary>
public class NutSettingsTests
{
    private static readonly NutSettings Nas = new() { Host = "nas.local", Ups = "myups", Username = "monitor" };

    [Fact]
    public void Off_by_default_and_a_whole_server_passes_its_checks()
    {
        ServiceSettings.Default.Nut.IsSetUp.ShouldBeFalse();
        ServiceSettings.Default.Nut.Port.ShouldBe(3493);
        With(Nas).ShouldBeNull();
        With(Nas with { Host = "192.168.1.20", Port = 3500, Username = "", Password = "p@ss word" }).ShouldBeNull();
        With(Nas with { Host = "[fe80::1%12]" }).ShouldBeNull();
    }

    [Theory]
    [InlineData("nas local", "myups", 3493, "", "Type the UPS server as a computer name or an IP address.")]
    [InlineData("nas.local", "my ups", 3493, "", "Type the UPS's name as the server knows it, in letters, digits, dots, dashes or underscores.")]
    [InlineData("nas.local", "myups", 0, "", "The UPS server's port must be between 1 and 65535.")]
    [InlineData("nas.local", "myups", 70000, "", "The UPS server's port must be between 1 and 65535.")]
    [InlineData("nas.local", "", 3493, "", "Type both the UPS server and the UPS's name, or neither.")]
    [InlineData("", "myups", 3493, "", "Type both the UPS server and the UPS's name, or neither.")]
    [InlineData("nas.local", "myups", 3493, "two words", "The UPS server's username can't hold spaces and is at most 64 characters.")]
    public void Each_value_is_held_to_what_upsd_takes(string host, string ups, int port, string user, string problem)
        => With(new NutSettings { Host = host, Ups = ups, Port = port, Username = user }).ShouldBe(problem);

    [Fact]
    public void A_password_with_a_line_break_could_end_the_command_early_and_is_refused()
        => With(Nas with { Password = "a\nLOGIN myups" }).ShouldBe("The UPS server's password is at most 128 characters.");

    [Fact]
    public void Settings_stored_before_the_ups_server_take_it_as_off()
    {
        var stored = PipeProtocol.DeserializeSettings("""{"profile":{"chassis":0},"idleThresholdSeconds":300}""").ShouldNotBeNull();

        stored.Nut.ShouldBe(NutSettings.Off);
        stored.Validate().ShouldBeNull();
    }

    [Fact]
    public void A_password_sent_is_kept_encrypted_and_taken_out_of_the_settings()
    {
        using var t = new TestDatabase();
        var repository = new SettingsRepository(t.Db);
        var secret = new NutSecret(repository);

        var taken = secret.Take(ServiceSettings.Default with { Nut = Nas with { Password = "sec ret" } });

        taken.Nut.Password.ShouldBeNull();
        taken.Nut.HasPassword.ShouldBeTrue();
        secret.Password().ShouldBe("sec ret");
        repository.Get(NutSecret.PasswordKey).ShouldNotBeNull().ShouldNotContain("sec ret");
        PipeProtocol.SerializeSettings(taken).ShouldNotContain("sec ret");
    }

    [Fact]
    public void No_password_sent_keeps_the_one_held_an_empty_one_forgets_it_and_turning_the_server_off_forgets_it_too()
    {
        using var t = new TestDatabase();
        var secret = new NutSecret(new SettingsRepository(t.Db));
        secret.Take(ServiceSettings.Default with { Nut = Nas with { Password = "sec ret" } });

        secret.Take(ServiceSettings.Default with { Nut = Nas with { Port = 3500 } }).Nut.HasPassword.ShouldBeTrue();
        secret.Password().ShouldBe("sec ret");

        secret.Take(ServiceSettings.Default with { Nut = Nas with { Password = "" } }).Nut.HasPassword.ShouldBeFalse();
        secret.Password().ShouldBeNull();

        secret.Take(ServiceSettings.Default with { Nut = Nas with { Password = "again" } });
        secret.Take(ServiceSettings.Default).Nut.HasPassword.ShouldBeFalse();
        secret.Password().ShouldBeNull();
    }

    [Fact]
    public async Task The_loop_stores_and_publishes_settings_without_the_password()
    {
        using var t = new TestDatabase();
        var board = new StatusBoard();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
        var commands = new LoopCommands();
        var environment = new LoopEnvironment(
            Sensors: _ => new FakeSensorSet((ts, delta) => Samples.At(ts, delta, batteryW: 20)),
            Inventory: () => Facts.Laptop(), SystemUptime: () => TimeSpan.FromHours(1), SystemShuttingDown: () => false, DatabaseNotice: null);
        var loop = new SamplingLoop(t.Db, environment, commands, new LiveFeed(), board, new MonitorBoard(MonitorBoardTests.Catalogue, clock),
            clock, NullLogger<SamplingLoop>.Instance);
        await loop.StartAsync(CancellationToken.None);
        await loop.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var sent = board.Settings.ShouldNotBeNull() with { Nut = Nas with { Password = "sec ret" } };
            await commands.SendAsync(new ApplySettingsCommand(sent)).WaitAsync(TimeSpan.FromSeconds(5));

            var published = board.Settings.ShouldNotBeNull();
            published.Nut.ShouldBe(Nas with { HasPassword = true });
            new SettingsStore(new SettingsRepository(t.Db)).Load().ShouldNotBeNull().Nut.ShouldBe(Nas with { HasPassword = true });
            new NutSecret(new SettingsRepository(t.Db)).Password().ShouldBe("sec ret");
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void The_sensors_see_the_server_set_up_and_the_same_target_while_the_settings_stay_the_same()
    {
        var board = new StatusBoard();
        var asked = 0;
        var nut = new NutSwitch(board, () => { asked++; return "sec ret"; }, CancellationToken.None);

        nut.Current().ShouldBeNull();                                   // nothing published yet
        board.Publish(ServiceSettings.Default);
        nut.Current().ShouldBeNull();                                   // off

        var settings = ServiceSettings.Default with { Nut = Nas with { Host = " nas.local ", HasPassword = true } };
        board.Publish(settings);
        var target = nut.Current().ShouldNotBeNull();
        (target.Host, target.Port, target.Ups, target.Username).ShouldBe(("nas.local", 3493, "myups", "monitor"));
        target.Password.ShouldNotBeNull()().ShouldBe("sec ret");
        nut.Current().ShouldBeSameAs(target);

        board.Publish(settings with { Nut = settings.Nut with { } });   // applied again, as after a new password
        nut.Current().ShouldNotBeSameAs(target);

        board.Publish(ServiceSettings.Default with { Nut = Nas });      // no password held: none is said
        nut.Current().ShouldNotBeNull().Password.ShouldBeNull();
        asked.ShouldBe(1);
    }

    [Fact]
    public void A_retired_sensor_set_reads_no_server()
    {
        var board = new StatusBoard();
        board.Publish(ServiceSettings.Default with { Nut = Nas });
        using var retired = new CancellationTokenSource();
        var nut = new NutSwitch(board, () => null, retired.Token);
        nut.Current().ShouldNotBeNull();

        retired.Cancel();

        nut.Current().ShouldBeNull();
    }

    private static string? With(NutSettings nut) => (ServiceSettings.Default with { Nut = nut }).Validate();
}
