using Vibe.Registry;

namespace Vibe.Registry.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibe-shared-" + Guid.NewGuid().ToString("N"));
    public string Hive => System.IO.Path.Combine(Path, "hive", "User.fhive");

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}

public class SharedHiveTests
{
    private static async Task<RegistryChange> Next(TaskCompletionSource<RegistryChange> source)
        => await source.Task.WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public void A_missing_file_starts_empty_and_the_first_write_creates_it()
    {
        using var dir = new TempDir();
        using var hive = Hive.OpenShared("HKCU", dir.Hive);

        Assert.True(hive.IsShared);
        Assert.False(File.Exists(dir.Hive));

        hive.SetValue(@"Software\Vde", "v", RegistryValueKind.DWord, 7u);

        Assert.True(File.Exists(dir.Hive));
        Assert.False(File.Exists(dir.Hive + ".tmp"));
        Assert.False(hive.IsDirty);
        using var again = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        Assert.Equal(7u, again.GetValue(@"Software\Vde", "v")!.Data);
    }

    [Fact]
    public void Two_handles_on_one_file_do_not_lose_each_others_updates()
    {
        using var dir = new TempDir();
        using var a = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        using var b = Hive.OpenShared("HKCU", dir.Hive, watch: false);

        a.SetValue("A", "x", RegistryValueKind.String, "from a");
        b.SetValue("B", "y", RegistryValueKind.String, "from b");   // b has never seen A: it must catch up before writing
        a.SetValue("A", "x2", RegistryValueKind.String, "again");

        using var reader = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        Assert.Equal("from a", reader.GetValue("A", "x")!.Data);
        Assert.Equal("from b", reader.GetValue("B", "y")!.Data);
        Assert.Equal("again", reader.GetValue("A", "x2")!.Data);
    }

    [Fact]
    public async Task Changes_from_another_handle_arrive_as_events()
    {
        using var dir = new TempDir();
        using var watcher = Hive.OpenShared("HKCU", dir.Hive);
        using var writer = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        var seen = new TaskCompletionSource<RegistryChange>();
        watcher.Changed += (_, c) => { if (c.Kind == RegistryChangeKind.ValueSet) seen.TrySetResult(c); };

        writer.SetValue(@"Software\Vde\Appearance", "Theme", RegistryValueKind.String, "simple");

        var change = await Next(seen);
        Assert.Equal(@"Software\Vde\Appearance", change.KeyPath);
        Assert.Equal("Theme", change.ValueName);
        Assert.Equal("simple", watcher.GetValue(@"Software\Vde\Appearance", "Theme")!.Data);
    }

    [Fact]
    public void Refresh_raises_one_event_per_difference()
    {
        using var dir = new TempDir();
        using var a = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        using var b = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        a.SetValue("Keep", "v", RegistryValueKind.Bool, true);
        a.SetValue("Gone\\Deep", "v", RegistryValueKind.Bool, true);
        b.Refresh();

        var seen = new List<RegistryChange>();
        b.Changed += (_, c) => seen.Add(c);
        a.DeleteKey("Gone");
        a.SetValue("Keep", "v", RegistryValueKind.Bool, false);
        a.SetValue("New\\Sub", "n", RegistryValueKind.Int32, 1);
        b.Refresh();

        Assert.Contains(new RegistryChange(RegistryChangeKind.KeyDeleted, "Gone"), seen);
        Assert.Contains(new RegistryChange(RegistryChangeKind.ValueSet, "Keep", "v"), seen);
        Assert.Contains(new RegistryChange(RegistryChangeKind.KeyCreated, @"New\Sub"), seen);
        Assert.Contains(new RegistryChange(RegistryChangeKind.ValueSet, @"New\Sub", "n"), seen);
        Assert.Equal(false, b.GetValue("Keep", "v")!.Data);
        Assert.False(b.KeyExists("Gone"));

        seen.Clear();
        b.Refresh();   // nothing new on disk
        Assert.Empty(seen);
    }

    [Fact]
    public void Many_writers_hammering_one_file_lose_nothing()
    {
        using var dir = new TempDir();
        const int writers = 8, each = 40;

        Parallel.For(0, writers, w =>
        {
            using var hive = Hive.OpenShared("HKCU", dir.Hive, watch: false);
            for (int i = 0; i < each; i++)
                hive.SetValue($"W{w}", $"v{i}", RegistryValueKind.Int32, i);
        });

        using var check = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        for (int w = 0; w < writers; w++)
            Assert.Equal(each, check.GetValues($"W{w}").Count);
    }

    [Fact]
    public void Read_only_hives_read_but_never_write()
    {
        using var dir = new TempDir();
        using (var writer = Hive.OpenShared("HKLM", dir.Hive, watch: false))
            writer.SetValue("Sys", "v", RegistryValueKind.String, "x");
        var before = File.ReadAllBytes(dir.Hive);

        using var readOnly = Hive.OpenShared("HKLM", dir.Hive, watch: false, readOnly: true);

        Assert.True(readOnly.IsReadOnly);
        Assert.Equal("x", readOnly.GetValue("Sys", "v")!.Data);
        Assert.Throws<UnauthorizedAccessException>(() => readOnly.SetValue("Sys", "v", RegistryValueKind.String, "y"));
        Assert.Throws<UnauthorizedAccessException>(() => readOnly.DeleteKey("Sys"));
        Assert.Equal(before, File.ReadAllBytes(dir.Hive));
    }

    [Fact]
    public void Volatile_keys_survive_a_refresh_in_the_process_that_made_them()
    {
        using var dir = new TempDir();
        using var a = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        using var b = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        a.SetValue("Run\\Session", "pid", RegistryValueKind.Int32, 42);
        a.SetKeyFlags("Run", RegistryKeyFlags.Volatile);

        b.SetValue("Other", "v", RegistryValueKind.Bool, true);   // writes the file
        a.Refresh();

        Assert.Equal(42, a.GetValue("Run\\Session", "pid")!.Data);   // still there in a
        Assert.True(a.KeyExists("Other"));                              // and a saw b's change
        Assert.False(b.KeyExists("Run"));                               // never on disk, so b never had it
    }

    [Fact]
    public void A_corrupt_file_is_never_overwritten()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.GetDirectoryName(dir.Hive)!);
        File.WriteAllText(dir.Hive, "this is not a hive");

        Assert.Throws<InvalidDataException>(() => Hive.OpenShared("HKCU", dir.Hive, watch: false));
        Assert.Equal("this is not a hive", File.ReadAllText(dir.Hive));
    }

    [Fact]
    public void A_hive_that_goes_corrupt_later_refuses_writes_and_keeps_the_file()
    {
        using var dir = new TempDir();
        using var hive = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        hive.SetValue("A", "v", RegistryValueKind.Bool, true);
        File.WriteAllText(dir.Hive, "damaged by something else");

        Assert.Throws<InvalidDataException>(() => hive.SetValue("A", "w", RegistryValueKind.Bool, true));
        Assert.Equal("damaged by something else", File.ReadAllText(dir.Hive));
    }

    [Fact]
    public void OpenDefault_uses_the_given_paths_and_the_system_hive_is_read_only()
    {
        using var dir = new TempDir();
        var system = Path.Combine(dir.Path, "etc", "hive", "System.fhive");
        using (var seed = Hive.OpenShared("HKLM", system, watch: false))
            seed.SetValue(@"Software\Classes\.txt", "Content Type", RegistryValueKind.String, "text/plain");

        using var store = RegistryStore.OpenDefault(dir.Hive, system, watch: false);

        Assert.True(store.LocalMachine.IsReadOnly);
        Assert.False(store.CurrentUser.IsReadOnly);
        Assert.Equal("text/plain", store.ClassesRoot.GetValue(".txt", "Content Type")!.Data);   // HKCR sees the system hive
        store.ClassesRoot.SetValue(".txt", "Content Type", RegistryValueKind.String, "text/x-mine");   // goes to HKCU
        Assert.Equal("text/x-mine", store.ClassesRoot.GetValue(".txt", "Content Type")!.Data);
        Assert.True(File.Exists(dir.Hive));
    }
}
