using PowerLedger.Sensors;

namespace PowerLedger.Service.Tests;

/// <summary>Processes from a script: each read, every one still running uses its cycles and time per read, and the
/// processors are as busy as they were plus whatever closed programs and drivers took.</summary>
internal sealed class FakeProcesses : IProcessSource
{
    private readonly Lock _gate = new();
    private ProcessorTimes _times = new(Idle: 1_000_000, Kernel: 1_000_000, User: 0, Dpc: 0, Interrupt: 0);

    public sealed class Fake(uint pid, string name, uint session = 1, string? path = null, string? package = null, long created = 1, string? sid = null)
    {
        public uint Pid { get; } = pid;
        public long Created { get; } = created;
        public uint Session { get; } = session;
        public string Name { get; } = name;
        public string? Path { get; } = path;
        public string? Package { get; } = package;
        public string? Sid { get; } = sid;
        public ulong CyclesPerRead { get; set; }
        public long TimePerRead { get; set; }
        public ulong Cycles { get; set; }
        public long Time { get; set; }
        public bool Running { get; set; } = true;
    }

    public List<Fake> Processes { get; } = [];

    /// <summary>Busy time each read that no process running explains, as a program that started and ended between reads.</summary>
    public long ClosedPerRead { get; set; }

    /// <summary>Drivers' interrupts and deferred calls each read.</summary>
    public long DriversPerRead { get; set; }

    /// <summary>While set, Windows will not list the processes.</summary>
    public bool Failing { get; set; }

    public int Reads { get; private set; }

    public int Opens { get; private set; }

    public bool Disposed { get; private set; }

    public Fake Add(Fake process, ulong cyclesPerRead = 0, long timePerRead = 0)
    {
        lock (_gate)
        {
            process.CyclesPerRead = cyclesPerRead;
            process.TimePerRead = timePerRead;
            Processes.Add(process);
            return process;
        }
    }

    public bool Read(List<ProcessCounts> into, out ProcessorTimes processors)
    {
        lock (_gate)
        {
            Reads++;
            into.Clear();
            processors = _times;
            if (Failing) return false;
            var busy = ClosedPerRead + DriversPerRead;
            for (var i = 0; i < Processes.Count; i++)
            {
                var p = Processes[i];
                if (!p.Running) continue;
                p.Cycles += p.CyclesPerRead;
                p.Time += p.TimePerRead;
                busy += p.TimePerRead;
                into.Add(new ProcessCounts(p.Pid, p.Created, p.Session, p.Cycles, p.Time, i));
            }
            _times = _times with { Kernel = _times.Kernel + busy, Dpc = _times.Dpc + DriversPerRead };
            processors = _times;
            return true;
        }
    }

    public string ImageName(int name)
    {
        lock (_gate) return Processes[name].Name;
    }

    public ProcessImage? Image(uint pid, long createTime)
    {
        lock (_gate)
        {
            Opens++;
            var p = Processes.FirstOrDefault(p => p.Pid == pid && p.Created == createTime && p.Running);
            return p is null || (p.Path is null && p.Package is null) ? null : new ProcessImage(p.Path, p.Package, p.Sid);
        }
    }

    public string DisplayName(string path) => "Name of " + System.IO.Path.GetFileNameWithoutExtension(path);

    public string WindowsFolder => @"c:\windows\";

    public void Dispose() => Disposed = true;
}
