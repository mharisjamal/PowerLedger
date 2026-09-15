using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Reads this machine's power plan, so it runs where there is a real one.</summary>
[Trait("Category", "Hardware")]
public class SleepSettingsTests
{
    [Fact]
    public void The_active_plan_has_sleep_and_display_timeouts()
    {
        var timeouts = new SleepSettings().Read();
        timeouts.SleepAc.ShouldNotBeNull();
        timeouts.DisplayAc.ShouldNotBeNull();
    }
}
