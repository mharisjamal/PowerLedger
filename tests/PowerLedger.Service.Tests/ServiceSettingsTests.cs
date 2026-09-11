using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class ServiceSettingsTests
{
    [Fact]
    public void The_defaults_are_the_spec_values_and_pass_their_own_checks()
    {
        var settings = ServiceSettings.Default;
        settings.IdleThresholdSeconds.ShouldBe(300);
        settings.SampleIntervalSeconds.ShouldBe(1);
        settings.RawRetentionHours.ShouldBe(48);
        settings.HistoryRetentionYears.ShouldBe(2);
        settings.Validate().ShouldBeNull();
        (settings with { Profile = MachineProfile.DefaultDesktop }).Validate().ShouldBeNull();
    }

    [Theory]
    [InlineData(59, 1, 48, 2)]
    [InlineData(1801, 1, 48, 2)]
    [InlineData(300, 0, 48, 2)]
    [InlineData(300, 6, 48, 2)]
    [InlineData(300, 1, 23, 2)]
    [InlineData(300, 1, 169, 2)]
    [InlineData(300, 1, 48, 0)]
    [InlineData(300, 1, 48, 6)]
    public void Each_number_is_held_to_its_range(int idle, int interval, int rawHours, int years)
        => new ServiceSettings { IdleThresholdSeconds = idle, SampleIntervalSeconds = interval, RawRetentionHours = rawHours, HistoryRetentionYears = years }
            .Validate().ShouldNotBeNull();

    [Theory]
    [InlineData(60, 1, 24, 1)]
    [InlineData(1800, 5, 168, 5)]
    public void The_ends_of_each_range_are_accepted(int idle, int interval, int rawHours, int years)
        => new ServiceSettings { IdleThresholdSeconds = idle, SampleIntervalSeconds = interval, RawRetentionHours = rawHours, HistoryRetentionYears = years }
            .Validate().ShouldBeNull();

    [Fact]
    public void A_profile_with_an_impossible_value_is_rejected()
    {
        var laptop = MachineProfile.DefaultLaptop;
        With(laptop with { ExtrasWatts = double.NaN }).ShouldNotBeNull();
        With(laptop with { ExtrasWatts = -1 }).ShouldNotBeNull();
        With(laptop with { SsdCount = -1 }).ShouldNotBeNull();
        With(laptop with { RamSticks = 0 }).ShouldNotBeNull();
        With(laptop with { Chassis = (ChassisKind)7 }).ShouldNotBeNull();
        With(laptop with { PsuTier = (PsuTier)9 }).ShouldNotBeNull();
        With(laptop with { DisplayDiagonalInches = 5 }).ShouldNotBeNull();
        With(laptop with { CpuTdpOverrideW = 0 }).ShouldNotBeNull();
        With(laptop with { MonitorWatts = double.PositiveInfinity }).ShouldNotBeNull();
    }

    [Fact]
    public void Values_that_mean_unknown_or_none_are_accepted()
        => With(MachineProfile.DefaultLaptop with { DisplayDiagonalInches = 0, CpuTdpOverrideW = null, GpuTdpOverrideW = 75, SsdCount = 0 })
            .ShouldBeNull();

    [Fact]
    public void A_missing_profile_is_rejected_rather_than_crashing()
        => (ServiceSettings.Default with { Profile = null! }).Validate().ShouldNotBeNull();

    private static string? With(MachineProfile profile) => (ServiceSettings.Default with { Profile = profile }).Validate();
}
