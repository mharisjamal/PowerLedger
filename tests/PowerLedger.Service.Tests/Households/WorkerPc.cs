using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLedger.Contracts;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Lan;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>One PC's household worker on loopback, with the App at its screen reading the notices when there is one.</summary>
internal sealed class WorkerPc : IAsyncDisposable
{
    private readonly TestDatabase _database = new();
    private readonly ChannelReader<HouseholdNotice>? _app;
    private readonly RelayClient _client;

    public WorkerPc(string name, ChassisKind kind, FakeNetwork network, FakeRelay relay, TimeProvider clock, bool appAtTheScreen)
    {
        Board.Publish(ServiceSettings.Default with { Profile = ServiceSettings.Default.Profile with { Chassis = kind } });
        var notices = new NoticeHub(() => 1);
        if (appAtTheScreen) _app = notices.Subscribe(1);
        _client = new RelayClient(FakeRelay.Endpoint, clock, relay);
        var environment = new HouseholdEnvironment(
            network.Join(), new FakeNetworkCategory(), _client, IPAddress.Loopback, () => name, RunLoop: false,
            BrowseTime: TimeSpan.Zero, Timeouts: new PairingTimeouts(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)),
            CodeWait: (_, cancel) => Task.Delay(5, cancel));
        Worker = new HouseholdWorker(_database.Db, Board, notices, environment, clock, NullLogger<HouseholdWorker>.Instance);
        Household = new HouseholdRepository(_database.Db);
    }

    /// <summary>Pairs two PCs on the network, the second's user pressing Join.</summary>
    public static async Task Pair(WorkerPc adder, WorkerPc joiner)
    {
        await adder.Send<FoundPcsReply>(new BrowsePcsRequest(90));
        (await adder.Send<HouseholdReply>(new AddPcRequest(91, joiner.Worker.InstanceId))).Ok.ShouldBeTrue();
        var prompt = await joiner.Next(NoticeKind.JoinPrompt);
        await joiner.Send<HouseholdReply>(new AnswerPromptRequest(92, prompt.PromptId!, true));
        (await adder.Next(NoticeKind.PairingProgress, text => text.EndsWith("joined your household.", StringComparison.Ordinal))).ShouldNotBeNull();
        await adder.Worker.Running;
        await joiner.Worker.Running;
    }

    public StatusBoard Board { get; } = new();

    public HouseholdWorker Worker { get; }

    public HouseholdRepository Household { get; }

    public async Task<T> Send<T>(PipeRequest request) where T : PipeMessage =>
        (await Worker.HandleAsync(request, CancellationToken.None)).ShouldBeOfType<T>();

    /// <summary>The next notice of <paramref name="kind"/> the App gets, passing over others.</summary>
    public async Task<HouseholdNotice> Next(NoticeKind kind, Func<string, bool>? matching = null)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var notice = await _app!.ReadAsync(limit.Token);
            if (notice.Kind == kind && (matching?.Invoke(notice.Text) ?? true)) return notice;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Worker.StopAsync(CancellationToken.None);
        Worker.Dispose();
        _client.Dispose();
        _database.Dispose();
    }
}
