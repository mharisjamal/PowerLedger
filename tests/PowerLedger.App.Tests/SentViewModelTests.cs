using System.Globalization;
using System.IO;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class SentViewModelTests : IDisposable
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-sent-{Guid.NewGuid():N}");
    private readonly FakeLink _link = new();
    private readonly List<string> _opened = [];

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private SentViewModel Model() => new(_link, UiThreads.Inline, _folder, English, _opened.Add);

    [Fact]
    public void A_missing_folder_is_the_empty_state()
    {
        var model = Model();

        model.Rows.ShouldBeEmpty();
        model.Empty.ShouldBe("Nothing has been sent from this PC.");
    }

    [Fact]
    public void An_existing_but_empty_folder_is_also_the_empty_state()
    {
        Directory.CreateDirectory(_folder);
        var model = Model();

        model.Empty.ShouldBe("Nothing has been sent from this PC.");
    }

    [Fact]
    public void Files_list_newest_first_with_their_day_and_size_rounded_up()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "2026-09-22.json.gz"), new byte[40 * 1024]);
        File.WriteAllBytes(Path.Combine(_folder, "2026-09-24.json.gz"), new byte[1]);

        var model = Model();

        model.Empty.ShouldBeNull();
        model.Rows.Select(r => r.Day).ShouldBe(new[] { "24 Sep 2026", "22 Sep 2026" });
        model.Rows[0].Size.ShouldBe("1 KB");
        model.Rows[1].Size.ShouldBe("40 KB");
    }

    [Fact]
    public void Opening_a_row_hands_its_path_on()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "2026-09-24.json.gz");
        File.WriteAllBytes(path, [1, 2, 3]);
        var model = Model();

        model.Open(model.Rows.Single());

        _opened.ShouldBe(new[] { path });
    }

    [Fact]
    public async Task Send_now_sends_and_refreshes_the_list()
    {
        _link.Connect(true);
        _link.SharingAnswer = new SharingOutcome(true, "Sent.");
        Directory.CreateDirectory(_folder);
        var model = Model();
        model.Empty.ShouldBe("Nothing has been sent from this PC.");

        File.WriteAllBytes(Path.Combine(_folder, "2026-09-24.json.gz"), [1]);   // appears only once refreshed
        await model.SendNowAsync();

        _link.SharingRequests.ShouldBe(new object[] { "sendNow" });
        model.Message.ShouldBe("Sent.");
        model.Empty.ShouldBeNull();
        model.Rows.Single().Day.ShouldBe("24 Sep 2026");
    }

    /// <summary>The service can take seconds over a sharing request (finding 6: they are serialised, so a second one
    /// waits its turn); Send now must show that and refuse a second press meanwhile.</summary>
    [Fact]
    public async Task Send_now_is_busy_while_on_its_way_to_the_service()
    {
        _link.Connect(true);
        var gate = new TaskCompletionSource<SharingOutcome>();
        _link.SharingGate = gate;
        var model = Model();

        model.SendNow.Execute(null);

        model.Busy.ShouldBeTrue();
        model.SendNow.CanExecute(null).ShouldBeFalse();

        gate.SetResult(new SharingOutcome(true, "Sent."));

        await WaitFor.True(() => !model.Busy);
        model.SendNow.CanExecute(null).ShouldBeTrue();
    }
}
