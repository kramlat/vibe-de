using Mkhive;
using Vibe.Registry;

namespace mkhive.Tests;

public sealed class Work : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mkhive-" + Guid.NewGuid().ToString("N"));
    public StringWriter Out { get; } = new();
    public StringWriter Err { get; } = new();

    public Work() => Directory.CreateDirectory(Root);

    public string Path_(string name) => Path.Combine(Root, name);

    public string Write(string name, string content)
    {
        var path = Path_(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public int Run(Func<string, string?>? env, params string[] args) => MkhiveCommand.Run(args, Out, Err, env ?? (_ => null));
    public int Run(params string[] args) => Run(null, args);

    public Hive Load(string path) => new Func<Hive>(() =>
    {
        var h = Hive.Load("t", path);
        h.Identity = RegistryIdentity.Root;
        return h;
    })();

    public void Dispose() => Directory.Delete(Root, recursive: true);

    public const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\Software\Vde]
        "Version"="1.0"

        [HKEY_CLASSES_ROOT\.thing]
        @="Thing.Document"

        [HKEY_CLASSES_ROOT\Thing.Document\shell\open\command]
        @="/usr/bin/thing \"%1\""

        [HKEY_CURRENT_USER\Software\ForTheUser]
        "x"=dword:00000001
        """;
}

public class CreateTests
{
    private static ushort M(string octal) => PosixPermissions.ParseMode(octal);

    [Fact]
    public void Builds_a_system_hive_from_reg_files_mapping_HKLM_and_HKCR()
    {
        using var w = new Work();
        var reg = w.Write("in/a.reg", Work.Reg);
        var hive = w.Path_("out/etc/hive/System.fhive");

        var code = w.Run("create", hive, reg);

        Assert.Equal(0, code);
        var loaded = w.Load(hive);
        Assert.Equal("1.0", loaded.GetValue(@"Software\Vde", "Version")!.Data);                                    // HKLM\x -> x
        Assert.Equal("Thing.Document", loaded.GetValue(@"Software\Classes\.thing", "")!.Data);                      // HKCR\x -> Software\Classes\x
        Assert.False(loaded.KeyExists(@"Software\ForTheUser"));                                                    // HKCU does not belong in a system hive
        Assert.Contains("HKEY_CURRENT_USER", w.Err.ToString());                                                    // and says so
        Assert.Equal(0, w.Out.ToString().Length);                                                                   // quiet unless -v
    }

    [Fact]
    public void Everything_belongs_to_root_by_default_with_default_modes_and_the_file_is_644()
    {
        using var w = new Work();
        var reg = w.Write("a.reg", Work.Reg);
        var hive = w.Path_("sys/System.fhive");

        Assert.Equal(0, w.Run("create", hive, reg));

        var loaded = w.Load(hive);
        Assert.Equal(new PosixPermissions(0, 0, M("755")), loaded.GetKeyPermissions(""));
        Assert.Equal(new PosixPermissions(0, 0, M("755")), loaded.GetKeyPermissions(@"Software\Classes\Thing.Document\shell"));
        Assert.Equal(new PosixPermissions(0, 0, M("644")), loaded.GetValuePermissions(@"Software\Vde", "Version"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(hive));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                     | UnixFileMode.OtherRead | UnixFileMode.OtherExecute, File.GetUnixFileMode(Path.GetDirectoryName(hive)!));
        Assert.False(File.Exists(hive + ".tmp"));
    }

    [Fact]
    public void A_user_hive_maps_HKCU_belongs_to_the_given_owner_and_is_private()
    {
        using var w = new Work();
        var reg = w.Write("a.reg", Work.Reg);
        var hive = w.Path_("home/.config/hive/User.fhive");

        Assert.Equal(0, w.Run("create", "--type", "user", "--owner", "1000:100", hive, reg));

        var loaded = w.Load(hive);
        Assert.Equal(1u, loaded.GetValue(@"Software\ForTheUser", "x")!.Data);
        Assert.False(loaded.KeyExists(@"Software\Vde"));
        Assert.Equal(new PosixPermissions(1000, 100, M("755")), loaded.GetKeyPermissions(@"Software\ForTheUser"));
        Assert.Equal(new PosixPermissions(1000, 100, M("644")), loaded.GetValuePermissions(@"Software\ForTheUser", "x"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(hive));
    }

    [Fact]
    public void Commands_stay_inert_unless_a_rule_or_auto_exec_marks_them_executable()
    {
        using var w = new Work();
        var reg = w.Write("a.reg", Work.Reg);
        var command = @"Software\Classes\Thing.Document\shell\open\command";

        w.Run("create", w.Path_("plain.fhive"), reg);
        Assert.Equal(M("644"), w.Load(w.Path_("plain.fhive")).GetValuePermissions(command, "")!.Value.Mode);

        w.Run("create", "--auto-exec", w.Path_("exec.fhive"), reg);
        Assert.Equal(M("755"), w.Load(w.Path_("exec.fhive")).GetValuePermissions(command, "")!.Value.Mode);
        Assert.Equal(M("644"), w.Load(w.Path_("exec.fhive")).GetValuePermissions(@"Software\Vde", "Version")!.Value.Mode);   // only commands
    }

    [Fact]
    public void Rules_set_modes_and_owners_and_the_last_match_wins()
    {
        using var w = new Work();
        var reg = w.Write("a.reg", """
            Windows Registry Editor Version 5.00

            [HKEY_LOCAL_MACHINE\Software\Vde\Secrets\Deep\Er]
            "token"="s"
            "other"="o"

            [HKEY_LOCAL_MACHINE\Software\Vde\Open]
            "x"="1"
            @="default"
            """);
        var rules = w.Write("p.rules", """
            # secrets are private to root
            key   Software\Vde\Secrets\**           700
            value Software\Vde\Secrets\** *         600
            value Software\Vde\Secrets\**\Er token  400 0:5
            key   Software\Vde\*                    750 root:77
            value Software\Vde\Open @               664
            """);

        Assert.Equal(0, w.Run("create", "--rules", rules, w.Path_("s.fhive"), reg));

        var h = w.Load(w.Path_("s.fhive"));
        Assert.Equal(new PosixPermissions(0, 0, M("700")), h.GetKeyPermissions(@"Software\Vde\Secrets\Deep\Er"));   // ** crosses segments
        Assert.Equal(new PosixPermissions(0, 77, M("750")), h.GetKeyPermissions(@"Software\Vde\Secrets"));          // last matching rule: the * one
        Assert.Equal(new PosixPermissions(0, 0, M("750")).WithMode(M("750")).Mode, h.GetKeyPermissions(@"Software\Vde\Open").Mode);
        Assert.Equal(new PosixPermissions(0, 5, M("400")), h.GetValuePermissions(@"Software\Vde\Secrets\Deep\Er", "token"));
        Assert.Equal(M("600"), h.GetValuePermissions(@"Software\Vde\Secrets\Deep\Er", "other")!.Value.Mode);
        Assert.Equal(M("664"), h.GetValuePermissions(@"Software\Vde\Open", "")!.Value.Mode);       // @ is the default value
        Assert.Equal(M("644"), h.GetValuePermissions(@"Software\Vde\Open", "x")!.Value.Mode);       // untouched: the default
    }

    [Fact]
    public void An_existing_hive_is_not_overwritten_unless_asked()
    {
        using var w = new Work();
        var reg = w.Write("a.reg", Work.Reg);
        var other = w.Write("b.reg", "Windows Registry Editor Version 5.00\n\n[HKEY_LOCAL_MACHINE\\Software\\Other]\n\"v\"=dword:00000002\n");
        var hive = w.Path_("h.fhive");
        w.Run("create", hive, reg);
        var original = File.ReadAllBytes(hive);

        Assert.Equal(MkhiveCommand.Exists, w.Run("create", hive, other));
        Assert.Equal(original, File.ReadAllBytes(hive));

        Assert.Equal(0, w.Run("create", "--force", hive, other));
        var replaced = w.Load(hive);
        Assert.True(replaced.KeyExists(@"Software\Other"));
        Assert.False(replaced.KeyExists(@"Software\Vde"));   // the old content is gone
    }

    [Fact]
    public void Merging_adds_new_things_and_leaves_what_was_there_exactly_as_it_was()
    {
        using var w = new Work();
        var first = w.Write("a.reg", Work.Reg);
        var second = w.Write("b.reg", "Windows Registry Editor Version 5.00\n\n[HKEY_LOCAL_MACHINE\\Software\\Vde]\n\"Version\"=\"2.0\"\n\"New\"=\"n\"\n");
        var hive = w.Path_("h.fhive");
        w.Run("create", "--mode", "640", hive, first);
        // an administrator changed the mode of an existing value after installation
        var admin = w.Load(hive);
        admin.SetValuePermissions(@"Software\Vde", "Version", new PosixPermissions(0, 0, M("600")));
        admin.Save();

        Assert.Equal(0, w.Run("create", "--merge", "--auto-exec", hive, second));

        var h = w.Load(hive);
        Assert.Equal("2.0", h.GetValue(@"Software\Vde", "Version")!.Data);                            // updated
        Assert.Equal("n", h.GetValue(@"Software\Vde", "New")!.Data);                                   // added
        Assert.Equal("Thing.Document", h.GetValue(@"Software\Classes\.thing", "")!.Data);              // untouched
        Assert.Equal(M("600"), h.GetValuePermissions(@"Software\Vde", "Version")!.Value.Mode);         // the admin's chmod survived
        Assert.Equal(M("644"), h.GetValuePermissions(@"Software\Vde", "New")!.Value.Mode);             // new value: default
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(hive));   // file mode kept
    }

    [Fact]
    public void The_same_inputs_and_source_date_epoch_give_byte_identical_hives()
    {
        using var w = new Work();
        var reg = w.Write("a.reg", Work.Reg);
        Func<string, string?> env = k => k == "SOURCE_DATE_EPOCH" ? "1700000000" : null;

        w.Run(env, "create", w.Path_("one.fhive"), reg);
        Thread.Sleep(1100);   // a different wall-clock second must make no difference
        w.Run(env, "create", w.Path_("two.fhive"), reg);

        Assert.Equal(File.ReadAllBytes(w.Path_("one.fhive")), File.ReadAllBytes(w.Path_("two.fhive")));
        var h = w.Load(w.Path_("one.fhive"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime, h.GetKeyInfo("Software\\Vde").CreatedUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime, h.GetKeyInfo("").ModifiedUtc);
    }

    [Fact]
    public void Several_inputs_are_applied_in_order()
    {
        using var w = new Work();
        var a = w.Write("a.reg", "Windows Registry Editor Version 5.00\n\n[HKEY_LOCAL_MACHINE\\S]\n\"v\"=\"first\"\n\"keep\"=\"k\"\n");
        var b = w.Write("b.reg", "Windows Registry Editor Version 5.00\n\n[HKEY_LOCAL_MACHINE\\S]\n\"v\"=\"second\"\n\"gone\"=\"g\"\n\n[HKEY_LOCAL_MACHINE\\S]\n\"gone\"=-\n");

        Assert.Equal(0, w.Run("create", "-v", w.Path_("h.fhive"), a, b));

        var h = w.Load(w.Path_("h.fhive"));
        Assert.Equal("second", h.GetValue("S", "v")!.Data);
        Assert.Equal("k", h.GetValue("S", "keep")!.Data);
        Assert.Null(h.GetValue("S", "gone"));
        Assert.Contains("wrote", w.Out.ToString());
    }

    [Fact]
    public void Unknown_registry_roots_warn_and_strict_turns_that_into_an_error()
    {
        using var w = new Work();
        var reg = w.Write("a.reg", Work.Reg);

        Assert.Equal(0, w.Run("create", w.Path_("lax.fhive"), reg));
        Assert.Contains("warning", w.Err.ToString());

        Assert.Equal(MkhiveCommand.InputError, w.Run("create", "--strict", w.Path_("strict.fhive"), reg));
        Assert.False(File.Exists(w.Path_("strict.fhive")));   // nothing left behind
    }
}

public class FailureTests
{
    [Fact]
    public void Bad_input_is_reported_with_the_file_name_and_leaves_no_hive()
    {
        using var w = new Work();
        var bad = w.Write("bad.reg", "this is not a registry file");
        var hive = w.Path_("h.fhive");

        Assert.Equal(MkhiveCommand.InputError, w.Run("create", hive, bad));

        Assert.Contains("bad.reg", w.Err.ToString());
        Assert.False(File.Exists(hive));
    }

    [Fact]
    public void Missing_files_are_file_errors()
    {
        using var w = new Work();
        Assert.Equal(MkhiveCommand.IoError, w.Run("create", w.Path_("h.fhive"), w.Path_("nope.reg")));
        Assert.Contains("nope.reg", w.Err.ToString());
        Assert.Equal(MkhiveCommand.IoError, w.Run("create", "--rules", w.Path_("nope.rules"), w.Path_("h.fhive")));
        Assert.Equal(MkhiveCommand.IoError, w.Run("dump", w.Path_("nope.fhive")));
    }

    [Theory]
    [InlineData("value Software\\X 644")]                  // too few fields
    [InlineData("chmod Software\\X 644")]                  // unknown rule
    [InlineData("key Software\\X 999")]                    // not octal
    [InlineData("key Software\\X 644 abc")]                // owner not numeric
    [InlineData("key \"Software\\X 644")]                  // unterminated quote
    public void A_bad_rules_file_stops_everything_before_any_work_is_done(string rule)
    {
        using var w = new Work();
        var rules = w.Write("p.rules", rule + "\n");
        var hive = w.Path_("h.fhive");

        Assert.Equal(MkhiveCommand.InputError, w.Run("create", "--rules", rules, hive));

        Assert.Contains("p.rules:1", w.Err.ToString());
        Assert.False(File.Exists(hive));
    }

    [Theory]
    [InlineData(new string[0], 1)]
    [InlineData(new[] { "frobnicate" }, 1)]
    [InlineData(new[] { "create" }, 1)]                                       // no file
    [InlineData(new[] { "create", "--bogus", "x.fhive" }, 1)]
    [InlineData(new[] { "create", "--type", "weird", "x.fhive" }, 1)]
    [InlineData(new[] { "create", "--mode", "999", "x.fhive" }, 1)]
    [InlineData(new[] { "create", "--owner", "me", "x.fhive" }, 1)]
    [InlineData(new[] { "create", "--merge", "--force", "x.fhive" }, 1)]
    [InlineData(new[] { "create", "--rules" }, 1)]                            // option without its value
    [InlineData(new[] { "dump" }, 1)]
    public void Usage_mistakes_exit_1_and_change_nothing(string[] args, int expected)
    {
        using var w = new Work();
        Assert.Equal(expected, w.Run(args));
        Assert.NotEqual(0, w.Err.ToString().Length);
    }

    [Fact]
    public void Help_exits_0_and_prints_the_usage()
    {
        using var w = new Work();
        Assert.Equal(0, w.Run("--help"));
        Assert.Contains("usage: mkhive create", w.Out.ToString());
    }

    [Fact]
    public void A_corrupt_hive_cannot_be_merged_into_or_dumped()
    {
        using var w = new Work();
        var hive = w.Write("c.fhive", "not a hive at all");
        var reg = w.Write("a.reg", Work.Reg);

        Assert.Equal(MkhiveCommand.IoError, w.Run("create", "--merge", hive, reg));
        Assert.Equal(MkhiveCommand.IoError, w.Run("dump", hive));
        Assert.Equal("not a hive at all", File.ReadAllText(hive));   // never overwritten
    }
}

public class DumpAndDataTests
{
    [Fact]
    public void Dump_lists_owners_modes_and_values_like_ls()
    {
        using var w = new Work();
        var reg = w.Write("a.reg", """
            Windows Registry Editor Version 5.00

            [HKEY_LOCAL_MACHINE\Software\Vde]
            @="root default"
            "Count"=dword:0000002a
            "Blob"=hex:01,02,03
            """);
        var rules = w.Write("p.rules", "value Software\\Vde Count 600 1000:100\n");
        w.Run("create", "--rules", rules, w.Path_("h.fhive"), reg);

        Assert.Equal(0, w.Run("dump", w.Path_("h.fhive")));

        var text = w.Out.ToString();
        Assert.Contains(@"drwxr-xr-x 0:0 \", text);
        Assert.Contains(@"drwxr-xr-x 0:0 \Software\Vde", text);
        Assert.Contains(@"-rw-r--r-- 0:0 \Software\Vde\@ = String ""root default""", text);
        Assert.Contains(@"-rw------- 1000:100 \Software\Vde\Count = DWord 0x0000002a", text);
        Assert.Contains(@"\Software\Vde\Blob = Binary 01 02 03", text);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vibe.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void The_registry_data_that_ships_with_the_repo_builds_cleanly()
    {
        using var w = new Work();
        var data = Path.Combine(RepoRoot(), "data", "registry");

        var code = w.Run("create", "--rules", Path.Combine(data, "permissions.rules"), w.Path_("System.fhive"), Path.Combine(data, "system.reg"));

        Assert.Equal(0, code);
        Assert.Equal("", w.Err.ToString());
        var h = w.Load(w.Path_("System.fhive"));
        Assert.Equal("fluent", h.GetValue(@"Software\Vde\Appearance", "Theme")!.Data);
        Assert.Equal("folder", h.GetValue(@"Software\Classes\CLSID\inode/directory", "DefaultIcon")!.Data);
    }
}
