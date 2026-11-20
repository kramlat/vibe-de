namespace Vibe.Registry.Tests;

public class VirtualRootTests
{
    [Fact]
    public void An_alias_root_always_exists_and_reads_and_writes_the_key_it_stands_for()
    {
        var machine = new Hive("HKLM");
        IHive config = new HiveAlias("HKCC", machine, @"System\HardwareProfiles\Current");
        Assert.True(config.KeyExists(""));
        Assert.Empty(config.GetSubKeyNames(""));
        Assert.Empty(config.GetValues(""));

        string? seen = null;
        config.Changed += (_, c) => seen = c.KeyPath;
        config.SetValue(@"Display", "Dpi", RegistryValueKind.Int32, 96);
        Assert.Equal(96, machine.GetValue(@"System\HardwareProfiles\Current\Display", "Dpi")!.Data);
        Assert.Equal(["Display"], config.GetSubKeyNames(""));
        Assert.Equal(1, config.GetKeyInfo("").SubKeyCount);
        Assert.Equal("Display", seen);
        Assert.Throws<UnauthorizedAccessException>(() => config.DeleteKey(""));
    }

    [Fact]
    public void HKU_lists_default_and_own_profile_and_passes_through_to_them()
    {
        var user = new Hive("HKCU");
        var machine = new Hive("HKLM");
        IHive users = new UsersRoot(user, machine, "1000", privileged: false);

        Assert.Equal(".DEFAULT", Assert.Single(users.GetSubKeyNames(""), n => n.StartsWith('.')));
        Assert.Equal(new[] { ".DEFAULT", "1000" }, users.GetSubKeyNames(""));
        Assert.Equal(2, users.GetKeyInfo("").SubKeyCount);
        Assert.True(users.KeyExists(@"1000"));

        users.SetValue(@"1000\Software\X", "V", RegistryValueKind.Bool, true);
        Assert.Equal(true, user.GetValue(@"Software\X", "V")!.Data);
        users.SetValue(@".DEFAULT\Software\X", "V", RegistryValueKind.Bool, false);
        Assert.Equal(false, machine.GetValue(@"Users\.DEFAULT\Software\X", "V")!.Data);

        Assert.Throws<UnauthorizedAccessException>(() => users.SetValue("", "V", RegistryValueKind.Bool, true));   // the root holds no values
        Assert.Throws<UnauthorizedAccessException>(() => users.CreateKey("2000"));                                  // profiles cannot be added
        Assert.Throws<UnauthorizedAccessException>(() => users.DeleteKey("1000"));
    }

    [Fact]
    public void Only_root_sees_and_opens_other_users_profiles()
    {
        var other = new Hive("HKU2000");
        other.SetValue(@"Software\Theirs", "Secret", RegistryValueKind.String, "x");
        Func<IReadOnlyDictionary<string, IHive>> discover = () => new Dictionary<string, IHive> { ["2000"] = other };

        var ordinary = new UsersRoot(new Hive("HKCU"), new Hive("HKLM"), "1000", privileged: false, discover);
        Assert.DoesNotContain("2000", ordinary.GetSubKeyNames(""));
        Assert.False(ordinary.KeyExists(@"2000\Software\Theirs"));
        Assert.Throws<UnauthorizedAccessException>(() => ordinary.GetValues(@"2000\Software\Theirs"));

        var root = new UsersRoot(new Hive("HKCU"), new Hive("HKLM"), "0", privileged: true, discover);
        Assert.Equal(new[] { ".DEFAULT", "0", "2000" }, root.GetSubKeyNames(""));
        Assert.Equal("x", root.GetValue(@"2000\Software\Theirs", "Secret")!.Data);
    }
}

public class EmptyRootTests
{
    [Fact]
    public void HKPD_and_HKDD_exist_are_empty_and_refuse_changes()
    {
        foreach (var name in new[] { EmptyRoot.PerformanceDataName, EmptyRoot.DynDataName })
        {
            IHive root = new EmptyRoot(name);
            Assert.True(root.KeyExists(""));
            Assert.False(root.KeyExists("Anything"));
            Assert.Empty(root.GetSubKeyNames(""));
            Assert.Empty(root.GetValues(""));
            Assert.Equal(0, root.GetKeyInfo("").SubKeyCount);
            Assert.Throws<KeyNotFoundException>(() => root.GetSubKeyNames("Anything"));
            Assert.Throws<UnauthorizedAccessException>(() => root.CreateKey("Anything"));
            Assert.Throws<UnauthorizedAccessException>(() => root.SetValue("", "V", RegistryValueKind.Bool, true));
        }
    }
}
