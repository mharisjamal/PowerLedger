using Microsoft.Extensions.Logging.Abstractions;
using PowerLedger.Service.Households;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>When this PC is announced on the network (households design §3): only while the user lets it be found and the
/// network is Private.</summary>
public sealed class AnnouncerTests
{
    private static readonly Dictionary<string, string> Txt = new() { ["v"] = "1", ["name"] = "Desktop-7", ["tag"] = "" };

    private readonly FakeNetwork _network = new();
    private readonly FakeNetworkCategory _category = new(isPrivate: true);

    [Fact]
    public void It_is_announced_while_the_user_lets_it_be_found_on_a_private_network()
    {
        var discovery = _network.Join();
        var announcer = new Announcer(discovery, _category, NullLogger.Instance);

        announcer.Update(new Announcement("a1", 50123, Txt)).ShouldBeTrue();

        announcer.Announced.ShouldBeTrue();
        var found = _network.Announced.ShouldHaveSingleItem();
        (found.Instance, found.Port, found.Txt["name"]).ShouldBe(("a1", 50123, "Desktop-7"));
    }

    [Fact]
    public void Nothing_is_announced_on_a_public_network_and_it_is_withdrawn_when_the_network_turns_public()
    {
        var discovery = _network.Join();
        var announcer = new Announcer(discovery, _category, NullLogger.Instance);
        _category.IsPrivate = false;

        announcer.Update(new Announcement("a1", 50123, Txt)).ShouldBeFalse();
        _network.Announced.ShouldBeEmpty();

        _category.IsPrivate = true;
        _network.Announced.ShouldHaveSingleItem();              // the network changing checks again

        _category.IsPrivate = false;
        _network.Announced.ShouldBeEmpty();
        announcer.Announced.ShouldBeFalse();
    }

    [Fact]
    public void Turning_the_tick_off_withdraws_it_and_only_a_change_registers_again()
    {
        var discovery = _network.Join();
        var announcer = new Announcer(discovery, _category, NullLogger.Instance);
        announcer.Update(new Announcement("a1", 50123, Txt));

        announcer.Update(new Announcement("a1", 50123, new Dictionary<string, string>(Txt)));
        discovery.Registrations.ShouldBe(1);

        announcer.Update(new Announcement("a1", 50123, new Dictionary<string, string>(Txt) { ["name"] = "Kitchen PC" }));
        discovery.Registrations.ShouldBe(2);
        _network.Announced.ShouldHaveSingleItem().Txt["name"].ShouldBe("Kitchen PC");

        announcer.Update(null).ShouldBeFalse();
        _network.Announced.ShouldBeEmpty();
        _category.IsPrivate = true;
        _network.Announced.ShouldBeEmpty();                      // a network change doesn't bring back what the user turned off
    }

    [Fact]
    public void A_registration_that_fails_is_tried_again_at_the_next_update()
    {
        var discovery = new FailingDiscovery();
        var announcer = new Announcer(discovery, _category, NullLogger.Instance);

        announcer.Update(new Announcement("a1", 50123, Txt)).ShouldBeFalse();
        discovery.Fail = false;
        announcer.Update(new Announcement("a1", 50123, Txt)).ShouldBeTrue();
        discovery.Registered.ShouldBeTrue();
    }

    private sealed class FailingDiscovery : IDiscovery
    {
        public bool Fail { get; set; } = true;

        public bool Registered { get; private set; }

        public void Register(string instance, int port, IReadOnlyDictionary<string, string> txt)
        {
            if (Fail) throw new InvalidOperationException("DnsServiceRegister failed with 87.");
            Registered = true;
        }

        public void Unregister() => Registered = false;

        public Task<IReadOnlyList<FoundService>> BrowseAsync(TimeSpan timeout, CancellationToken cancel = default) =>
            Task.FromResult<IReadOnlyList<FoundService>>([]);

        public void Dispose()
        {
        }
    }
}
