using Shouldly;

namespace PowerLedger.Core.Tests;

/// <summary>
/// Shouldly 4.3.0 ships a tolerance <c>ShouldBe</c> overload for <c>double</c> but not for <c>double?</c>
/// (see the T4-generated <c>ShouldBeTestExtensions</c>: only <c>double</c>, <c>float</c> and <c>decimal</c> get one).
/// CalibrationLearnerTests asserts learned baselines (which are <c>double?</c>) within a tolerance, so this
/// bridges the gap using Shouldly's own <c>ShouldNotBeNull</c> and non-nullable <c>ShouldBe</c> — same failure
/// reporting, just unwrapped first.
/// </summary>
internal static class NullableDoubleShouldlyExtensions
{
    public static void ShouldBe(this double? actual, double expected, double tolerance, string? customMessage = null)
        => actual.ShouldNotBeNull(customMessage).ShouldBe(expected, tolerance, customMessage);
}
