using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>0.10.7: Aero by request, against a fake data server.</summary>
public class AeroAccessTests
{
    private readonly FakeUiSettings _ui = new() { Current = UiPreferences.Default with { AeroApproved = false } };
    private readonly FakeAeroServer _server = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly List<string> _copied = [];
    private int _approved;

    private AeroAccess Access()
    {
        var access = new AeroAccess(_ui, _server, UiThreads.Inline, _clock, () => "Study PC", _copied.Add, () => "AERO-ABC12");
        access.Approved += () => _approved++;
        return access;
    }

    [Fact]
    public void Request_makes_the_id_once_keeps_it_and_sends_it_with_the_pc_name()
    {
        var access = Access();
        access.HasRequested.ShouldBeFalse();

        access.Request.Execute(null);
        access.Request.Execute(null);

        _ui.Current.AeroRequestId.ShouldBe("AERO-ABC12");
        _server.Requests.ShouldBe(["AERO-ABC12 Study PC", "AERO-ABC12 Study PC"]);
        access.RequestText.ShouldBe("Your request ID: AERO-ABC12. Send it to the PowerLedger owner.");
        access.Status.ShouldBe("Pending");
        access.CopyId.Execute(null);
        _copied.ShouldBe(["AERO-ABC12"]);
    }

    [Fact]
    public void Nothing_is_checked_before_a_request()
    {
        Access().Start();

        _server.StatusChecks.ShouldBe(0);
    }

    [Fact]
    public void Approval_unlocks_and_says_so_once_without_switching_the_look()
    {
        _ui.Current = _ui.Current with { AeroRequestId = "AERO-ABC12", Look = Look.Classic };
        var access = Access();
        _server.State = AeroRequestState.Approved;

        access.Check();
        access.Check();

        (_ui.Current.AeroApproved, _ui.Current.Look, access.IsLocked, access.Status).ShouldBe((true, Look.Classic, false, "Approved"));
        _approved.ShouldBe(1);
    }

    [Fact]
    public void A_revoke_locks_and_leaves_aero_for_midnight()
    {
        _ui.Current = _ui.Current with { AeroRequestId = "AERO-ABC12", AeroApproved = true, Look = Look.Aero };
        var access = Access();
        _server.State = AeroRequestState.Revoked;

        access.Check();

        (_ui.Current.AeroApproved, _ui.Current.Look, access.Status).ShouldBe((false, Look.Midnight, "Not approved"));
    }

    [Fact]
    public void Offline_or_a_server_error_keeps_the_last_known_state()
    {
        _ui.Current = _ui.Current with { AeroRequestId = "AERO-ABC12", AeroApproved = true, Look = Look.Aero };
        var access = Access();
        _server.State = null;

        access.Check();

        (_ui.Current.AeroApproved, _ui.Current.Look, access.Status).ShouldBe((true, Look.Aero, "Approved"));
        _ui.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void A_request_the_server_never_heard_of_is_sent_again()
    {
        _ui.Current = _ui.Current with { AeroRequestId = "AERO-ABC12" };
        var access = Access();
        _server.State = AeroRequestState.None;

        access.Check();

        _server.Requests.ShouldBe(["AERO-ABC12 Study PC"]);
        access.Status.ShouldBe("Pending");
    }

    [Fact]
    public void While_pending_it_checks_every_five_minutes_and_stops_once_approved()
    {
        _ui.Current = _ui.Current with { AeroRequestId = "AERO-ABC12" };
        var access = Access();
        access.Start();
        _server.StatusChecks.ShouldBe(1);

        _clock.Advance(AeroAccess.CheckEvery);
        _server.StatusChecks.ShouldBe(2);

        _server.State = AeroRequestState.Approved;
        _clock.Advance(AeroAccess.CheckEvery);
        _clock.Advance(AeroAccess.CheckEvery);
        _server.StatusChecks.ShouldBe(3);
        _approved.ShouldBe(1);
    }

    [Fact]
    public void Settings_checks_when_it_shows_and_picking_aero_while_locked_says_how_to_ask()
    {
        _ui.Current = _ui.Current with { AeroRequestId = "AERO-ABC12", Look = Look.Midnight };
        var access = Access();
        var settings = new SettingsViewModel(
            new FakeLink(), new FakeMachineHistory(), _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("en-US"), "USD",
            aero: access);

        settings.Look = Look.Aero;
        (settings.Look, settings.AeroLocked, settings.AppMessage).ShouldBe((Look.Midnight, true, AeroAccess.Locked));

        _server.State = AeroRequestState.Approved;
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        settings.Show();
        settings.Hide();

        _server.StatusChecks.ShouldBe(1);
        settings.AeroLocked.ShouldBeFalse();
        raised.ShouldContain(nameof(SettingsViewModel.AeroLocked));
        settings.Look = Look.Aero;
        settings.Look.ShouldBe(Look.Aero);
    }

    [Theory]
    [InlineData("AERO-ABC12", true)]
    [InlineData("AERO-0Z9YX", true)]
    [InlineData("AERO-ABCDU", false)]
    [InlineData("AERO-ABC1", false)]
    [InlineData("aero-abc12", false)]
    [InlineData(null, false)]
    public void An_id_is_aero_and_five_crockford_characters(string? id, bool valid) => AeroAccess.IsId(id).ShouldBe(valid);

    [Fact]
    public void New_ids_are_ids()
    {
        for (var i = 0; i < 100; i++) AeroAccess.IsId(AeroAccess.NewId()).ShouldBeTrue();
    }

    [Theory]
    [InlineData("none", "None")]
    [InlineData("pending", "Pending")]
    [InlineData("approved", "Approved")]
    [InlineData("revoked", "Revoked")]
    public void The_server_states_parse(string text, string state) => HttpAeroServer.Parse(text).ShouldBe(Enum.Parse<AeroRequestState>(state));

    [Fact]
    public void An_unknown_server_state_is_no_answer() => HttpAeroServer.Parse("maybe").ShouldBeNull();

    private sealed class FakeAeroServer : IAeroServer
    {
        public AeroRequestState? State { get; set; } = AeroRequestState.Pending;

        public List<string> Requests { get; } = [];

        public int StatusChecks { get; private set; }

        public Task<AeroRequestState?> RequestAsync(string id, string name, CancellationToken cancel = default)
        {
            Requests.Add($"{id} {name}");
            return Task.FromResult<AeroRequestState?>(AeroRequestState.Pending);
        }

        public Task<AeroRequestState?> StatusAsync(string id, CancellationToken cancel = default)
        {
            StatusChecks++;
            return Task.FromResult(State);
        }
    }
}
