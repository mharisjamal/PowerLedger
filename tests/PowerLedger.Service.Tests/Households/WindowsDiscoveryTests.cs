using Microsoft.Extensions.Logging.Abstractions;
using PowerLedger.Service.Households;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Windows' own DNS-SD on this PC's network: it announces a test instance, finds it by browsing and resolves it.</summary>
[Trait("Category", "Hardware")]
public sealed class WindowsDiscoveryTests
{
    [Fact]
    public async Task An_announced_instance_is_found_and_resolved_with_its_port_and_text()
    {
        var instance = "t" + Guid.NewGuid().ToString("N")[..15];
        using var discovery = new WindowsDiscovery(NullLogger.Instance);
        discovery.Register(instance, 50999, new Dictionary<string, string> { ["v"] = "1", ["name"] = "Test PC", ["tag"] = "0011223344556677" });
        try
        {
            FoundService? found = null;
            for (var attempt = 0; attempt < 5 && found is null; attempt++)
            {
                found = (await discovery.BrowseAsync(TimeSpan.FromSeconds(3))).FirstOrDefault(service => service.Instance == instance);
            }

            found.ShouldNotBeNull();
            found.Port.ShouldBe(50999);
            found.Address.ShouldNotBeNull();
            found.Txt["name"].ShouldBe("Test PC");
            found.Txt["tag"].ShouldBe("0011223344556677");
        }
        finally
        {
            discovery.Unregister();
        }
    }

    [Fact]
    public void The_network_category_can_be_read()
    {
        using var category = new WindowsNetworkCategory(NullLogger.Instance);

        Should.NotThrow(() => category.IsPrivate);
    }
}
