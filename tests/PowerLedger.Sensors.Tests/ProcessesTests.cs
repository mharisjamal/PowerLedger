using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class ProcessesTests
{
    /// <summary>Two entries as NtQuerySystemInformation lays them out, the second's offset from the first in its first
    /// field; each one's image name follows it, and its name pointer holds the name's address as if the buffer sat at
    /// <paramref name="baseAddress"/>.</summary>
    private static byte[] Entries(ProcessLayout layout, long baseAddress)
    {
        var buffer = new byte[600];
        void Entry(int at, int next, uint pid, uint session, ulong cycles, long create, long user, long kernel, string name, uint parent = 0)
        {
            var e = buffer.AsSpan(at);
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)next);
            BinaryPrimitives.WriteUInt64LittleEndian(e[layout.Cycles..], cycles);
            BinaryPrimitives.WriteInt64LittleEndian(e[layout.Create..], create);
            BinaryPrimitives.WriteInt64LittleEndian(e[layout.User..], user);
            BinaryPrimitives.WriteInt64LittleEndian(e[layout.Kernel..], kernel);
            var nameAt = at + 200;
            Encoding.Unicode.GetBytes(name).CopyTo(buffer, nameAt);
            BinaryPrimitives.WriteUInt16LittleEndian(e[layout.NameLength..], (ushort)(name.Length * 2));
            if (layout.Wide)
            {
                BinaryPrimitives.WriteInt64LittleEndian(e[layout.NameBuffer..], baseAddress + nameAt);
                BinaryPrimitives.WriteUInt64LittleEndian(e[layout.Pid..], pid);
                BinaryPrimitives.WriteUInt64LittleEndian(e[layout.Parent..], parent);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(e[layout.NameBuffer..], (uint)(baseAddress + nameAt));
                BinaryPrimitives.WriteUInt32LittleEndian(e[layout.Pid..], pid);
                BinaryPrimitives.WriteUInt32LittleEndian(e[layout.Parent..], parent);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(e[layout.Session..], session);
        }
        Entry(0, 300, pid: 4, session: 0, cycles: 123_456_789_012, create: 0, user: 0, kernel: 9_000_000, name: "System");
        Entry(300, 0, pid: 70_000, session: 2, cycles: 42, create: 133_000_000_000_000_000, user: 30, kernel: 12, name: "chrome.exe", parent: 69_999);
        return buffer;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_layouts_read_each_process_counts_and_its_image_name(bool sixtyFourBit)
    {
        var layout = sixtyFourBit ? ProcessLayout.SixtyFourBit : ProcessLayout.ThirtyTwoBit;
        const long Base = 0x1000_0000;
        var buffer = Entries(layout, Base);
        List<ProcessCounts> processes = [new(1, 1, 1, 1, 1, 1)];

        WindowsProcesses.Parse(buffer, layout, processes);

        processes.ShouldBe(
        [
            new ProcessCounts(4, 0, 0, 123_456_789_012, 9_000_000, 0),
            new ProcessCounts(70_000, 133_000_000_000_000_000, 2, 42, 42, 300, ParentPid: 69_999),   // review item 6: who started it
        ]);
        WindowsProcesses.ImageName(buffer, layout, Base, 0).ShouldBe("System");
        WindowsProcesses.ImageName(buffer, layout, Base, 300).ShouldBe("chrome.exe");
    }

    [Fact]
    public void A_name_pointing_outside_the_buffer_reads_as_none()
    {
        var layout = ProcessLayout.SixtyFourBit;
        var buffer = Entries(layout, 0x1000_0000);
        WindowsProcesses.ImageName(buffer, layout, 0x2000_0000, 0).ShouldBe("");
    }

    [Fact]
    public void Windows_lists_this_process_with_its_session_and_its_work_so_far()
    {
        using var source = new WindowsProcesses();
        List<ProcessCounts> processes = [];
        source.Read(processes, out var processors).ShouldBeTrue();

        using var self = Process.GetCurrentProcess();
        var me = processes.Single(p => p.Pid == (uint)Environment.ProcessId);
        me.Session.ShouldBe((uint)self.SessionId);
        me.Cycles.ShouldBeGreaterThan(0UL);
        me.Time.ShouldBeGreaterThan(0);
        source.ImageName(processes.Single(p => p.Pid == 4).Name).ShouldBe("System");
        processors.Idle.ShouldBeGreaterThan(0);
        processors.Busy.ShouldBeGreaterThan(0);
        processors.Kernel.ShouldBeGreaterThanOrEqualTo(processors.Idle);

        source.Read(processes, out var later).ShouldBeTrue();      // the buffer is used again
        processes.Count(p => p.Pid == (uint)Environment.ProcessId).ShouldBe(1);
        later.Busy.ShouldBeGreaterThanOrEqualTo(processors.Busy);
    }

    [Fact]
    public void A_process_is_opened_for_its_image_only_while_it_is_the_one_listed()
    {
        using var source = new WindowsProcesses();
        List<ProcessCounts> processes = [];
        source.Read(processes, out _).ShouldBeTrue();
        var me = processes.Single(p => p.Pid == (uint)Environment.ProcessId);

        var image = source.Image(me.Pid, me.CreateTime).ShouldNotBeNull();
        image.Path.ShouldBe(Environment.ProcessPath, StringCompareShould.IgnoreCase);
        image.PackageFamily.ShouldBeNull();
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            image.UserSid.ShouldBe(identity.User?.Value);              // review item 10: the account it runs as
        source.Image(me.Pid, me.CreateTime + 1).ShouldBeNull();    // the id names another process now
    }

    /// <summary>Review item 15: a second dispose must not free the buffers again, and a read after one answers nothing.</summary>
    [Fact]
    public void Disposing_twice_frees_nothing_twice_and_a_read_after_answers_nothing()
    {
        var source = new WindowsProcesses();
        List<ProcessCounts> processes = [];
        source.Read(processes, out _).ShouldBeTrue();
        source.Dispose();
        source.Dispose();
        source.Read(processes, out _).ShouldBeFalse();
        processes.ShouldBeEmpty();
    }

    [Fact]
    public void An_image_is_named_by_its_description_or_else_its_file_name()
    {
        using var source = new WindowsProcesses();
        source.DisplayName(Path.Combine(Environment.SystemDirectory, "kernel32.dll")).ShouldBe("Windows NT BASE API Client DLL");
        var plain = Path.Combine(Path.GetTempPath(), $"plain-{Guid.NewGuid():N}.exe");
        File.WriteAllText(plain, "not a program");
        try
        {
            source.DisplayName(plain).ShouldBe(Path.GetFileNameWithoutExtension(plain));
        }
        finally
        {
            File.Delete(plain);
        }
        source.DisplayName(@"C:\nowhere\gone.exe").ShouldBe("gone");
        source.WindowsFolder.ShouldBe(Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant().TrimEnd('\\') + "\\");
    }
}
