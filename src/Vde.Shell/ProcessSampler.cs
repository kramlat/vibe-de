using System.Globalization;

namespace Vde.Shell;

// One process as /proc describes it.
public sealed record ProcessInfo(int Pid, int ParentPid, string Name, string Command, char State, int Threads, long MemoryBytes, double CpuPercent, string User);

// What the whole machine is doing right now (the Performance tab).
public sealed record MachineInfo(double CpuPercent, long MemoryTotal, long MemoryUsed, int Processes, int Threads, TimeSpan Uptime, int Cpus);

// Reads /proc twice and subtracts: CPU is the share of the machine's cpu time a process used between two samples
// (100% = one whole core, like top's default). `root` is /proc, replaceable so tests can bring their own tree.
public sealed class ProcessSampler
{
    private readonly string _root;
    private readonly int _pageSize;
    private readonly Dictionary<int, long> _lastTicks = new();
    private long _lastTotal;
    private long _lastBusy;
    private readonly Dictionary<int, string> _users = new();

    public ProcessSampler(string root = "/proc", int pageSize = 4096) { _root = root; _pageSize = pageSize; }

    public int Cpus { get; private set; } = Math.Max(1, Environment.ProcessorCount);

    public (IReadOnlyList<ProcessInfo> Processes, MachineInfo Machine) Sample()
    {
        var (total, busy, cpus) = ReadCpu();
        if (cpus > 0) Cpus = cpus;
        long dTotal = total - _lastTotal;
        double scale = dTotal > 0 ? (double)Cpus / dTotal * 100 : 0;
        double machineCpu = dTotal > 0 && _lastTotal > 0 ? Math.Clamp((double)(busy - _lastBusy) / dTotal * 100, 0, 100) : 0;
        bool first = _lastTotal == 0;
        _lastTotal = total; _lastBusy = busy;

        var list = new List<ProcessInfo>();
        var seen = new HashSet<int>();
        int threads = 0;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(_root); } catch (IOException) { dirs = []; }
        foreach (var dir in dirs)
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) continue;
            var info = ReadProcess(dir, pid, scale, first, out long ticks);
            if (info is null) continue;
            seen.Add(pid);
            _lastTicks[pid] = ticks;
            threads += info.Threads;
            list.Add(info);
        }
        foreach (var gone in _lastTicks.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _lastTicks.Remove(gone);
            _users.Remove(gone);
        }

        var (memTotal, memUsed) = ReadMemory();
        return (list, new MachineInfo(machineCpu, memTotal, memUsed, list.Count, threads, ReadUptime(), Cpus));
    }

    private ProcessInfo? ReadProcess(string dir, int pid, double scale, bool first, out long ticks)
    {
        ticks = 0;
        string stat;
        try { stat = File.ReadAllText(Path.Combine(dir, "stat")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        // "pid (comm) S ppid ..." — comm may hold spaces and parentheses, so cut at the last ')'
        int open = stat.IndexOf('('), close = stat.LastIndexOf(')');
        if (open < 0 || close < open) return null;
        string comm = stat[(open + 1)..close];
        var f = stat[(close + 2)..].Split(' ');
        if (f.Length < 22) return null;   // fields after comm: [0]=state [1]=ppid ... [11]=utime [12]=stime [17]=threads [21]=rss
        if (!long.TryParse(f[11], out long ut) || !long.TryParse(f[12], out long st)) return null;
        ticks = ut + st;
        _ = int.TryParse(f[1], out int ppid);
        _ = int.TryParse(f[17], out int threads);
        _ = long.TryParse(f[21], out long rss);
        double cpu = !first && _lastTicks.TryGetValue(pid, out long before) && ticks >= before ? (ticks - before) * scale : 0;
        // cpu ticks and the total are both in jiffies, so the ratio needs no clock-tick constant
        return new ProcessInfo(pid, ppid, comm, ReadCommand(dir) ?? comm, f[0].Length > 0 ? f[0][0] : '?', threads, rss * _pageSize, cpu, UserOf(dir, pid));
    }

    private static string? ReadCommand(string dir)
    {
        try
        {
            var raw = File.ReadAllText(Path.Combine(dir, "cmdline"));
            var text = raw.Replace('\0', ' ').Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private string UserOf(string dir, int pid)
    {
        if (_users.TryGetValue(pid, out var known)) return known;
        string user = "";
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(dir, "status")))
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    var uid = line[4..].Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    user = UserName(uid);
                    break;
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _users[pid] = user;
        return user;
    }

    private static Dictionary<string, string>? _passwd;
    private static string UserName(string uid)
    {
        if (_passwd is null)
        {
            var map = new Dictionary<string, string>();
            try
            {
                foreach (var line in File.ReadLines("/etc/passwd"))
                {
                    var p = line.Split(':');
                    if (p.Length > 2) map.TryAdd(p[2], p[0]);
                }
            }
            catch (IOException) { }
            _passwd = map;
        }
        return _passwd.TryGetValue(uid, out var name) ? name : uid;
    }

    private (long Total, long Busy, int Cpus) ReadCpu()
    {
        try
        {
            long total = 0, busy = 0; int cpus = 0;
            foreach (var line in File.ReadLines(Path.Combine(_root, "stat")))
            {
                if (line.StartsWith("cpu ", StringComparison.Ordinal))
                {
                    var n = line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var v) ? v : 0).ToArray();
                    total = n.Take(8).Sum();   // user nice system idle iowait irq softirq steal (guest is already in user)
                    long idle = (n.Length > 3 ? n[3] : 0) + (n.Length > 4 ? n[4] : 0);
                    busy = total - idle;
                }
                else if (line.StartsWith("cpu", StringComparison.Ordinal)) cpus++;
            }
            return (total, busy, cpus);
        }
        catch (IOException) { return (0, 0, 0); }
    }

    private (long Total, long Used) ReadMemory()
    {
        try
        {
            long total = 0, available = 0;
            foreach (var line in File.ReadLines(Path.Combine(_root, "meminfo")))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = KiloBytes(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = KiloBytes(line);
            }
            return (total, Math.Max(0, total - available));
        }
        catch (IOException) { return (0, 0); }
    }

    private static long KiloBytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && long.TryParse(parts[1], out var kb) ? kb * 1024 : 0;
    }

    private TimeSpan ReadUptime()
    {
        try
        {
            var first = File.ReadAllText(Path.Combine(_root, "uptime")).Split(' ')[0];
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : TimeSpan.Zero;
        }
        catch (IOException) { return TimeSpan.Zero; }
    }
}
