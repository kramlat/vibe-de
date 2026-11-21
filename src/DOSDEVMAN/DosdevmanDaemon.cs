using Vde.Shell;
using Vibe.Registry;

namespace Dosdevman;

// The service: writes the links Wine reads at start, then again whenever the map changes in the registry (the shell, the tool or the user
// changed a letter) or a drive is mounted or unmounted. Runs as a systemd user unit.
public sealed class DosdevmanDaemon(DosDevices dos, IHive? hive, string dosdevices, TextWriter log)
{
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);
    public Func<string> MountTable { get; init; } = () => File.Exists("/proc/self/mountinfo") ? File.ReadAllText("/proc/self/mountinfo") : "";

    public async Task<int> RunAsync(CancellationToken cancel)
    {
        var dirty = 1;   // sync once at start
        void Changed(object? s, RegistryChange c) { if (c.IsUnder(DosDeviceStore.KeyPath)) Interlocked.Exchange(ref dirty, 1); }
        if (hive is not null) hive.Changed += Changed;
        var mounts = MountTable();
        log.WriteLine("dosdevman: started");
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var table = MountTable();
                if (table != mounts) { mounts = table; Interlocked.Exchange(ref dirty, 1); }
                if (Interlocked.Exchange(ref dirty, 0) == 1)
                    try { foreach (var line in dos.SyncAll(dosdevices)) log.WriteLine("dosdevman: " + line); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.WriteLine("dosdevman: " + e.Message); }
                try { await Task.Delay(Poll, cancel); } catch (OperationCanceledException) { }
            }
        }
        finally { if (hive is not null) hive.Changed -= Changed; }
        log.WriteLine("dosdevman: stopped");
        return 0;
    }
}
