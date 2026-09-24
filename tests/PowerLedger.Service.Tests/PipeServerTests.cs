using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public sealed class PipeServerTests : IAsyncLifetime
{
    private readonly string _name = $"PowerLedger.test.{Guid.NewGuid():N}";
    private readonly TestDatabase _database = new();
    private readonly StatusBoard _board = new();
    private readonly LiveFeed _feed = new();
    private readonly ServiceSignals _signals = new(TimeProvider.System);
    private readonly MonitorBoard _monitors = new(MonitorBoardTests.Catalogue, TimeProvider.System);
    private readonly Households.NoticeHub _notices;
    private uint _console = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
    private PipeServer _server = null!;

    public PipeServerTests() => _notices = new Households.NoticeHub(() => _console);

    public async Task InitializeAsync()
    {
        var handler = new PipeHandler(
            new LoopCommands(), _board, _monitors, _signals, new TariffRepository(_database.Db), TimeProvider.System, new Sharing.SharingCommands());
        _server = new PipeServer(handler, _feed, _signals, NullLogger<PipeServer>.Instance, _name, _notices);
        await _server.StartAsync(CancellationToken.None);
        await _server.Listening.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task A_subscriber_in_the_console_session_gets_the_households_notices_and_one_elsewhere_doesnt()
    {
        var notice = new HouseholdNotice(NoticeKind.JoinPrompt, "p1", "Join Desktop-7's household?", "Desktop-7", null, null);
        await using var client = await ConnectAsync();
        await client.WriteAsync(new SubscribeRequest(1));
        (await client.ReadAsync()).ShouldBe(new OkReply(1));
        await WaitFor.True(() => _notices.AnyoneAtTheScreen);

        _notices.Publish(notice).ShouldBeTrue();
        (await client.ReadAsync()).ShouldBe(notice);

        _console += 1000;                                                          // someone else is at the screen now
        _notices.Publish(notice).ShouldBeFalse();
        _feed.Publish(PipeProtocolTests.Frame(12));
        (await client.ReadAsync()).ShouldBeOfType<ReadingFrame>();
    }

    [Fact]
    public async Task A_request_gets_its_reply_on_the_same_connection()
    {
        await using var client = await ConnectAsync();
        await client.WriteAsync(new GetSettingsRequest(1));
        (await client.ReadAsync()).ShouldBe(new ErrorReply(1, PipeHandler.Starting));
        _board.Publish(ServiceSettings.Default);
        await client.WriteAsync(new GetSettingsRequest(2));
        (await client.ReadAsync()).ShouldBe(new SettingsReply(2, ServiceSettings.Default));
    }

    [Fact]
    public async Task A_subscriber_receives_each_published_frame()
    {
        await using var client = await ConnectAsync();
        await client.WriteAsync(new SubscribeRequest(1));
        (await client.ReadAsync()).ShouldBe(new OkReply(1));
        await WaitFor.True(() => _feed.Subscribers == 1);
        _feed.Publish(PipeProtocolTests.Frame(12));
        (await client.ReadAsync()).ShouldBeOfType<ReadingFrame>().TotalW.ShouldBe(12);
    }

    [Fact]
    public async Task Several_clients_are_served_at_once_and_a_client_that_leaves_is_forgotten()
    {
        await using var a = await ConnectAsync();
        var b = await ConnectAsync();
        await a.WriteAsync(new ReportActivityRequest(1, 30));
        await b.WriteAsync(new ReportActivityRequest(1, 10));
        (await a.ReadAsync()).ShouldBe(new OkReply(1));
        (await b.ReadAsync()).ShouldBe(new OkReply(1));
        _signals.UserIdleSeconds().ShouldNotBeNull().ShouldBeLessThan(11);

        await b.DisposeAsync();
        await WaitFor.True(() => _signals.UserIdleSeconds() is > 29);
    }

    [Fact]
    public async Task A_brightness_report_over_the_pipe_changes_what_a_monitor_draws_now()
    {
        _monitors.Detected([MonitorBoardTests.Dell]);
        _monitors.Status(displayOn: true).ShouldHaveSingleItem().WattsNow.ShouldBe(MonitorBoardTests.DellAt(null), 1e-9);
        await using var client = await ConnectAsync();

        await client.WriteAsync(new ReportBrightnessRequest(1, [new MonitorBrightness { Instance = MonitorBoardTests.Dell.Instance, Brightness = 0 }]));

        (await client.ReadAsync()).ShouldBe(new OkReply(1));
        _monitors.Status(displayOn: true).ShouldHaveSingleItem().WattsNow.ShouldBe(MonitorBoardTests.DellAt(0), 1e-9);
    }

    [Fact]
    public async Task A_client_that_sends_an_oversized_line_is_told_why_and_disconnected()
    {
        var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await using var client = new MessageChannel(pipe);
        var junk = new byte[PipeProtocol.MaxMessageBytes + 100];
        Array.Fill(junk, (byte)'x');
        var writing = Task.Run(async () =>
        {
            try
            {
                await pipe.WriteAsync(junk);
            }
            catch (IOException)
            {
                // the server hangs up before reading it all
            }
        });

        (await client.ReadAsync()).ShouldBeOfType<ErrorReply>().Id.ShouldBeNull();
        (await client.ReadAsync()).ShouldBeNull();
        await writing;
    }

    [Fact]
    public void No_other_process_can_serve_the_name_while_the_service_does()
        => Should.Throw<UnauthorizedAccessException>(() => PipeServer.Create(_name, first: true).Dispose());

    [Fact]
    public void The_acl_denies_the_network_and_lets_users_read_and_write_but_not_serve()
    {
        var rules = PipeServer.Security().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        rules.ShouldContain(r => r.IdentityReference == new SecurityIdentifier(WellKnownSidType.NetworkSid, null) && r.AccessControlType == AccessControlType.Deny);
        var users = rules.Single(r => r.IdentityReference == new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));
        users.PipeAccessRights.HasFlag(PipeAccessRights.ReadData | PipeAccessRights.WriteData).ShouldBeTrue();
        users.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance).ShouldBeFalse();
    }

    private async Task<MessageChannel> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        return new MessageChannel(pipe);
    }
}
