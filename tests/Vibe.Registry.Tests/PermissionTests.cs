using Vibe.Registry;

namespace Vibe.Registry.Tests;

public class PermissionTests
{
    // Alice and Bob share group 100; Carol shares nothing.
    private static readonly RegistryIdentity Alice = new(1000, 1000, [100]);
    private static readonly RegistryIdentity Bob = new(1001, 1001, [100]);
    private static readonly RegistryIdentity Carol = new(1002, 1002);
    private static readonly RegistryIdentity Root = RegistryIdentity.Root;

    private static ushort M(string octal) => PosixPermissions.ParseMode(octal);

    // A hive whose root anyone may create in, like /tmp.
    private static Hive Make()
    {
        var hive = new Hive("t") { Identity = Root };
        hive.SetKeyPermissions("", new PosixPermissions(0, 0, M("777")));
        return hive;
    }

    [Fact]
    public void New_objects_belong_to_their_creator_with_default_modes()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.SetValue(@"A\B", "v", RegistryValueKind.Bool, true);

        Assert.Equal(new PosixPermissions(1000, 1000, M("755")), hive.GetKeyPermissions("A"));
        Assert.Equal(new PosixPermissions(1000, 1000, M("755")), hive.GetKeyPermissions(@"A\B"));
        Assert.Equal(new PosixPermissions(1000, 1000, M("644")), hive.GetValuePermissions(@"A\B", "v"));
        Assert.Equal("0644", hive.GetValuePermissions(@"A\B", "v")!.Value.ModeText);
        Assert.Equal("rw-r--r--", hive.GetValuePermissions(@"A\B", "v")!.Value.Symbolic);
    }

    [Fact]
    public void Umask_trims_the_default_modes()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.Umask = M("077");
        hive.SetValue("Mine", "v", RegistryValueKind.Bool, true);

        Assert.Equal(M("700"), hive.GetKeyPermissions("Mine").Mode);
        Assert.Equal(M("600"), hive.GetValuePermissions("Mine", "v")!.Value.Mode);
    }

    [Fact]
    public void Permissions_survive_the_file()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.SetValue(@"A\B", "v", RegistryValueKind.String, "x");
        hive.SetKeyPermissions("A", new PosixPermissions(1000, 100, M("750")));
        hive.SetValuePermissions(@"A\B", "v", new PosixPermissions(1000, 100, M("640")));

        var ms = new MemoryStream();
        hive.Write(ms);
        ms.Position = 0;
        var back = Hive.Read("t", ms);
        back.Identity = Root;

        Assert.Equal(new PosixPermissions(0, 0, M("777")), back.GetKeyPermissions(""));
        Assert.Equal(new PosixPermissions(1000, 100, M("750")), back.GetKeyPermissions("A"));
        Assert.Equal(new PosixPermissions(1000, 1000, M("755")), back.GetKeyPermissions(@"A\B"));
        Assert.Equal(new PosixPermissions(1000, 100, M("640")), back.GetValuePermissions(@"A\B", "v"));
    }

    [Fact]
    public void Files_from_before_permissions_existed_and_corrupt_modes_are_refused()
    {
        var hive = Make();
        hive.SetValue("A", "v", RegistryValueKind.Bool, true);
        var ms = new MemoryStream();
        hive.Write(ms);
        var good = ms.ToArray();

        var old = (byte[])good.Clone();
        old[6] = 0;   // minor version 0: the format without permissions
        Assert.Throws<InvalidDataException>(() => Hive.Read("t", new MemoryStream(old)));

        var badMode = (byte[])good.Clone();
        badMode[64 + 40 + 1] = 0x02;   // root key's mode gets bit 0x200: more than rwxrwxrwx
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(badMode.AsSpan(48), FhiveFormat.Crc32(badMode.AsSpan(64)));
        Assert.Throws<InvalidDataException>(() => Hive.Read("t", new MemoryStream(badMode)));
    }

    [Fact]
    public void A_private_key_is_invisible_and_unreadable_to_others()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.SetValue(@"Private\Inner", "secret", RegistryValueKind.String, "s3cret");
        hive.SetKeyPermissions("Private", new PosixPermissions(1000, 1000, M("700")));

        Assert.Equal("s3cret", hive.GetValue(@"Private\Inner", "secret")!.Data);   // the owner still can

        hive.Identity = Bob;
        Assert.False(hive.KeyExists(@"Private\Inner"));
        Assert.Throws<UnauthorizedAccessException>(() => hive.GetValue(@"Private\Inner", "secret"));
        Assert.Throws<UnauthorizedAccessException>(() => hive.GetSubKeyNames("Private"));
        Assert.Contains("Private", hive.GetSubKeyNames(""));   // the name itself is in the world-readable root
    }

    [Fact]
    public void Value_modes_decide_who_can_read_and_list_them()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.SetValue("K", "world", RegistryValueKind.String, "w");
        hive.SetValue("K", "group", RegistryValueKind.String, "g");
        hive.SetValue("K", "mine", RegistryValueKind.String, "m");
        hive.SetValuePermissions("K", "group", new PosixPermissions(1000, 100, M("640")));
        hive.SetValuePermissions("K", "mine", new PosixPermissions(1000, 1000, M("600")));

        hive.Identity = Bob;     // in group 100
        Assert.Equal("g", hive.GetValue("K", "group")!.Data);
        Assert.Throws<UnauthorizedAccessException>(() => hive.GetValue("K", "mine"));
        Assert.Equal(new[] { "group", "world" }, hive.GetValues("K").Select(v => v.Name));

        hive.Identity = Carol;   // no group
        Assert.Throws<UnauthorizedAccessException>(() => hive.GetValue("K", "group"));
        Assert.Equal(new[] { "world" }, hive.GetValues("K").Select(v => v.Name));
    }

    [Fact]
    public void Searching_every_key_along_the_path_is_required()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.SetValue(@"A\B\C", "v", RegistryValueKind.Bool, true);
        hive.SetKeyPermissions(@"A\B\C", new PosixPermissions(1000, 1000, M("777")));
        hive.SetKeyPermissions("A", new PosixPermissions(1000, 1000, M("744")));   // others may list A but not enter it

        hive.Identity = Bob;
        Assert.False(hive.KeyExists(@"A\B\C"));
        Assert.Throws<UnauthorizedAccessException>(() => hive.GetValue(@"A\B\C", "v"));
        Assert.Contains("B", hive.GetSubKeyNames("A"));
    }

    [Fact]
    public void Adding_removing_and_replacing_values_follow_file_semantics()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.SetValue("Shared", "alices", RegistryValueKind.String, "a");
        hive.SetKeyPermissions("Shared", new PosixPermissions(1000, 100, M("755")));   // group has no write yet

        hive.Identity = Bob;
        Assert.Throws<UnauthorizedAccessException>(() => hive.SetValue("Shared", "bobs", RegistryValueKind.String, "b"));   // no key write
        Assert.Throws<UnauthorizedAccessException>(() => hive.DeleteValue("Shared", "alices"));
        Assert.Throws<UnauthorizedAccessException>(() => hive.SetValue("Shared", "alices", RegistryValueKind.String, "x"));   // value is 0644

        hive.Identity = Alice;
        hive.SetKeyPermissions("Shared", new PosixPermissions(1000, 100, M("775")));   // now the group may write the key
        hive.SetValuePermissions("Shared", "alices", new PosixPermissions(1000, 1000, M("400")));   // read-only even for her

        hive.Identity = Bob;
        hive.SetValue("Shared", "bobs", RegistryValueKind.String, "b");
        Assert.Equal(new PosixPermissions(1001, 1001, M("644")), hive.GetValuePermissions("Shared", "bobs"));   // Bob owns what he adds
        Assert.Throws<UnauthorizedAccessException>(() => hive.SetValue("Shared", "alices", RegistryValueKind.String, "x"));   // 0400: not writable
        Assert.True(hive.DeleteValue("Shared", "alices"));   // deleting needs the key's write bit, not the value's
        Assert.Null(hive.GetValue("Shared", "alices"));
    }

    [Fact]
    public void Replacing_a_value_keeps_its_owner_and_mode()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.SetValue("K", "v", RegistryValueKind.Int32, 1);
        hive.SetValuePermissions("K", "v", new PosixPermissions(1000, 100, M("664")));

        hive.Identity = Bob;   // group member with write
        hive.SetValue("K", "v", RegistryValueKind.Int32, 2);

        Assert.Equal(2, hive.GetValue("K", "v")!.Data);
        Assert.Equal(new PosixPermissions(1000, 100, M("664")), hive.GetValuePermissions("K", "v"));
    }

    [Fact]
    public void Deleting_a_key_needs_the_parent_and_everything_inside_like_rm_r()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.SetValue(@"Tree\Locked\Inner", "v", RegistryValueKind.Bool, true);
        hive.SetKeyPermissions("Tree", new PosixPermissions(1000, 1000, M("777")));
        hive.SetKeyPermissions(@"Tree\Locked", new PosixPermissions(1000, 1000, M("555")));   // nobody may remove entries

        hive.Identity = Bob;
        Assert.Throws<UnauthorizedAccessException>(() => hive.DeleteKey("Tree"));   // would have to empty Locked
        hive.Identity = Alice;
        Assert.Throws<UnauthorizedAccessException>(() => hive.DeleteKey("Tree"));   // even for the owner: 555
        Assert.True(hive.KeyExists(@"Tree\Locked\Inner"));                            // and nothing was removed

        hive.SetKeyPermissions(@"Tree\Locked", new PosixPermissions(1000, 1000, M("755")));
        hive.Identity = Bob;
        Assert.Throws<UnauthorizedAccessException>(() => hive.DeleteKey("Tree"));   // Locked is now 755: Bob cannot write into it
        hive.Identity = Alice;
        Assert.True(hive.DeleteKey("Tree"));
    }

    [Fact]
    public void Changing_permissions_follows_the_chmod_chgrp_chown_rules()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.SetValue("K", "v", RegistryValueKind.Bool, true);
        var mine = hive.GetKeyPermissions("K");

        hive.Identity = Bob;
        Assert.Throws<UnauthorizedAccessException>(() => hive.SetKeyPermissions("K", mine.WithMode(M("777"))));   // not the owner

        hive.Identity = Alice;
        hive.SetKeyPermissions("K", mine.WithMode(M("750")));                                                       // chmod: ok
        hive.SetKeyPermissions("K", mine with { Gid = 100, Mode = M("750") });                                    // chgrp to a group she is in: ok
        Assert.Throws<UnauthorizedAccessException>(() => hive.SetKeyPermissions("K", mine with { Gid = 999 }));   // a group she is not in
        Assert.Throws<UnauthorizedAccessException>(() => hive.SetKeyPermissions("K", mine with { Uid = 1001 }));  // chown: only root
        Assert.Throws<ArgumentException>(() => hive.SetKeyPermissions("K", mine.WithMode(0x200)));

        hive.Identity = Root;
        hive.SetKeyPermissions("K", new PosixPermissions(1002, 999, M("700")));                                     // root can do anything
        Assert.Equal(new PosixPermissions(1002, 999, M("700")), hive.GetKeyPermissions("K"));
    }

    [Fact]
    public void Flags_belong_to_the_owner_and_root_sees_everything()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.SetValue("K", "v", RegistryValueKind.Bool, true);
        hive.SetKeyPermissions("K", new PosixPermissions(1000, 1000, M("700")));

        hive.Identity = Bob;
        Assert.Throws<UnauthorizedAccessException>(() => hive.SetKeyFlags("K", RegistryKeyFlags.Hidden));

        hive.Identity = Root;   // root bypasses every mode
        Assert.Equal(true, hive.GetValue("K", "v")!.Data);
        hive.SetKeyFlags("K", RegistryKeyFlags.Hidden);
        hive.SetValue("K", "v", RegistryValueKind.Bool, false);
    }

    [Fact]
    public void Permission_changes_raise_events_only_when_something_changed()
    {
        var hive = Make();
        hive.Identity = Alice;
        hive.SetValue("K", "v", RegistryValueKind.Bool, true);
        var seen = new List<RegistryChange>();
        hive.Changed += (_, c) => seen.Add(c);

        var perms = hive.GetKeyPermissions("K");
        hive.SetKeyPermissions("K", perms);   // same as now
        hive.SetKeyPermissions("K", perms.WithMode(M("700")));
        hive.SetValuePermissions("K", "v", hive.GetValuePermissions("K", "v")!.Value.WithMode(M("755")));   // makes it executable

        Assert.Equal(new[]
        {
            new RegistryChange(RegistryChangeKind.PermissionsChanged, "K"),
            new RegistryChange(RegistryChangeKind.PermissionsChanged, "K", "v"),
        }, seen);
    }

    [Fact]
    public void Shared_hives_persist_and_enforce_permissions_across_handles()
    {
        using var dir = new TempDir();
        using var a = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        a.Identity = Root;
        a.SetKeyPermissions("", new PosixPermissions(0, 0, M("777")));
        a.Identity = Alice;
        a.SetValue(@"Mine\Inner", "v", RegistryValueKind.String, "x");
        a.SetKeyPermissions("Mine", new PosixPermissions(1000, 1000, M("700")));

        using var b = Hive.OpenShared("HKCU", dir.Hive, watch: false);
        b.Identity = Bob;
        var seen = new List<RegistryChange>();
        b.Changed += (_, c) => seen.Add(c);

        Assert.True(b.KeyExists("Mine"));                // like stat: the name is in the world-searchable root
        Assert.False(b.KeyExists(@"Mine\Inner"));        // but its inside is closed: the mode came through the file
        Assert.Throws<UnauthorizedAccessException>(() => b.SetValue(@"Mine", "w", RegistryValueKind.Bool, true));

        a.SetKeyPermissions("Mine", new PosixPermissions(1000, 1000, M("755")));
        b.Refresh();
        Assert.Contains(new RegistryChange(RegistryChangeKind.PermissionsChanged, "Mine"), seen);
        Assert.True(b.KeyExists(@"Mine\Inner"));
    }

    [Fact]
    public void The_merged_classes_view_reports_the_permissions_of_the_value_it_would_return()
    {
        var user = new Hive("HKCU") { Identity = Root };
        var system = new Hive("HKLM") { Identity = Root };
        var hkcr = new ClassesRoot(user, system);
        system.SetValue(@"Software\Classes\.x\shell\open\command", "", RegistryValueKind.String, "/bin/true");
        system.SetValuePermissions(@"Software\Classes\.x\shell\open\command", "", new PosixPermissions(0, 0, M("755")));

        Assert.Equal(M("755"), hkcr.GetValuePermissions(@".x\shell\open\command", "")!.Value.Mode);

        user.SetValue(@"Software\Classes\.x\shell\open\command", "", RegistryValueKind.String, "/bin/false");   // user overrides: 0644
        Assert.Equal(M("644"), hkcr.GetValuePermissions(@".x\shell\open\command", "")!.Value.Mode);
        Assert.Null(hkcr.GetValuePermissions(@".nope", ""));
    }
}
