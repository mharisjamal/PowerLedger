using System.Text;
using System.Text.Json.Nodes;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Service.Sharing;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>The upload as it goes on the wire (data-sharing design §3): built from named fields only, one section per switch.</summary>
public class ReportTests
{
    [Theory]
    [MemberData(nameof(SharingContractTests.Named), "valid-", MemberType = typeof(SharingContractTests))]
    public void A_valid_report_reads_in_and_writes_out_unchanged(string fixture)
    {
        var file = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contract", "fixtures", fixture));

        var back = ReportJson.Write(ReportJson.Read(Encoding.UTF8.GetBytes(file)));

        var (written, reference) = (JsonNode.Parse(back), JsonNode.Parse(file));
        JsonNode.DeepEquals(written, reference).ShouldBeTrue(written!.ToJsonString());
    }

    [Fact]
    public void A_report_with_every_section_passes_the_servers_schema()
    {
        var report = ReportBuilder.Build(SharingFakes.Inputs());

        report.Diagnostics.ShouldNotBeNull();
        report.Usage.ShouldNotBeNull();
        report.Power.ShouldNotBeNull().Hardware.ShouldNotBeNull();
        ReportSchema.Problems(ReportJson.Write(report)).ShouldBeNull();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public void Each_switch_off_leaves_its_section_out(bool diagnostics, bool usage, bool power)
    {
        var consent = new Consent(ConsentText.Version, diagnostics, usage, power, Share: false);

        var report = ReportBuilder.Build(SharingFakes.Inputs(consent));

        (report.Diagnostics is not null, report.Usage is not null, report.Power is not null).ShouldBe((diagnostics, usage, power));
        report.Consent.ShouldBe(new ConsentDto(ConsentText.Version, diagnostics, usage, power, false));
        var json = ReportJson.Write(report);
        ReportSchema.Problems(json).ShouldBeNull();
        var keys = JsonNode.Parse(json)!.AsObject().Select(property => property.Key).ToList();
        keys.Contains("diagnostics").ShouldBe(diagnostics);
        keys.Contains("usage").ShouldBe(usage);
        keys.Contains("power").ShouldBe(power);
    }

    [Fact]
    public void Usage_goes_only_on_a_day_the_app_reported_it_and_power_only_with_minutes_or_hardware()
    {
        var inputs = SharingFakes.Inputs() with { Events = SharingFakes.Events with { Usage = null }, Minutes = [], WithHardware = false };

        var report = ReportBuilder.Build(inputs);

        report.Usage.ShouldBeNull();
        report.Power.ShouldBeNull();
        report.Diagnostics.ShouldNotBeNull();
        ReportBuilder.Build(inputs with { WithHardware = true }).Power.ShouldNotBeNull().Minutes.T.ShouldBeEmpty();
    }

    [Fact]
    public void The_header_says_what_sent_it_and_which_day_it_describes()
    {
        var report = ReportBuilder.Build(SharingFakes.Inputs());

        (report.Schema, report.InstallId, report.App, report.Windows, report.Arch, report.Chassis, report.Day, report.UtcOffsetMinutes)
            .ShouldBe((1, SharingFakes.InstallId, "0.6.0", "10.0.26200", "x64", "desktop", "2026-09-24", 60));
        report.Consent.ShouldBe(new ConsentDto(ConsentText.Version, true, true, true, true));
    }

    [Theory]
    [InlineData("0.6.0+1a2b3c4", "0.6.0")]
    [InlineData("0.6.0-rc.1+1a2b3c4", "0.6.0")]
    [InlineData("10.20.300", "10.20.300")]
    [InlineData("six", "0.0.0")]
    [InlineData(null, "0.0.0")]
    public void A_version_goes_as_the_plain_three_numbers_the_server_takes(string? version, string sent) =>
        ServiceVersion.Plain(version).ShouldBe(sent);

    [Fact]
    public void The_minutes_go_as_columns_with_watts_and_seconds_to_a_tenth_and_loads_to_a_thousandth()
    {
        var minutes = ReportBuilder.Build(SharingFakes.Inputs()).Power.ShouldNotBeNull().Minutes;

        minutes.T.ShouldBe(new[] { 600, 601, 602 });
        minutes.AvgW.ShouldAllBe(w => w == 142.3);
        minutes.MaxW.ShouldAllBe(w => w == 160.3);
        minutes.CpuW.ShouldAllBe(w => w == 35.2);
        minutes.GpuW.ShouldAllBe(w => w == 40.5);
        minutes.MonitorsW.ShouldAllBe(w => w == 24.3);
        minutes.CpuLoad.ShouldAllBe(load => load == 0.121);
        minutes.GpuLoad.ShouldAllBe(load => load == 0.2);
        minutes.Brightness.ShouldAllBe(brightness => brightness == null);
        minutes.EstimatedS.ShouldAllBe(seconds => seconds == 60);
        minutes.Samples.ShouldAllBe(samples => samples == 60);
        minutes.MeasuredMask.ShouldAllBe(mask => mask == 3);

        // A small negative remainder rounds to 0, never to "-0".
        var json = Encoding.UTF8.GetString(ReportJson.Write(ReportBuilder.Build(SharingFakes.Inputs())));
        json.ShouldContain("\"unattributedW\":[0,0,0]");
    }

    [Fact]
    public void The_hardware_is_copied_field_by_field()
    {
        var hardware = ReportBuilder.Build(SharingFakes.Inputs()).Power.ShouldNotBeNull().Hardware.ShouldNotBeNull();

        hardware.Cpu.ShouldBe(new CpuDto("AMD Ryzen 7 7800X3D 8-Core Processor", null, 16, SharingFakes.Facts.CpuTdpW, TdpTyped: false));
        hardware.Gpus.ShouldHaveSingleItem().ShouldBe(new GpuDto("nvidia", "NVIDIA GeForce RTX 4070", null, Discrete: true, 220, TdpTyped: true));
        hardware.MemoryGb.ShouldBe(31.9);
        hardware.Monitors.ShouldHaveSingleItem()
            .ShouldBe(new MonitorDto("GSM", "5B7F", "LG ULTRAGEAR", 27, 2560, 1440, 165, false, OwnPlug: true, Counted: true, 24.3, "energyStar"));
        hardware.Psu.ShouldBe(new PsuDto("gold", 1000, "Corsair HX1000i"));
        hardware.Ups.ShouldBe(new UpsDto("APC Back-UPS ES 850G2", "thisPc"));
        hardware.Profile.ShouldBe(new ProfileDto(2, true, 1, 0, 3, 4.5, null));
        hardware.SampleIntervalS.ShouldBe(1);
        hardware.Tariff.ShouldBe(new TariffDto(0.25m, "USD"));
    }

    [Fact]
    public void Without_a_ups_the_ups_is_null_and_a_price_the_schema_cannot_take_goes_as_no_tariff()
    {
        var inputs = SharingFakes.Inputs() with
        {
            Status = SharingFakes.Status(devices: []),
            Tariff = new Tariff(SharingFakes.At, 1500m, "IDR"),
        };

        var hardware = ReportBuilder.Build(inputs).Power.ShouldNotBeNull().Hardware.ShouldNotBeNull();

        hardware.Ups.ShouldBeNull();
        hardware.Psu.ShouldBe(new PsuDto("gold", null, null));
        hardware.Tariff.ShouldBeNull();
        ReportSchema.Problems(ReportJson.Write(ReportBuilder.Build(inputs))).ShouldBeNull();
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", "nvidia")]
    [InlineData("AMD Radeon RX 7800 XT", "amd")]
    [InlineData("Radeon 780M Graphics", "amd")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "intel")]
    [InlineData("Intel(R) UHD Graphics 620", "intel")]
    [InlineData("Qualcomm(R) Adreno(TM) X1-85 GPU", "qualcomm")]
    [InlineData("Microsoft Basic Display Adapter", "other")]
    public void A_graphics_cards_maker_is_read_from_its_name(string name, string vendor) =>
        ReportBuilder.Build(SharingFakes.Inputs() with { Facts = SharingFakes.Facts with { GpuName = name } })
            .Power.ShouldNotBeNull().Hardware.ShouldNotBeNull().Gpus.ShouldHaveSingleItem().Vendor.ShouldBe(vendor);

    [Theory]
    [InlineData(@"DISPLAY\GSM5B7F\5&1A2B3C&0&UID4352", "GSM", "5B7F")]
    [InlineData(@"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353", "DEL", "A0B1")]
    [InlineData(@"DISPLAY\Default_Monitor\1&2&3", null, null)]
    [InlineData("", null, null)]
    public void A_monitors_maker_and_product_code_come_from_its_instance_and_nothing_else_of_it_does(string instance, string? maker, string? product)
    {
        var monitor = SharingFakes.Monitor with { Instance = instance, Source = MonitorSource.Estimate, Inches = 0, Width = 0, RefreshHz = null };

        var sent = ReportBuilder.Build(SharingFakes.Inputs() with { Status = SharingFakes.Status(monitors: [monitor]) })
            .Power.ShouldNotBeNull().Hardware.ShouldNotBeNull().Monitors.ShouldHaveSingleItem();

        (sent.Maker, sent.Product, sent.FigureFrom).ShouldBe((maker, product, "estimated"));
        (sent.SizeIn, sent.Width, sent.RefreshHz).ShouldBe((null, null, null));    // unknown, not 0
    }

    [Fact]
    public void Each_source_says_its_state_as_the_about_page_does_with_the_days_failures_and_the_device_it_reads()
    {
        var diagnostics = ReportBuilder.Build(SharingFakes.Inputs()).Diagnostics.ShouldNotBeNull();

        diagnostics.Sources.ShouldBe(new[]
        {
            new SourceDto("energy-meter", "working", 0, null, null),
            new SourceDto("nvidia-gpu", "working", 0, null, "NVIDIA GeForce RTX 4070"),
            new SourceDto("amd-gpu", "notOnMachine", 0, "No AMD graphics driver is installed.", "NVIDIA GeForce RTX 4070"),
            new SourceDto("battery", "failing", 5, "The battery didn't answer.", null),
            new SourceDto("power-supply", "note", 0, "No power supply found on USB.", "Corsair HX1000i"),
            new SourceDto("ups", "working", 0, null, "APC Back-UPS ES 850G2"),
        });
        var crash = diagnostics.Crashes.ShouldHaveSingleItem();
        (crash.At, crash.Component, crash.Version, crash.Message).ShouldBe(("2026-09-24T10:00:00.000Z", "app", "0.6.0", "Collection was modified."));
        crash.Types.ShouldBe(new[] { "System.InvalidOperationException" });
    }

    [Fact]
    public void The_apps_counts_are_copied_as_they_were_merged()
    {
        var usage = ReportBuilder.Build(SharingFakes.Inputs()).Usage.ShouldNotBeNull();

        (usage.AppOpens, usage.ReportsExported, usage.UpdatesInstalled, usage.DaysSinceFirstRun, usage.Theme, usage.Language)
            .ShouldBe((3, 0, 0, 12, "dark", "en-US"));
        usage.Pages.ShouldBe(new Dictionary<string, int> { ["now"] = 5, ["history"] = 2 });
        usage.Settings.ShouldBe(new Dictionary<string, int> { ["theme"] = 1 });
    }

    [Fact]
    public void Names_paths_device_ids_and_serial_numbers_never_reach_the_report()
    {
        var names = new ScrubNames(Environment.UserName, Environment.MachineName, "CONTOSO");
        const string profile = @"C:\Users\alice";
        const string hid = @"\\?\hid#vid_1b1c&pid_1c05#7&2d0f1a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string instance = @"DISPLAY\GSM5B7F\5&1A2B3C&0&UID4352";
        const string serial = "7MKZG34";
        var everything = $"{names.Machine} {names.User} CONTOSO {profile}\\AppData\\Local {hid} {instance}";
        var inputs = SharingFakes.Inputs() with
        {
            Names = names,
            Facts = SharingFakes.Facts with { CpuName = $"AMD Ryzen 7 {names.Machine}", GpuName = $"NVIDIA GeForce {names.User} CONTOSO" },
            Status = SharingFakes.Status(
                sources:
                [
                    new SourceStatus("power-supply", true, $"Opening {hid} failed", 0, null),
                    new SourceStatus("battery", true, null, 1, everything),
                    new SourceStatus("amd-gpu", false, $@"Not found under {profile}", 0, null),
                ],
                monitors: [SharingFakes.Monitor with { Key = $"GSM5B7F-{serial}", Instance = instance, Name = $"LG {names.Machine}" }],
                devices:
                [
                    new PowerDeviceStatus(PowerDeviceKind.Ups, $"APC on {names.Machine}", 100, ""),
                    new PowerDeviceStatus(PowerDeviceKind.PowerSupply, $"Corsair for {names.User}", 300, ""),
                ]),
            Events = SharingFakes.Events with
            {
                Sources = new Dictionary<string, SourceDay> { ["battery"] = new(3, everything) },
                Crashes = [new CrashReport(SharingFakes.At, "service", "0.6.0", [$"{names.Machine}.Error"], everything, $"   at X() in {profile}\\src\\X.cs:line 4\n{everything}")],
            },
        };

        var json = ReportJson.Write(ReportBuilder.Build(inputs));

        var text = Encoding.UTF8.GetString(json);
        var strings = Strings(JsonNode.Parse(json)).ToList();
        foreach (var secret in new[] { names.Machine!, names.User!, "CONTOSO", profile, "alice", "vid_1b1c", "2d0f1a", "1A2B3C", "UID4352", serial })
        {
            text.ShouldNotContain(secret, Case.Insensitive);
            strings.ShouldAllBe(value => !value.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{secret} was sent");
        }
        ReportSchema.Problems(json).ShouldBeNull();
    }

    /// <summary>Every property name and string value in the JSON, unescaped.</summary>
    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(property => Strings(property.Value).Prepend(property.Key)),
        JsonArray a => a.SelectMany(Strings),
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => [],
    };
}
