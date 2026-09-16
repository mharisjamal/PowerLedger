using System.Globalization;
using System.Reflection;
using System.Text.Json;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class MachineProfileTests
{
    [Fact]
    public void DefaultLaptop_is_a_laptop_with_a_panel_and_no_external_monitors()
    {
        var p = MachineProfile.DefaultLaptop;
        p.Chassis.ShouldBe(ChassisKind.Laptop);
        p.DisplayDiagonalInches.ShouldBe(15.6);
        p.IncludeMonitors.ShouldBeFalse();
        p.PsuTier.ShouldBe(PsuTier.Bronze);
    }

    [Fact]
    public void DefaultDesktop_has_no_internal_panel()
    {
        MachineProfile.DefaultDesktop.Chassis.ShouldBe(ChassisKind.Desktop);
        MachineProfile.DefaultDesktop.DisplayDiagonalInches.ShouldBe(0);
    }

    [Fact]
    public void Quality_integer_values_are_pinned_because_they_are_stored_in_sqlite()
    {
        ((int)Quality.Estimated).ShouldBe(0);
        ((int)Quality.Calibrated).ShouldBe(1);
        ((int)Quality.Measured).ShouldBe(2);
    }

    [Fact]
    public void Json_missing_properties_fall_back_to_documented_defaults_not_zero()
    {
        var p = JsonSerializer.Deserialize<MachineProfile>("""{"Chassis":0,"DisplayDiagonalInches":0}""")!;
        p.Chassis.ShouldBe(ChassisKind.Desktop);
        p.PsuTier.ShouldBe(PsuTier.Bronze);
        p.MonitorWatts.ShouldBe(25);
        p.RamSticks.ShouldBe(1);
        p.CpuTdpOverrideW.ShouldBeNull();
        p.Monitors.ShouldBeEmpty();
    }

    [Fact]
    public void Profiles_with_the_same_monitor_choices_are_equal_whatever_list_holds_them()
    {
        var choice = new MonitorChoice { Key = "DELA0B1-4C4A3833", Watts = 30 };
        var profile = MachineProfile.DefaultDesktop with { Monitors = [choice] };
        var copy = MachineProfile.DefaultDesktop with { Monitors = new List<MonitorChoice> { choice with { } } };
        copy.ShouldBe(profile);
        copy.GetHashCode().ShouldBe(profile.GetHashCode());
        (copy == profile).ShouldBeTrue();

        profile.ShouldNotBe(MachineProfile.DefaultDesktop with { Monitors = [choice with { Counted = false }] });
        profile.ShouldNotBe(MachineProfile.DefaultDesktop with { Monitors = [choice, choice with { Key = "GSM5B08-77" }] });
        profile.ShouldNotBe(MachineProfile.DefaultDesktop);
        profile.ShouldNotBe(MachineProfile.DefaultDesktop with { Monitors = null! });
    }

    [Fact]
    public void Every_member_takes_part_in_equality()
    {
        foreach (var property in typeof(MachineProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var changed = MachineProfile.DefaultDesktop with { };
            property.SetValue(changed, Different(property.GetValue(changed), property.PropertyType));
            changed.Equals(MachineProfile.DefaultDesktop).ShouldBeFalse(property.Name);
        }
    }

    private static object Different(object? value, Type type) => value switch
    {
        Enum member => Enum.ToObject(type, Convert.ToInt32(member, CultureInfo.InvariantCulture) + 1),
        int number => number + 1,
        double number => number + 1,
        bool flag => !flag,
        null when type == typeof(double?) => 1.0,
        IReadOnlyList<MonitorChoice> => new[] { new MonitorChoice { Key = "DELA0B1-4C4A3833" } },
        _ => throw new InvalidOperationException($"Give {type.Name} a different value here."),
    };
}
