using PowerLedger.Service.Updates;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class InstallTimingTests
{
    private static readonly DateTimeOffset Downloaded = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

    private static InstallMoment Decide(
        bool urgent = false, bool userAtConsole = true, bool windowShowing = true, double? idle = 0, TimeSpan? after = null, TimeSpan? warnedAfter = null)
        => InstallTiming.Decide(
            urgent, userAtConsole, windowShowing, idle, Downloaded, warnedAfter is { } w ? Downloaded + w : null, Downloaded + (after ?? TimeSpan.Zero));

    [Fact]
    public void A_required_update_goes_in_at_once_even_with_the_window_in_use()
        => Decide(urgent: true).ShouldBe(InstallMoment.Now);

    [Fact]
    public void With_nobody_signed_in_at_the_console_it_goes_in_at_once()
        => Decide(userAtConsole: false).ShouldBe(InstallMoment.Now);

    [Fact]
    public void With_the_window_not_showing_it_goes_in_at_once()
        => Decide(windowShowing: false).ShouldBe(InstallMoment.Now);

    [Fact]
    public void With_the_window_in_use_it_waits()
    {
        Decide(idle: 0).ShouldBe(InstallMoment.Wait);
        Decide(idle: 299, after: TimeSpan.FromHours(3)).ShouldBe(InstallMoment.Wait);
        Decide(idle: null).ShouldBe(InstallMoment.Wait);
    }

    [Fact]
    public void Five_idle_minutes_bring_the_notice_then_the_install_a_minute_later()
    {
        Decide(idle: 300).ShouldBe(InstallMoment.Warn);
        Decide(idle: 0, after: TimeSpan.FromMinutes(10), warnedAfter: TimeSpan.FromMinutes(9.5)).ShouldBe(InstallMoment.Wait);
        Decide(idle: 0, after: TimeSpan.FromMinutes(10), warnedAfter: TimeSpan.FromMinutes(9)).ShouldBe(InstallMoment.Now);
    }

    [Fact]
    public void Six_hours_after_the_download_it_goes_in_however_busy_the_user_is_with_the_notice_a_minute_before()
    {
        Decide(after: TimeSpan.FromHours(6) - TimeSpan.FromMinutes(1.5)).ShouldBe(InstallMoment.Wait);
        Decide(after: TimeSpan.FromHours(6) - TimeSpan.FromMinutes(1)).ShouldBe(InstallMoment.Warn);
        Decide(after: TimeSpan.FromHours(6), warnedAfter: TimeSpan.FromHours(6) - TimeSpan.FromMinutes(1)).ShouldBe(InstallMoment.Now);
    }

    [Fact]
    public void A_metered_connection_is_waited_out_for_a_day()
    {
        InstallTiming.MayDownload(metered: false, urgent: false, Downloaded, Downloaded).ShouldBeTrue();
        InstallTiming.MayDownload(metered: true, urgent: false, Downloaded, Downloaded + TimeSpan.FromHours(23.9)).ShouldBeFalse();
        InstallTiming.MayDownload(metered: true, urgent: false, Downloaded, Downloaded + TimeSpan.FromHours(24)).ShouldBeTrue();
    }

    [Fact]
    public void A_required_update_downloads_on_a_metered_connection_at_once()
        => InstallTiming.MayDownload(metered: true, urgent: true, Downloaded, Downloaded).ShouldBeTrue();
}
