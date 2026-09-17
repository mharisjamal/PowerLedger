using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>
/// The HID layer against Windows itself. The development machine has no power supply that reports over USB, so these
/// check that listing the devices works and that finding none costs nothing; on a machine that has one they read it.
/// </summary>
[Trait("Category", "Hardware")]
public class PsuHardwareTests
{
    [Fact]
    public void The_ups_and_the_power_supply_see_the_same_devices_because_one_layer_lists_them()
    {
        // The UPS source lists collections to read their descriptors and this one to match ids against its table.
        // Both ask the one native layer, so every device a power supply port finds is a collection the UPS's lists.
        var interfaces = new WindowsHid().Interfaces().Select(path => path.Path).ToList();
        var devices = new WindowsHidPort().Find(static (_, _) => true).Select(device => device.Path).ToList();

        devices.ShouldNotBeEmpty();
        devices.ShouldBeSubsetOf(interfaces);
    }

    [Fact]
    public void Windows_lists_its_hid_devices_and_says_which_of_them_are_power_supplies()
    {
        var hid = new WindowsHidPort();

        var all = hid.Find(static (_, _) => true);
        var supplies = hid.Find(PsuModels.Known);

        // Any PC has a keyboard or a touchpad on HID, and each device says how long its reports are.
        all.ShouldNotBeEmpty();
        all.ShouldAllBe(device => device.Path.Length > 0 && device.InputReportLength >= 0);
        supplies.ShouldAllBe(device => PsuModels.Known(device.VendorId, device.ProductId));
        supplies.Count.ShouldBeLessThanOrEqualTo(all.Count);
    }

    [Fact]
    public void The_source_reads_this_machine_without_throwing()
    {
        using var source = new PsuSource(static () => true);
        var draft = new SampleDraft();

        Should.NotThrow(() => source.Contribute(draft));

        if (new WindowsHidPort().Find(PsuModels.Known).Count == 0)
        {
            // The development laptop: nothing to read, and the source says so rather than inventing a figure.
            draft.PsuOutputW.ShouldBeNull();
            draft.PsuWallW.ShouldBeNull();
            draft.PsuName.ShouldBeNull();
            source.Unavailable.ShouldNotBeNull();
        }
        else
        {
            // A supply that answered gave one figure or the other, never both: a Corsair says what it draws from the
            // wall and the other two makers' units the DC their rails put out.
            draft.PsuName.ShouldNotBeNull();
            (draft.PsuOutputW is null || draft.PsuWallW is null).ShouldBeTrue();
        }
    }
}
