using Vibe.Registry;

namespace Vibe.Registry.Tests;

public class ClassesRootTests
{
    private static (Hive user, Hive system, ClassesRoot hkcr) Build()
    {
        var user = new Hive("HKCU");
        var system = new Hive("HKLM");
        return (user, system, new ClassesRoot(user, system));
    }

    [Fact]
    public void Reads_merge_both_hives_and_the_user_wins()
    {
        var (user, system, hkcr) = Build();
        system.SetValue(@"Software\Classes\.txt", "", RegistryValueKind.String, "system");
        system.SetValue(@"Software\Classes\.txt", "OnlySystem", RegistryValueKind.DWord, 1u);
        user.SetValue(@"Software\Classes\.txt", "", RegistryValueKind.String, "user");
        system.CreateKey(@"Software\Classes\.md");
        user.CreateKey(@"Software\Classes\.log");

        Assert.Equal("user", hkcr.GetValue(".txt", "")!.Data);
        Assert.Equal(1u, hkcr.GetValue(".txt", "OnlySystem")!.Data);
        Assert.Equal(new[] { "", "OnlySystem" }, hkcr.GetValues(".txt").Select(v => v.Name));
        Assert.Equal("user", hkcr.GetValues(".txt").Single(v => v.Name == "").Data);
        Assert.Equal(new[] { ".log", ".md", ".txt" }, hkcr.GetSubKeyNames(""));
        Assert.True(hkcr.KeyExists(".md"));
        Assert.False(hkcr.KeyExists(".nope"));
        Assert.Throws<KeyNotFoundException>(() => hkcr.GetSubKeyNames(".nope"));
    }

    [Fact]
    public void Writes_and_deletes_only_touch_the_user_hive()
    {
        var (user, system, hkcr) = Build();
        system.SetValue(@"Software\Classes\.txt", "v", RegistryValueKind.String, "system");

        hkcr.SetValue(".txt", "v", RegistryValueKind.String, "mine");
        Assert.Equal("mine", user.GetValue(@"Software\Classes\.txt", "v")!.Data);
        Assert.Equal("system", system.GetValue(@"Software\Classes\.txt", "v")!.Data);

        Assert.True(hkcr.DeleteValue(".txt", "v"));
        Assert.Equal("system", hkcr.GetValue(".txt", "v")!.Data);   // the system value shows through again
        Assert.False(hkcr.DeleteValue(".txt", "v"));
    }

    [Fact]
    public void Works_without_a_system_hive()
    {
        var user = new Hive("HKCU");
        var hkcr = new ClassesRoot(user);
        hkcr.SetValue("a", "v", RegistryValueKind.Bool, true);
        Assert.Equal(true, hkcr.GetValue("a", "v")!.Data);
    }

    [Fact]
    public void Changes_are_relayed_with_paths_relative_to_HKCR_and_other_keys_are_ignored()
    {
        var (user, system, hkcr) = Build();
        var seen = new List<RegistryChange>();
        hkcr.Changed += (_, c) => seen.Add(c);

        user.SetValue(@"Software\Classes\MIME\text/plain", "Default", RegistryValueKind.String, "x");
        system.SetValue(@"Software\Classes\.txt", "v", RegistryValueKind.Bool, true);
        user.SetValue(@"Software\Other", "v", RegistryValueKind.Bool, true);

        Assert.Equal(new[]
        {
            new RegistryChange(RegistryChangeKind.KeyCreated, "MIME"),
            new RegistryChange(RegistryChangeKind.KeyCreated, @"MIME\text/plain"),
            new RegistryChange(RegistryChangeKind.ValueSet, @"MIME\text/plain", "Default"),
            new RegistryChange(RegistryChangeKind.KeyCreated, ".txt"),
            new RegistryChange(RegistryChangeKind.ValueSet, ".txt", "v"),
        }, seen);
    }

    [Fact]
    public void Store_saves_only_dirty_hives_and_reloads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vibe-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sys = Path.Combine(dir, "sys", "system.fhive");
            var usr = Path.Combine(dir, "usr", "user.fhive");
            var store = RegistryStore.Load(sys, usr);
            store.ClassesRoot.SetValue(".txt", "Content Type", RegistryValueKind.String, "text/plain");

            store.Save();

            Assert.True(File.Exists(usr));
            Assert.False(File.Exists(sys));   // nothing changed there
            Assert.Equal("text/plain", RegistryStore.Load(sys, usr).ClassesRoot.GetValue(".txt", "Content Type")!.Data);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
