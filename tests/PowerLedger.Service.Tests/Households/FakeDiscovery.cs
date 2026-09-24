using System.Net;
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

    public Task<IReadOnlyList<FoundService>> BrowseAsync(TimeSpan timeout, CancellationToken cancel = default) =>
        Task.FromResult(network.Announced);

    public void Dispose() => Unregister();
}

/// <summary>A network whose category the test sets, raising <see cref="Changed"/> as it does.</summary>
internal sealed class FakeNetworkCategory(bool isPrivate = true) : INetworkCategory
{
    private bool _isPrivate = isPrivate;

    public bool IsPrivate
    {
        get => Volatile.Read(ref _isPrivate);
        set
        {
            Volatile.Write(ref _isPrivate, value);
            Changed?.Invoke();
        }
    }

    public event Action? Changed;
}
