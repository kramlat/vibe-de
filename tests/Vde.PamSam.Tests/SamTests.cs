using Vde.PamSam;
using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Vde.PamSam.Tests;

public sealed class AccountFiles : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pamsam-" + Guid.NewGuid().ToString("N"));
    public string Passwd => Path.Combine(Dir, "passwd");
    public string Group => Path.Combine(Dir, "group");
    public string Shadow => Path.Combine(Dir, "shadow");

    public AccountFiles()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Passwd, "root:x:0:0:root:/root:/bin/bash\nalice:x:1000:1000:Alice Liddell,,,:/home/alice:/bin/zsh\nbob:x:1001:1001:Bob:/home/bob:/bin/bash\n");
        File.WriteAllText(Group, "root:x:0:\nwheel:x:10:alice\nalice:x:1000:\nbob:x:1001:\nusers:x:100:alice,bob\n");
        File.WriteAllText(Shadow, "root:!::::::\nalice:$6$salt$HASHHASHHASH:19000:0:99999:7:::\nbob:!:19500:0:90:7::20000:\n");
    }

    public void Dispose() => Directory.Delete(Dir, recursive: true);
}

[Collection("one at a time")]   // HiveServer.Listen sets a process-wide umask for a moment; other tests must not create folders then
public class SamBuilderTests
{
    [Fact]
    public void The_SAM_hive_has_the_accounts_groups_and_domain_in_the_windows_layout()
    {
        using var files = new AccountFiles();
        var hive = new Hive("SAM");
        SamBuilder.Fill(hive, new AccountDirectory(files.Passwd, files.Group), files.Shadow, "tower");

        var domain = @"Domains\Account";
        Assert.Equal("tower", hive.GetValue(domain, "Name")!.Data);
        Assert.Equal(3, hive.GetValue(domain, "UserCount")!.Data);
        Assert.Equal(new[] { "alice", "bob", "root" }, hive.GetSubKeyNames(domain + @"\Users"));

        var alice = domain + @"\Users\alice";
        Assert.Equal(1000L, hive.GetValue(alice, "Uid")!.Data);
        Assert.Equal(RegistryValueKind.Int64, hive.GetValue(alice, "Uid")!.Kind);   // a number is a number, not a DWORD
        Assert.Equal("Alice Liddell", hive.GetValue(alice, "FullName")!.Data);
        Assert.Equal("/bin/zsh", hive.GetValue(alice, "Shell")!.Data);
        Assert.Equal(true, hive.GetValue(alice, "Administrator")!.Data);               // in wheel
        Assert.Equal(false, hive.GetValue(domain + @"\Users\bob", "Administrator")!.Data);
        Assert.Equal(new[] { "alice", "users", "wheel" }, (string[])hive.GetValue(alice, "Groups")!.Data!);

        Assert.Equal(new[] { "alice", "bob", "root", "users", "wheel" }, hive.GetSubKeyNames(domain + @"\Aliases"));
        Assert.Equal(new[] { "alice" }, (string[])hive.GetValue(domain + @"\Aliases\wheel", "Members")!.Data!);
    }

    [Fact]
    public void Password_facts_come_from_shadow_never_the_hash_and_only_the_account_and_root_can_read_them()
    {
        using var files = new AccountFiles();
        var hive = new Hive("SAM") { Identity = RegistryIdentity.Root };   // what the service itself is: it reads everything
        SamBuilder.Fill(hive, new AccountDirectory(files.Passwd, files.Group), files.Shadow);
        var alice = @"Domains\Account\Users\alice";
        var bob = @"Domains\Account\Users\bob";

        Assert.Equal("Set", hive.GetValue(alice, "PasswordState")!.Data);
        Assert.Equal("Locked", hive.GetValue(bob, "PasswordState")!.Data);
        Assert.Equal(DateTime.UnixEpoch.AddDays(19000), hive.GetValue(alice, "PasswordLastSet")!.Data);
        Assert.Equal(DateTime.UnixEpoch.AddDays(19500 + 90), hive.GetValue(bob, "PasswordExpires")!.Data);
        Assert.Equal(DateTime.UnixEpoch.AddDays(20000), hive.GetValue(bob, "AccountExpires")!.Data);
        Assert.DoesNotContain(hive.GetValues(alice), v => v.Data is string s && s.Contains("HASH"));

        var asAlice = hive.As(new RegistryIdentity(1000, 1000, [1000]));
        var asBob = hive.As(new RegistryIdentity(1001, 1001, [1001]));
        Assert.Equal("Set", asAlice.GetValue(alice, "PasswordState")!.Data);                          // her own
        Assert.Equal("Alice Liddell", asBob.GetValue(alice, "FullName")!.Data);                       // public facts for everyone
        Assert.Throws<UnauthorizedAccessException>(() => asBob.GetValue(alice, "PasswordState"));     // not someone else's
        Assert.Throws<UnauthorizedAccessException>(() => asBob.SetValue(bob, "Shell", RegistryValueKind.String, "/bin/evil"));   // nobody edits accounts here
    }

    [Fact]
    public void Without_shadow_access_the_rest_is_still_there_and_a_refill_replaces_the_old_content()
    {
        using var files = new AccountFiles();
        var hive = new Hive("SAM");
        var accounts = new AccountDirectory(files.Passwd, files.Group);
        SamBuilder.Fill(hive, accounts, Path.Combine(files.Dir, "no-such-shadow"));
        Assert.Null(hive.GetValue(@"Domains\Account\Users\alice", "PasswordState"));
        Assert.Equal("alice", hive.GetSubKeyNames(@"Domains\Account\Users")[0]);

        File.AppendAllText(files.Passwd, "carol:x:1002:1002:Carol:/home/carol:/bin/sh\n");
        SamBuilder.Fill(hive, accounts, files.Shadow);
        Assert.Contains("carol", hive.GetSubKeyNames(@"Domains\Account\Users"));
        Assert.Equal(4, hive.GetValue(@"Domains\Account", "UserCount")!.Data);
    }
}

[Collection("one at a time")]   // HiveServer.Listen sets a process-wide umask for a moment; other tests must not create folders then
public class SamServiceTests
{
    [Fact]
    public void A_client_mounts_the_served_SAM_as_a_key_of_HKLM_and_is_seen_by_its_own_uid()
    {
        using var files = new AccountFiles();
        var socket = Path.Combine(files.Dir, "run", "sam.sock");
        uint callerUid = 1001;   // the "kernel's" answer in this test: bob is connecting
        using var service = new SamService(new AccountDirectory(files.Passwd, files.Group), files.Passwd, files.Group, files.Shadow,
            identityOf: _ => new RegistryIdentity(callerUid, callerUid, [callerUid]));
        service.Server.Listen(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => service.Server.RunAsync(stop.Token));

        var system = new Hive("HKLM");
        system.SetValue(@"Software\Vde", "Version", RegistryValueKind.String, "1");
        using var hklm = new MountedHive(system, new Dictionary<string, IHive> { ["SAM"] = RemoteHive.Connect(socket) });

        Assert.Contains("SAM", hklm.GetSubKeyNames(""));
        Assert.Contains("Software", hklm.GetSubKeyNames(""));
        Assert.Equal("1", hklm.GetValue(@"Software\Vde", "Version")!.Data);                         // the rest of HKLM is untouched
        Assert.Equal(new[] { "alice", "bob", "root" }, hklm.GetSubKeyNames(@"SAM\Domains\Account\Users"));
        Assert.Equal("/home/bob", hklm.GetValue(@"SAM\Domains\Account\Users\bob", "Home")!.Data);
        Assert.Equal("Locked", hklm.GetValue(@"SAM\Domains\Account\Users\bob", "PasswordState")!.Data);       // bob's own
        Assert.Throws<UnauthorizedAccessException>(() => hklm.GetValue(@"SAM\Domains\Account\Users\alice", "PasswordState"));
        Assert.True(hklm.KeyExists("SAM"));
        Assert.Equal(system.GetKeyInfo("").SubKeyCount + 1, hklm.GetKeyInfo("").SubKeyCount);

        stop.Cancel();
    }
}

[Collection("one at a time")]   // HiveServer.Listen sets a process-wide umask for a moment; other tests must not create folders then
public class MountedHiveTests
{
    [Fact]
    public void A_mount_hides_a_same_named_key_and_changes_below_it_are_reported_with_its_name()
    {
        var baseHive = new Hive("HKLM");
        baseHive.SetValue("SAM", "Shadowed", RegistryValueKind.String, "x");
        var mounted = new Hive("SAM");
        mounted.SetValue(@"Domains", "V", RegistryValueKind.Int32, 1);
        var hklm = new MountedHive(baseHive, new Dictionary<string, IHive> { ["sam"] = mounted });

        Assert.Null(hklm.GetValue("SAM", "Shadowed"));
        Assert.Equal(1, hklm.GetValue(@"SAM\Domains", "V")!.Data);
        string? seen = null;
        hklm.Changed += (_, c) => seen = c.KeyPath;
        mounted.SetValue(@"Domains", "V", RegistryValueKind.Int32, 2);
        Assert.Equal("sam\\Domains", seen);
        hklm.SetValue("Other", "N", RegistryValueKind.Int32, 3);   // not mounted: the base hive
        Assert.Equal(3, baseHive.GetValue("Other", "N")!.Data);
    }
}
