using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class NotificationsTests
{
    private const uint PbtApmSuspend = 0x4;
    private const uint PbtApmResumeSuspend = 0x7;
    private const uint PbtApmResumeAutomatic = 0x12;
    private const uint PbtPowerSettingChange = 0x8013;

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]   // dimmed is still lit
    public void The_console_display_state_reaches_the_signals(int state, bool expected)
    {
        var signals = new ServiceSignals(TimeProvider.System) { DisplayOn = !expected };
        var notifications = new PowerNotifications(new LoopCommands(), signals, NullLogger<PowerNotifications>.Instance);
        var setting = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy(Guid.Parse("6FE69556-704A-47A0-8F24-C28D936FDA47").ToByteArray(), 0, setting, 16);
            Marshal.WriteInt32(setting, 16, 4);
            Marshal.WriteInt32(setting, 20, state);
            notifications.OnDisplay(IntPtr.Zero, PbtPowerSettingChange, setting).ShouldBe(0u);
        }
        finally
        {
            Marshal.FreeHGlobal(setting);
        }
        signals.DisplayOn.ShouldBe(expected);
    }

    [Fact]
    public async Task A_suspend_holds_the_machine_until_the_loop_has_written()
    {
        var commands = new LoopCommands();
        var notifications = new PowerNotifications(commands, new ServiceSignals(TimeProvider.System), NullLogger<PowerNotifications>.Instance);
        var callback = Task.Run(() => notifications.OnSuspendResume(IntPtr.Zero, PbtApmSuspend, IntPtr.Zero));
        var command = await commands.Reader.ReadAsync();
        command.ShouldBeOfType<SuspendCommand>();
        callback.IsCompleted.ShouldBeFalse();
        command.Complete();
        await callback.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Only_the_automatic_resume_wakes_the_loop()
    {
        var commands = new LoopCommands();
        var notifications = new PowerNotifications(commands, new ServiceSignals(TimeProvider.System), NullLogger<PowerNotifications>.Instance);
        notifications.OnSuspendResume(IntPtr.Zero, PbtApmResumeAutomatic, IntPtr.Zero);
        notifications.OnSuspendResume(IntPtr.Zero, PbtApmResumeSuspend, IntPtr.Zero);
        commands.Reader.TryRead(out var first).ShouldBeTrue();
        first.ShouldBeOfType<ResumeCommand>();
        commands.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void Session_lock_and_unlock_reach_the_signals()
    {
        var signals = new ServiceSignals(TimeProvider.System);
        PowerLedgerServiceLifetime.Apply(SessionChangeReason.SessionLock, signals);
        signals.SessionLocked.ShouldBeTrue();
        PowerLedgerServiceLifetime.Apply(SessionChangeReason.RemoteConnect, signals);
        signals.SessionLocked.ShouldBeTrue();
        PowerLedgerServiceLifetime.Apply(SessionChangeReason.SessionUnlock, signals);
        signals.SessionLocked.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task Windows_reports_the_display_state_as_soon_as_the_service_registers()
    {
        var signals = new ServiceSignals(TimeProvider.System) { DisplayOn = false };
        var notifications = new PowerNotifications(new LoopCommands(), signals, NullLogger<PowerNotifications>.Instance);
        await notifications.StartAsync(CancellationToken.None);
        try
        {
            await WaitFor.True(() => signals.DisplayOn);
        }
        finally
        {
            await notifications.StopAsync(CancellationToken.None);
        }
    }
}
