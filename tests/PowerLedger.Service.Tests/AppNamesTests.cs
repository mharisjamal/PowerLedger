using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Review item 1: an app's name is read off the loop's thread, so a slow disk never holds a tick.</summary>
public class AppNamesTests
{
    [Fact]
    public async Task A_name_is_read_once_off_the_callers_thread_which_never_waits_for_it()
    {
        using var gate = new ManualResetEventSlim();
        var reads = 0;
        var readOn = -1;
        var names = new AppNames(path =>
        {
            Interlocked.Increment(ref reads);
            readOn = Environment.CurrentManagedThreadId;
            gate.Wait();                                            // a disk that takes its time
            return "Name of " + path;
        });

        names.For(@"c:\a.exe").ShouldBeNull();
        names.For(@"c:\a.exe").ShouldBeNull();
        gate.Set();
        await WaitFor.True(() => names.For(@"c:\a.exe") is not null);

        names.For(@"c:\a.exe").ShouldBe(@"Name of c:\a.exe");
        Volatile.Read(ref reads).ShouldBe(1);
        readOn.ShouldNotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public async Task An_image_that_will_not_be_read_is_named_by_its_file()
    {
        var names = new AppNames(_ => throw new IOException("gone"));
        names.For(@"c:\apps\tool.exe").ShouldBeNull();
        await WaitFor.True(() => names.For(@"c:\apps\tool.exe") is not null);
        names.For(@"c:\apps\tool.exe").ShouldBe("tool");
    }
}
