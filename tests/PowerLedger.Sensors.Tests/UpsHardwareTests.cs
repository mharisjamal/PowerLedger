using System.Diagnostics;
using PowerLedger.Sensors;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.Sensors.Tests;

/// <summary>
/// What this machine's own HID devices say. The development machine has no UPS, so these pass with none: they check
/// that the HID calls work and that what Windows hands back parses, and they list any UPS for whoever has one.
/// </summary>
[Trait("Category", "Hardware")]
public class UpsHardwareTests(ITestOutputHelper output)
{
    [Fact]
    public void The_hid_collections_on_this_machine_open_and_their_descriptors_parse()
    {
        var hid = new WindowsHid();
        var timer = Stopwatch.StartNew();
        var interfaces = hid.Interfaces();
        output.WriteLine($"{interfaces.Count} HID collections listed in {timer.Elapsed.TotalMilliseconds:F1} ms");

        var upses = 0;
        var opened = 0;
        foreach (var path in interfaces)
        {
            using var collection = hid.Open(path);
            if (collection is null)
            {
                output.WriteLine($"not opened: {path.Path}");
                continue;
            }
            opened++;
            collection.Device.ShouldNotBeNullOrWhiteSpace();

            foreach (var value in collection.Values)
            {
                value.BitSize.ShouldBeInRange((ushort)1, (ushort)32);
                value.ReportCount.ShouldBeGreaterThan((ushort)0);
                collection.Collections.Count.ShouldBeGreaterThan(0);
                value.Collection.ShouldBeLessThan((ushort)collection.Collections.Count);
            }

            if (collection.UsagePage != 0x84 || collection.Usage is not (0x04 or 0x24)) continue;
            upses++;
            output.WriteLine($"UPS: {collection.Manufacturer} {collection.Product}, "
                             + $"{collection.Values.Count} feature fields, {collection.Collections.Count} collections");
            foreach (var value in collection.Values.Where(field => field.UsagePage == 0x84))
            {
                var holder = collection.Collections[value.Collection];
                output.WriteLine($"  usage {value.Usage:x2} in collection {holder.Usage:x2} (type {holder.Type:x2}), "
                                 + $"report {value.ReportId}, {value.BitSize} bits, unit {value.Units:x8} exp {value.UnitsExp}");
            }
        }

        output.WriteLine($"{opened} collections opened, {upses} of them a UPS or a power summary");
        upses.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void The_source_reads_this_machine_without_throwing_and_says_what_it_found()
    {
        using var source = new UpsSource();
        var draft = new SampleDraft();
        var timer = Stopwatch.StartNew();

        Should.NotThrow(() => source.Contribute(draft));

        output.WriteLine($"first tick in {timer.Elapsed.TotalMilliseconds:F1} ms; "
                         + $"name {draft.UpsName ?? "none"}, watts {draft.UpsOutputW?.ToString() ?? "none"}, "
                         + $"source {draft.UpsSource}, status {source.Unavailable ?? "working"}");

        source.Supported.ShouldBeTrue();
        if (draft.UpsName is null)
        {
            // No UPS attached, which is the development machine.
            draft.UpsOutputW.ShouldBeNull();
            source.Unavailable.ShouldBe("no UPS found on USB");
        }
        else if (draft.UpsOutputW is { } watts)
        {
            watts.ShouldBeInRange(0, 100_000);
        }

        // The ticks in between reuse the answer, so they cost nothing.
        timer.Restart();
        for (var tick = 0; tick < 20; tick++) source.Contribute(new SampleDraft());
        (timer.Elapsed.TotalMilliseconds / 20).ShouldBeLessThan(5);
    }
}
