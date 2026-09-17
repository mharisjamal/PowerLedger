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
    public void Windows_lists_its_hid_devices_and_says_which_of_them_are_power_supplies()
    {
        var hid = new WindowsHid();

        var all = hid.Find(static (_, _) => true);
        var supplies = hid.Find(PsuModels.Known);

        // Any PC has a keyboard or a touchpad on HID, and each collection says how long its reports are.
        all.ShouldNotBeEmpty();
        all.ShouldAllBe(collection => collection.Path.Length > 0 && collection.InputReportLength >= 0);
        supplies.ShouldAllBe(collection => PsuModels.Known(collection.VendorId, collection.ProductId));
        supplies.Count.ShouldBeLessThanOrEqualTo(all.Count);
    }

    [Fact]
    public void The_source_reads_this_machine_without_throwing()
    {
        using var source = new PsuSource(static () => true);
        var draft = new SampleDraft();

        Should.NotThrow(() => source.Contribute(draft));

        if (new WindowsHid().Find(PsuModels.Known).Count == 0)
        {
            // The development laptop: nothing to read, and the source says so rather than inventing a figure.
            draft.PsuOutputW.ShouldBeNull();
            draft.PsuName.ShouldBeNull();
            source.Unavailable.ShouldNotBeNull();
        }
        else
        {
            draft.PsuName.ShouldNotBeNull();
        }
    }
}
