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

/// <summary>One PC's household worker on loopback, with the App at its screen reading the notices when there is one. With
/// <c>autoAnswer</c>, the App presses Join, Approve or Codes match on every prompt as it comes.</summary>
internal sealed class WorkerPc : IAsyncDisposable
{
    private readonly TestDatabase _database = new();
    private readonly ChannelReader<HouseholdNotice>? _app;
    private readonly RelayClient _client;
    private readonly Task? _answering;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>A PC on a fake server, as most tests have it.</summary>
    public WorkerPc(string name, ChassisKind kind, FakeNetwork network, FakeRelay relay, TimeProvider clock, bool appAtTheScreen)
        : this(name, kind, network, new RelayClient(FakeRelay.Endpoint, clock, relay), clock, appAtTheScreen, autoAnswer: false,
            codeWait: TimeSpan.FromMilliseconds(5))
    {
    }

    /// <param name="client">The server, fake or real; the PC disposes it.</param>
    /// <param name="codeWait">How long a pairing by code waits between looks at a meeting slot.</param>
    public WorkerPc(
        string name, ChassisKind kind, FakeNetwork network, RelayClient client, TimeProvider clock, bool appAtTheScreen, bool autoAnswer,
        TimeSpan codeWait)
    {
        Board.Publish(ServiceSettings.Default with { Profile = ServiceSettings.Default.Profile with { Chassis = kind } });
        var notices = new NoticeHub(() => 1);
        _client = client;
        var environment = new HouseholdEnvironment(
            network.Join(), Category, _client, IPAddress.Loopback, () => name, RunLoop: false,
            BrowseTime: TimeSpan.Zero, Timeouts: new PairingTimeouts(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)),
            CodeWait: (_, cancel) => Task.Delay(codeWait, cancel));
        Worker = new HouseholdWorker(_database.Db, Board, notices, environment, clock, NullLogger<HouseholdWorker>.Instance);
        Household = new HouseholdRepository(_database.Db);
        if (!appAtTheScreen) return;
        var screen = notices.Subscribe(1);
        if (!autoAnswer)
        {
            _app = screen;
            return;
        }
        var seen = Channel.CreateUnbounded<HouseholdNotice>();
        _app = seen.Reader;
        _answering = AnswerAsync(screen, seen.Writer, _stop.Token);
    }

    /// <summary>Pairs two PCs on the network, the second's user pressing Join and the first's Codes match.</summary>
    public static async Task Pair(WorkerPc adder, WorkerPc joiner)
    {
        await adder.Send<FoundPcsReply>(new BrowsePcsRequest(90));
        (await adder.Send<HouseholdReply>(new AddPcRequest(91, joiner.Worker.InstanceId))).Ok.ShouldBeTrue();
        var prompt = await joiner.Next(NoticeKind.JoinPrompt);
        await joiner.Send<HouseholdReply>(new AnswerPromptRequest(92, prompt.PromptId!, true));   // answered already when automatic
        var confirm = await adder.Next(NoticeKind.ConfirmCode);
        confirm.ComparisonCode.ShouldBe(prompt.ComparisonCode);
        await adder.Send<HouseholdReply>(new AnswerPromptRequest(93, confirm.PromptId!, true));
        (await adder.Next(NoticeKind.PairingProgress, text => text.EndsWith("joined your household.", StringComparison.Ordinal))).ShouldNotBeNull();
        (await joiner.Next(NoticeKind.Info, text => text.StartsWith("This PC joined", StringComparison.Ordinal))).ShouldNotBeNull();   // it enters once told
        await adder.Worker.Running;
        await joiner.Worker.Running;
    }

    public StatusBoard Board { get; } = new();

    /// <summary>The kind of network this PC is on, Private until the test says otherwise.</summary>
    public FakeNetworkCategory Category { get; } = new();

    public HouseholdWorker Worker { get; }

    public HouseholdRepository Household { get; }

    /// <summary>This PC's hour totals, which its household rows are built from.</summary>
    public AggregateRepository Aggregates => new(_database.Db);

    public async Task<T> Send<T>(PipeRequest request) where T : PipeMessage =>
        (await Worker.HandleAsync(request, CancellationToken.None)).ShouldBeOfType<T>();

    /// <summary>The next notice of <paramref name="kind"/> the App gets, passing over others.</summary>
    public async Task<HouseholdNotice> Next(NoticeKind kind, Func<string, bool>? matching = null)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var notice = await _app!.ReadAsync(limit.Token);
            if (notice.Kind == kind && (matching?.Invoke(notice.Text) ?? true)) return notice;
        }
    }

    /// <summary>Every notice the App has been given and the test hasn't read yet.</summary>
    public List<HouseholdNotice> Drain()
    {
        var notices = new List<HouseholdNotice>();
        while (_app!.TryRead(out var notice)) notices.Add(notice);
        return notices;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_answering is not null) await _answering;
        await Worker.StopAsync(CancellationToken.None);
        Worker.Dispose();
        _client.Dispose();
        _database.Dispose();
        _stop.Dispose();
    }

    /// <summary>The App at the screen pressing Join or Approve at once, and passing every notice on for the test to read.</summary>
    private async Task AnswerAsync(ChannelReader<HouseholdNotice> screen, ChannelWriter<HouseholdNotice> seen, CancellationToken stop)
    {
        try
        {
            await foreach (var notice in screen.ReadAllAsync(stop))
            {
                if (notice is { Kind: NoticeKind.JoinPrompt or NoticeKind.ApprovePrompt or NoticeKind.ConfirmCode, PromptId: { } prompt })
                {
                    await Worker.HandleAsync(new AnswerPromptRequest(0, prompt, true), stop);
                }
                await seen.WriteAsync(notice, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }
}
