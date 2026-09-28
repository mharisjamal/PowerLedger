using System.Net;
using PowerLedger.Contracts;
using PowerLedger.Service.Households;

namespace PowerLedger.Service.Tests;

/// <summary>A network of fake DNS-SD announcements shared by the services in one test, each on loopback.</summary>
internal sealed class FakeNetwork
{
    private readonly Dictionary<FakeDiscovery, FoundService> _announced = [];

    public IReadOnlyList<FoundService> Announced
    {
        get
        {
            lock (_announced) return [.. _announced.Values];
        }
    }

    public FakeDiscovery Join() => new(this);

    internal void Announce(FakeDiscovery owner, FoundService service)
    {
        lock (_announced) _announced[owner] = service;
    }

    internal void Withdraw(FakeDiscovery owner)
    {
        lock (_announced) _announced.Remove(owner);
    }
}

/// <summary>One PC's view of a <see cref="FakeNetwork"/>: what it registers, every PC on it can browse.</summary>
internal sealed class FakeDiscovery(FakeNetwork network) : IDiscovery
{
    private int _registrations;

    /// <summary>How many times <see cref="Register"/> was called.</summary>
    public int Registrations => Volatile.Read(ref _registrations);

    public void Register(string instance, int port, IReadOnlyDictionary<string, string> txt)
    {
        Interlocked.Increment(ref _registrations);
        network.Announce(this, new FoundService(instance, "localhost", IPAddress.Loopback, port, new Dictionary<string, string>(txt)));
    }

    public void Unregister() => network.Withdraw(this);

    /// <summary>What browsing throws instead of answering, when set.</summary>
    public Exception? BrowseFails { get; set; }

    public Task<IReadOnlyList<FoundService>> BrowseAsync(TimeSpan timeout, CancellationToken cancel = default) =>
        BrowseFails is { } error ? Task.FromException<IReadOnlyList<FoundService>>(error) : Task.FromResult(network.Announced);

    public void Dispose() => Unregister();
}

/// <summary>A network whose category the test sets, raising <see cref="Changed"/> as it does.</summary>
internal sealed class FakeNetworkCategory(bool isPrivate = true) : INetworkCategory
{
    private int _kind = (int)Of(isPrivate);

    public NetworkCategory Kind
    {
        get => (NetworkCategory)Volatile.Read(ref _kind);
        set
        {
            Volatile.Write(ref _kind, (int)value);
            Changed?.Invoke();
        }
    }

    /// <summary>True for a Private network, false for a Public one, as most tests have it.</summary>
    public bool IsPrivate
    {
        get => Kind == NetworkCategory.Private;
        set => Kind = Of(value);
    }

    public event Action? Changed;

    /// <summary>Changes the category without raising <see cref="Changed"/>, as Windows says nothing when only the category of a
    /// network changes.</summary>
    public void Quietly(bool isPrivate) => Volatile.Write(ref _kind, (int)Of(isPrivate));

    private static NetworkCategory Of(bool isPrivate) => isPrivate ? NetworkCategory.Private : NetworkCategory.Public;
}
