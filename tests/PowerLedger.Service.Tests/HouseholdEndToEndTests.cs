using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>
/// Households end to end (plan L2): two or three PCs' household stacks in one process, each with its own database and its
/// own listener on loopback, discovery faked so each finds the others' real listeners, the App at each screen pressing
/// Join as prompts come, against the real Worker from <c>server/</c> run by <see cref="WorkerFixture"/>. Pairing on the
/// network and by code, rows both ways on the network and through the Worker, and a removal changing the key.
/// </summary>
[Trait("Category", "Worker")]
public sealed class HouseholdEndToEndTests(WorkerFixture worker) : IClassFixture<WorkerFixture>, IAsyncLifetime
{
    private static int _addresses;

    private readonly FakeNetwork _network = new();
    private readonly List<WorkerPc> _pcs = [];
    private readonly List<IDisposable> _disposables = [];
    private readonly Dictionary<WorkerPc, DeviceKeys> _keys = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var pc in _pcs) await pc.DisposeAsync();
        foreach (var disposable in _disposables) disposable.Dispose();
    }

    [Fact]
    public async Task Pairing_on_the_network_shows_one_code_on_both_screens_and_the_worker_takes_the_joiners_proof()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);
        await desktop.Send<FoundPcsReply>(new BrowsePcsRequest(1));

        (await desktop.Send<HouseholdReply>(new AddPcRequest(2, laptop.Worker.InstanceId))).Ok.ShouldBeTrue();

        var shown = await desktop.Next(NoticeKind.ConfirmCode);                 // both users check the one code
        var asked = await laptop.Next(NoticeKind.JoinPrompt);
        asked.ComparisonCode.ShouldNotBeNull().ShouldMatch("^[0-9]{3} [0-9]{3}$");
        asked.ComparisonCode.ShouldBe(shown.ComparisonCode);
        (await desktop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("Laptop-2 joined your household.");
        var household = desktop.Worker.Store.HouseholdId.ShouldNotBeNull();
        laptop.Worker.Store.HouseholdId.ShouldBe(household);

        await desktop.Worker.RunOnceAsync(CancellationToken.None);               // the household and the laptop, on the Worker

        desktop.Worker.Store.Pending.ShouldBeEmpty();
        desktop.Board.Household!.Problem.ShouldBeNull();
        var members = await MembersAsync(desktop, household);
        members.Where(member => member.Removed is null).Select(member => member.Device)
            .ShouldBe([desktop.Worker.DeviceId, laptop.Worker.DeviceId], ignoreOrder: true);
        await laptop.Worker.RunOnceAsync(CancellationToken.None);
        (laptop.Board.Household!.Problem, laptop.Worker.Store.RelayConfirmed).ShouldBe(((string?)null, true));
    }

    [Fact]
    public async Task Hour_rows_flow_both_ways_on_the_network_and_through_the_worker()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);
        var now = DateTimeOffset.UtcNow;
        Hours(desktop, now, energyWh: 40);                                        // built into rows as each joins
        Hours(laptop, now, energyWh: 15);
        await WorkerPc.Pair(desktop, laptop);

        await desktop.Worker.RunOnceAsync(CancellationToken.None);               // on the network, both ways

        HoursOf(desktop, laptop).ShouldBe(3);
        HoursOf(laptop, desktop).ShouldBe(3);
        desktop.Household.Row(laptop.Worker.DeviceId, Hour(now, 1)).ShouldNotBeNull().EnergyWh.ShouldBe(15);

        await desktop.Send<HouseholdReply>(new SetDiscoverableRequest(3, false));   // off the network: the Worker only
        await laptop.Send<HouseholdReply>(new SetDiscoverableRequest(4, false));
        var changed = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        desktop.Household.Upsert([Row(desktop, now, 4, energyWh: 44, changed)]);
        laptop.Household.Upsert([Row(laptop, now, 5, energyWh: 55, changed)]);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        await laptop.Worker.RunOnceAsync(CancellationToken.None);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);

        laptop.Household.Row(desktop.Worker.DeviceId, Hour(now, 4)).ShouldNotBeNull().EnergyWh.ShouldBe(44);
        desktop.Household.Row(laptop.Worker.DeviceId, Hour(now, 5)).ShouldNotBeNull().EnergyWh.ShouldBe(55);
        (desktop.Board.Household!.Problem, laptop.Board.Household!.Problem).ShouldBe(((string?)null, (string?)null));
        desktop.Board.Household.Members.Single(member => member.DeviceId == laptop.Worker.DeviceId).LastSyncedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Removing_a_pc_changes_the_key_and_the_removed_pc_is_told_so_and_cant_open_what_comes_after()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);
        var study = await Start("Study PC", ChassisKind.Desktop);
        await WorkerPc.Pair(desktop, laptop);
        await WorkerPc.Pair(desktop, study);
        foreach (var pc in new[] { desktop, laptop, study })
        {
            await pc.Send<HouseholdReply>(new SetDiscoverableRequest(5, false));
            await pc.Worker.RunOnceAsync(CancellationToken.None);
        }
        var household = desktop.Worker.Store.HouseholdId!;
        var oldKey = study.Worker.Store.CurrentKey.ShouldNotBeNull();

        (await desktop.Send<HouseholdReply>(new RemovePcRequest(6, study.Worker.DeviceId))).Ok.ShouldBeTrue();
        var now = DateTimeOffset.UtcNow;
        desktop.Household.Upsert([Row(desktop, now, 7, energyWh: 70, DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds())]);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);               // the removal and the new key, then the row
        await laptop.Worker.RunOnceAsync(CancellationToken.None);
        await study.Worker.RunOnceAsync(CancellationToken.None);

        desktop.Worker.Store.Epoch.ShouldBe(2);
        laptop.Worker.Store.Epoch.ShouldBe(2);
        laptop.Worker.Store.CurrentKey.ShouldBe(desktop.Worker.Store.CurrentKey);
        laptop.Household.Row(desktop.Worker.DeviceId, Hour(now, 7)).ShouldNotBeNull().EnergyWh.ShouldBe(70);
        (await study.Next(NoticeKind.Info, text => text.Contains("removed", StringComparison.Ordinal))).Text
            .ShouldBe("This PC was removed from the household.");
        study.Worker.Store.HouseholdId.ShouldBeNull();
        study.Household.Row(desktop.Worker.DeviceId, Hour(now, 7)).ShouldBeNull();
        (await AsPc(study, client => client.MembersAsync(Keys(study), household, CancellationToken.None))).Status.ShouldBe(410);

        // What the desktop sealed after the removal, as the Worker hands it to a member: the study's old key can't open it.
        var page = (await AsPc(laptop, client => client.BatchesAsync(Keys(laptop), household, 0, 100, CancellationToken.None))).Value!;
        var after = page.Items.Last(item => item.Device == desktop.Worker.DeviceId);
        after.Epoch.ShouldBe(2);
        var aad = HouseholdCrypto.BatchAad(household, after.Device, after.Epoch, after.Seq);
        Should.Throw<CryptographicException>(() => HouseholdCrypto.Open(oldKey, Bytes(after.Body), aad));
        HouseholdCrypto.Open(laptop.Worker.Store.KeyFor(2)!, Bytes(after.Body), aad).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Pairing_by_code_goes_through_the_workers_meeting_slots_up_to_joined()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);

        var code = (await desktop.Send<HouseholdReply>(new StartCodePairingRequest(1))).Code.ShouldNotBeNull();
        (await laptop.Send<HouseholdReply>(new JoinByCodeRequest(2, code))).Ok.ShouldBeTrue();

        (await laptop.Next(NoticeKind.JoinPrompt)).ComparisonCode.ShouldBeNull();
        (await laptop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("This PC joined Desktop-7's household.");
        (await desktop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("Laptop-2 joined your household.");   // after the joined slot
        var household = desktop.Worker.Store.HouseholdId.ShouldNotBeNull();
        laptop.Worker.Store.HouseholdId.ShouldBe(household);
        laptop.Worker.Store.CurrentKey.ShouldBe(desktop.Worker.Store.CurrentKey);

        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        desktop.Worker.Store.Pending.ShouldBeEmpty();
        (await MembersAsync(desktop, household)).Select(member => member.Device).ShouldContain(laptop.Worker.DeviceId);
        await laptop.Worker.RunOnceAsync(CancellationToken.None);
        laptop.Board.Household!.Problem.ShouldBeNull();
    }

    /// <summary>A PC whose requests come from an address of its own, so the Worker's 60-a-minute address limit counts each
    /// PC apart, as it would for PCs in different places.</summary>
    private async Task<WorkerPc> Start(string name, ChassisKind kind)
    {
        var number = Interlocked.Increment(ref _addresses);
        var handler = new AddressHandler($"10.20.{number / 250}.{number % 250 + 1}") { InnerHandler = new SocketsHttpHandler() };
        _disposables.Add(handler);
        var pc = new WorkerPc(name, kind, _network, new RelayClient(worker.Endpoint, TimeProvider.System, handler), TimeProvider.System,
            appAtTheScreen: true, autoAnswer: true, codeWait: TimeSpan.FromMilliseconds(500));
        _pcs.Add(pc);
        await pc.Worker.StartAsync(CancellationToken.None);
        return pc;
    }

    /// <summary>The Worker's member list, asked as <paramref name="pc"/>.</summary>
    private async Task<List<ServerMember>> MembersAsync(WorkerPc pc, string household)
    {
        var result = await AsPc(pc, client => client.MembersAsync(Keys(pc), household, CancellationToken.None));
        result.Ok.ShouldBeTrue(result.Problem);
        return result.Value!;
    }

    /// <summary>A request of the test's own, signed as <paramref name="pc"/> and from its address.</summary>
    private async Task<RelayResult<T>> AsPc<T>(WorkerPc pc, Func<RelayClient, Task<RelayResult<T>>> request)
    {
        var number = Interlocked.Increment(ref _addresses);
        using var handler = new AddressHandler($"10.30.{number / 250}.{number % 250 + 1}") { InnerHandler = new SocketsHttpHandler() };
        using var client = new RelayClient(worker.Endpoint, TimeProvider.System, handler);
        return await request(client);
    }

    private DeviceKeys Keys(WorkerPc pc)
    {
        if (!_keys.TryGetValue(pc, out var keys))
        {
            _keys[pc] = keys = pc.Worker.Store.DeviceKeys();
            _disposables.Add(keys);
        }
        return keys;
    }

    private static byte[] Bytes(string base64Url) => Households.Wire.Decode(base64Url)!;

    /// <summary>Three hours of the PC's own in samples_1h, the day before, which its hour rows are built from.</summary>
    private static void Hours(WorkerPc pc, DateTimeOffset now, double energyWh)
    {
        var aggregates = pc.Aggregates;
        for (var hour = 0; hour < 3; hour++)
        {
            aggregates.UpsertHour(new Aggregate(
                DateTimeOffset.FromUnixTimeMilliseconds(Hour(now, hour)), energyWh, energyWh, energyWh, energyWh / 2, energyWh / 4, 1, energyWh / 4 - 1,
                0, 0, 0, 0, 3600, 0, 0, 3600, 3600, 0, 0));
        }
    }

    private static int HoursOf(WorkerPc on, WorkerPc of) =>
        on.Household.RowsBetween(of.Worker.DeviceId, 0, long.MaxValue).Count;

    private static long Hour(DateTimeOffset now, int hour) =>
        (now.AddDays(-1).ToUnixTimeMilliseconds() / 3_600_000 + hour) * 3_600_000;

    private static HouseholdRow Row(WorkerPc pc, DateTimeOffset now, int hour, double energyWh, long changed) => new(
        pc.Worker.DeviceId, Hour(now, hour), energyWh, 1, 1, 1, energyWh - 3, 0, 0, 3600, 0, 0, 3600, 0, 0, 1_000, "GBP", changed);

    /// <summary>Gives every request the address it comes from, as Cloudflare's edge does.</summary>
    private sealed class AddressHandler(string address) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("CF-Connecting-IP");
            request.Headers.Add("CF-Connecting-IP", address);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// The real Worker from <c>server/</c>, run locally for the test class: its D1 migrations applied to a fresh folder of its
/// own, then <c>npx wrangler dev --local</c> on a free port, waited for until it answers, and stopped with every process
/// under it. workerd gets a private temp folder as a plain Windows path, since with a mixed one its SQLite can't make the
/// temp file a larger D1 transaction needs (server/README.md). Nothing leaves the machine.
/// </summary>
/// <remarks>Every process wrangler starts is in a job that ends them all when it closes, as when the fixture is disposed or
/// the test host dies. <see cref="Process.Kill(bool)"/> with the tree isn't enough here: it stopped a few levels down and left
/// wrangler.js, its CLI, both workerd and esbuild running, which also kept the test run's output open, so
/// <c>dotnet test</c> never finished.</remarks>
public sealed class WorkerFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);
    private readonly StringBuilder _output = new();
    private readonly KillOnClose _job = new();
    private Process? _worker;
    private string _folder = "";

    public Uri Endpoint { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var server = ServerFolder();
        if (!Directory.Exists(Path.Combine(server, "node_modules", "wrangler")))
        {
            throw new InvalidOperationException($"The Worker's packages aren't installed: run npm ci in {server} first.");
        }
        _folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"powerledger-worker-{Guid.NewGuid():N}"));
        var state = Path.Combine(_folder, "state");
        Directory.CreateDirectory(Path.Combine(_folder, "temp"));
        Directory.CreateDirectory(state);

        var migrations = Start(server, $"wrangler d1 migrations apply powerledger-index --local --persist-to \"{state}\"");
        if (!migrations.WaitForExit(StartTimeout))
        {
            Stop(migrations);
            throw new TimeoutException($"The Worker's migrations didn't finish:\n{Output}");
        }
        migrations.WaitForExit();                                               // the last of its output
        var applied = migrations.ExitCode == 0;
        migrations.Dispose();
        if (!applied) throw new InvalidOperationException($"The Worker's migrations didn't apply:\n{Output}");

        var port = FreePort();
        Endpoint = new Uri($"http://127.0.0.1:{port}/");
        _worker = Start(server, $"wrangler dev --local --ip 127.0.0.1 --port {port} --persist-to \"{state}\"");
        await WaitUntilAnsweringAsync(_worker);
    }

    public Task DisposeAsync()
    {
        if (_worker is not null) Stop(_worker);
        _job.Dispose();                                                         // ends whatever is left of the tree
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return Task.CompletedTask;
    }

    private string Output
    {
        get
        {
            lock (_output) return _output.ToString();
        }
    }

    /// <summary>A command under <c>npx</c> in <paramref name="server"/>, its output kept for a failure's message. cmd waits on
    /// its input until it is in the job, so nothing it starts can be outside it.</summary>
    private Process Start(string server, string arguments)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /s /c \"set /p go= & npx {arguments}\"")
        {
            WorkingDirectory = server,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var temp = Path.Combine(_folder, "temp");
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        start.Environment["CI"] = "1";                                      // no prompts
        start.Environment["WRANGLER_SEND_METRICS"] = "false";
        start.Environment["NO_COLOR"] = "1";
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, line) => Keep(line.Data);
        process.ErrorDataReceived += (_, line) => Keep(line.Data);
        process.Start();
        _job.Add(process);
        process.StandardInput.Close();                                          // now it may go on
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private void Keep(string? line)
    {
        if (line is null) return;
        lock (_output) _output.AppendLine(line);
    }

    /// <summary>Waits for any answer from the Worker, from an address the tests' own never use.</summary>
    private async Task WaitUntilAnsweringAsync(Process worker)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (worker.HasExited) throw new InvalidOperationException($"The Worker stopped as it started:\n{Output}");
            using var probe = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint, "v1/meetings/00000000000000000000000000000000/adder"));
            probe.Headers.Add("CF-Connecting-IP", "10.255.255.254");
            try
            {
                using var response = await client.SendAsync(probe);
                return;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(250);
            }
        }
        throw new TimeoutException($"The Worker didn't answer within {StartTimeout.TotalSeconds} s:\n{Output}");
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
        }
        process.Dispose();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A Windows job whose processes all end when its handle closes (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE); the
    /// processes they start join it too.</summary>
    private sealed class KillOnClose : IDisposable
    {
        private const int ExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;
        private readonly SafeFileHandle _job;

        public KillOnClose()
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
            if (!SetInformationJobObject(_job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                throw new Win32Exception();
            }
        }

        public void Add(Process process)
        {
            if (!AssignProcessToJobObject(_job, process.Handle)) throw new Win32Exception();
        }

        public void Dispose() => _job.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }

    /// <summary>The repository's <c>server</c> folder, found above the test's own.</summary>
    private static string ServerFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var server = Path.Combine(folder.FullName, "server");
            if (File.Exists(Path.Combine(server, "wrangler.toml"))) return server;
        }
        throw new DirectoryNotFoundException($"No server/wrangler.toml above {AppContext.BaseDirectory}.");
    }
}
