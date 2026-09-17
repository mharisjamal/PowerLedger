using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class HidValueTests
{
    [Theory]
    [InlineData(1374u, HidValue.WattUnit, 7u, 1374)]        // whole watts: HID counts in g·cm²/s³, so watts are 10^7
    [InlineData(1374u, HidValue.WattUnit, 6u, 137.4)]       // tenths of a watt
    [InlineData(27u, 0u, 0u, 27)]                           // a percentage, which carries no unit
    [InlineData(480u, HidValue.VoltUnit, 6u, 48)]           // tenths of a volt
    [InlineData(1234u, 0u, 0x0Eu, 12.34)]                   // an exponent of -2, as the four-bit field encodes it
    [InlineData(1234u, 0u, 0xFEu, 12.34)]                   // the same exponent, widened to a byte by the firmware
    public void The_unit_and_its_exponent_turn_the_raw_bits_into_a_number(uint raw, uint units, uint exponent, double expected)
        => Field(units: units, exponent: exponent).Physical(raw).ShouldNotBeNull().ShouldBe(expected, 1e-9);

    [Fact]
    public void A_field_whose_range_goes_negative_is_read_as_a_signed_number()
    {
        var field = Field(min: -32768, max: 32767);

        field.Physical(0xFFF6).ShouldBe(-10);
        field.Physical(10).ShouldBe(10);
    }

    [Fact]
    public void A_field_whose_range_starts_at_nothing_is_read_unsigned()
    {
        Field(min: 0, max: 32767).Physical(0xFFF6).ShouldBe(65526);
    }

    [Fact]
    public void A_maximum_that_ran_into_the_sign_bit_leaves_the_field_unsigned_and_unchecked()
    {
        // A firmware that encodes 65535 in two bytes leaves Windows a maximum of -1, below its own minimum.
        var field = Field(min: 0, max: -1, hasNull: true);

        field.Physical(0xFFF6).ShouldBe(65526);
    }

    [Fact]
    public void A_field_with_a_null_value_reads_as_no_reading_outside_its_range()
    {
        var field = Field(min: 0, max: 100, bits: 8, hasNull: true);

        field.Physical(255).ShouldBeNull();
        field.Physical(27).ShouldBe(27);
    }

    [Fact]
    public void A_value_above_the_range_is_taken_as_sent_where_the_field_has_no_null_value()
    {
        // A CyberPower EC850LCD rated 510 W declares a maximum of 450: NUT read 450 until it stopped clamping
        // (issue 2917), so the range is not allowed to cut a rating down here either.
        Field(min: 0, max: 450).Physical(510).ShouldBe(510);
    }

    [Fact]
    public void A_field_with_a_physical_range_is_mapped_onto_it()
    {
        Field(min: 0, max: 1000, physicalMin: 0, physicalMax: 500).Physical(500).ShouldBe(250);
    }

    private static HidValue Field(
        uint units = 0, uint exponent = 0, int min = 0, int max = 0, int physicalMin = 0, int physicalMax = 0,
        ushort bits = 16, bool hasNull = false)
        => new()
        {
            UsagePage = 0x84,
            Usage = 0x34,
            Units = units,
            UnitsExp = exponent,
            LogicalMin = min,
            LogicalMax = max,
            PhysicalMin = physicalMin,
            PhysicalMax = physicalMax,
            BitSize = bits,
            HasNull = hasNull,
        };
}
