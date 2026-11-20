using Vde.Shell;
using Xunit;

namespace Vde.Shell.Tests;

public class ProcessSamplerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vde-proc").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private void Stat(long busy, long idle) =>
        File.WriteAllText(Path.Combine(_root, "stat"), $"cpu  {busy} 0 0 {idle} 0 0 0 0 0 0\ncpu0 {busy / 2} 0 0 {idle / 2} 0 0 0 0 0 0\ncpu1 {busy / 2} 0 0 {idle / 2} 0 0 0 0 0 0\n");

    private void Proc(int pid, string comm, long ticks, long rssPages, string cmdline = "")
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, pid.ToString())).FullName;
        // after the name: state ppid pgrp session tty tpgid flags minflt cminflt majflt cmajflt utime stime ... threads(17) ... rss(21)
        File.WriteAllText(Path.Combine(dir, "stat"), $"{pid} ({comm}) S 1 1 1 0 -1 0 0 0 0 0 {ticks} 0 0 0 20 0 3 0 100 0 {rssPages} 0\n");
        File.WriteAllText(Path.Combine(dir, "cmdline"), cmdline);
        File.WriteAllText(Path.Combine(dir, "status"), "Name:\tx\nUid:\t0\t0\t0\t0\n");
    }

    [Fact]
    public void Reads_name_memory_threads_and_command()
    {
        Stat(100, 900);
        File.WriteAllText(Path.Combine(_root, "meminfo"), "MemTotal:  1000 kB\nMemAvailable:  250 kB\n");
        File.WriteAllText(Path.Combine(_root, "uptime"), "90.5 10.0\n");
        Proc(42, "my (odd) name", 10, 5, "/bin/x\0--flag\0");
        var (list, machine) = new ProcessSampler(_root, 1024).Sample();
        var p = Assert.Single(list);
        Assert.Equal("my (odd) name", p.Name);
        Assert.Equal(42, p.Pid);
        Assert.Equal(1, p.ParentPid);
        Assert.Equal(3, p.Threads);
        Assert.Equal(5 * 1024, p.MemoryBytes);
        Assert.Equal("/bin/x --flag", p.Command);
        Assert.Equal("root", p.User);
        Assert.Equal(1000 * 1024, machine.MemoryTotal);
        Assert.Equal(750 * 1024, machine.MemoryUsed);
        Assert.Equal(2, machine.Cpus);
        Assert.Equal(TimeSpan.FromSeconds(90.5), machine.Uptime);
    }

    [Fact]
    public void Cpu_is_the_share_between_two_samples()
    {
        Stat(100, 900);
        Proc(7, "busy", 0, 1);
        var sampler = new ProcessSampler(_root);
        Assert.Equal(0, sampler.Sample().Processes[0].CpuPercent);   // nothing to subtract from yet
        Stat(300, 1700);   // 1000 jiffies passed, 200 of them busy: 20% of the machine
        Proc(7, "busy", 100, 1);   // this one used 100 of the 1000 across 2 cpus: 20% of one core... of two = 0.2 cores
        var (list, machine) = sampler.Sample();
        Assert.Equal(20, machine.CpuPercent, 1);
        Assert.Equal(20, list[0].CpuPercent, 1);
    }

    [Fact]
    public void A_process_that_has_gone_is_dropped_and_junk_directories_are_ignored()
    {
        Stat(1, 1);
        Proc(1, "a", 0, 0);
        Proc(2, "b", 0, 0);
        Directory.CreateDirectory(Path.Combine(_root, "self"));
        File.WriteAllText(Path.Combine(_root, "3"), "not a directory");
        var sampler = new ProcessSampler(_root);
        Assert.Equal(2, sampler.Sample().Processes.Count);
        Directory.Delete(Path.Combine(_root, "2"), true);
        Assert.Single(sampler.Sample().Processes);
    }

    [Fact]
    public void Missing_proc_is_an_empty_machine_not_a_crash()
    {
        var (list, machine) = new ProcessSampler(Path.Combine(_root, "nope")).Sample();
        Assert.Empty(list);
        Assert.Equal(0, machine.MemoryTotal);
    }
}
