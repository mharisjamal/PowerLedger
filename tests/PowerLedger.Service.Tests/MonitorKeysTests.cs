using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class MonitorKeysTests
{
    private const string Key = @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353";

    [Fact]
    public void WMI_s_instance_name_and_the_device_path_name_the_same_monitor()
    {
        MonitorKeys.FromInstanceName(@"DISPLAY\DELA0B1\5&2f5a1b&0&UID4353_0").ShouldBe(Key);
        MonitorKeys.FromDevicePath(@"\\?\DISPLAY#DELA0B1#5&2f5a1b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}").ShouldBe(Key);
    }

    [Theory]
    [InlineData(@"DISPLAY\DELA0B1\5&2f5a1b&0&UID4353_12", Key)]
    [InlineData(@"DISPLAY\DELA0B1\5&2f5a1b&0&UID4353", Key)]
    [InlineData(@" DISPLAY\DELA0B1\5&2f5a1b&0&UID4353_0 ", Key)]
    [InlineData(@"DISPLAY\GSM5B08\7&1a2b&0&UID_A1", @"DISPLAY\GSM5B08\7&1A2B&0&UID_A1")]
    [InlineData(@"DISPLAY\GSM5B08\7&1a2b&0&UID_", @"DISPLAY\GSM5B08\7&1A2B&0&UID_")]
    public void The_number_WMI_appends_is_dropped_only_when_digits_follow_the_underscore(string instanceName, string key)
        => MonitorKeys.FromInstanceName(instanceName).ShouldBe(key);

    [Theory]
    [InlineData(@"DISPLAY#DELA0B1#5&2f5a1b&0&UID4353")]
    [InlineData(@"\\?\display#dela0b1#5&2f5a1b&0&uid4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")]
    public void A_device_path_gives_the_key_with_or_without_its_prefix_and_interface_and_in_any_case(string devicePath)
        => MonitorKeys.FromDevicePath(devicePath).ShouldBe(Key);

    [Theory]
    [InlineData(@"\\?\HID#VID_046D&PID_C52B&MI_00#7&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
    [InlineData(@"\\?\DISPLAY#DELA0B1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")]
    [InlineData(@"\\?\DISPLAY##5&2f5a1b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")]
    [InlineData(@"\\.\DISPLAY1")]
    [InlineData("")]
    public void A_path_that_is_not_a_display_s_gives_no_key(string devicePath)
        => MonitorKeys.FromDevicePath(devicePath).ShouldBeNull();
}
