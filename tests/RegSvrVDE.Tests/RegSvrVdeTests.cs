using System.Diagnostics;
using RegSvrVDE;
using Vde.Components;
using Vibe.Registry;

namespace RegSvrVDE.Tests;

internal sealed class Env : IDisposable
{
    public const string Portal = "{5A0E2D4C-0000-4000-8000-000000000001}";
    public const string Worker = "{5A0E2D4C-0000-4000-8000-000000000002}";

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "regsvr-" + Guid.NewGuid().ToString("N"));
    public string Sample { get; }
    public string UserHive => Path.Combine(Dir, "user", "User.fhive");
    public StringWriter Out { get; private set; } = new();
    public StringWriter Err { get; private set; } = new();

    public Env()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "lib"));
        Sample = Path.Combine(Dir, "lib", "Sample.Component.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "sample", "Sample.Component.dll"), Sample);
    }

    public int Run(params string[] args)
    {
        Out = new StringWriter();
        Err = new StringWriter();
        return RegSvrVdeCommand.Run([.. args, "--user-hive=" + UserHive], Out, Err);
    }

    // The registry as another process would read it.
    public (ClassesRoot hkcr, Hive hive) Open(string? path = null)
    {
        var hive = Hive.OpenShared("HKCU", path ?? UserHive, watch: false);
        return (new ClassesRoot(hive), hive);
    }

    public void Dispose() => Directory.Delete(Dir, recursive: true);
}

public class RegSvrVdeTests
{
    private static ushort M(string octal) => PosixPermissions.ParseMode(octal);

    [Fact]
    public void Registers_the_declared_components_in_the_COM_layout()
    {
        using var env = new Env();

        Assert.Equal(0, env.Run(env.Sample));

        var (hkcr, hive) = env.Open();
        using (hive)
        {
            Assert.Equal("Sample portal backend", hkcr.GetValue($@"CLSID\{Env.Portal}", "")!.Data);
            var server = $@"CLSID\{Env.Portal}\InprocServer32";
            Assert.Equal(env.Sample, hkcr.GetValue(server, "")!.Data);
            Assert.Equal("Apartment", hkcr.GetValue(server, "ThreadingModel")!.Data);
            Assert.Equal("Sample.Component.PortalBackend", hkcr.GetValue(server, "Class")!.Data);
            Assert.StartsWith("Sample.Component, Version=", (string)hkcr.GetValue(server, "Assembly")!.Data!);
            Assert.Equal(M("755"), hkcr.GetValuePermissions(server, "")!.Value.Mode);      // marked executable

            Assert.Equal("Sample.Portal", hkcr.GetValue($@"CLSID\{Env.Portal}\ProgID", "")!.Data);
            Assert.Equal(Env.Portal, hkcr.GetValue(@"Sample.Portal\CLSID", "")!.Data);
            Assert.Equal(new[] { "{AAAAAAAA-0000-4000-8000-000000000001}", "{AAAAAAAA-0000-4000-8000-000000000002}" },
                hkcr.GetSubKeyNames($@"CLSID\{Env.Portal}\Implemented Categories"));

            Assert.Equal("Free", hkcr.GetValue($@"CLSID\{Env.Worker}\InprocServer32", "ThreadingModel")!.Data);
            Assert.False(hkcr.KeyExists($@"CLSID\{Env.Worker}\ProgID"));
            Assert.Equal(2, hkcr.GetSubKeyNames("CLSID").Count);   // the unusable ones were not registered
        }

        Assert.Contains($"registered {Env.Portal} Sample.Component.PortalBackend (Apartment)", env.Out.ToString());
        var warnings = env.Err.ToString();
        Assert.Contains("NotPublic", warnings);
        Assert.Contains("'not-a-guid' is not a GUID", warnings);
        Assert.Contains("Abstract", warnings);
    }

    [Fact]
    public void A_registered_component_is_found_and_trusted_by_the_catalog()
    {
        using var env = new Env();
        env.Run(env.Sample);

        var (hkcr, hive) = env.Open();
        using (hive)
        {
            var catalog = new ComponentCatalog(hkcr);

            var portal = catalog.Find(Env.Portal.ToLowerInvariant())!;   // any spelling of the GUID
            Assert.True(portal.Trust.IsTrusted, portal.Trust.Reason);
            Assert.Equal(ThreadingModel.Apartment, portal.Component.ThreadingModel);
            Assert.Equal("Sample.Component.PortalBackend", portal.Component.ClassName);
            Assert.Equal("Sample.Portal", portal.Component.ProgId);
            Assert.Equal(2, portal.Component.Categories!.Count);

            Assert.Equal(Env.Portal, catalog.FindByProgId("Sample.Portal")!.Component.Clsid);
            Assert.Equal(new[] { Env.Portal, Env.Worker }, catalog.All().Select(r => r.Component.Clsid));
            Assert.Null(catalog.Find("not a guid"));
            Assert.Null(catalog.Find("{5A0E2D4C-0000-4000-8000-0000000000FF}"));
        }
    }

    [Fact]
    public void Registering_again_changes_nothing_and_unregistering_removes_everything()
    {
        using var env = new Env();
        Assert.Equal(0, env.Run(env.Sample));
        Assert.Equal(0, env.Run(env.Sample));

        Assert.Equal(0, env.Run("/u", env.Sample));
        Assert.Contains("unregistered", env.Out.ToString());

        var (hkcr, hive) = env.Open();
        using (hive)
        {
            Assert.False(hkcr.KeyExists($@"CLSID\{Env.Portal}"));
            Assert.False(hkcr.KeyExists($@"CLSID\{Env.Worker}"));
            Assert.False(hkcr.KeyExists("Sample.Portal"));   // the ProgID went with it
        }

        Assert.Equal(0, env.Run("/u", env.Sample));          // already gone is fine
        Assert.Contains("was not registered", env.Out.ToString());
    }

    [Fact]
    public void Silent_means_no_success_message_but_errors_still_show()
    {
        using var env = new Env();

        Assert.Equal(0, env.Run("/s", env.Sample));
        Assert.Equal("", env.Out.ToString());

        Assert.Equal(RegSvrVdeCommand.AssemblyError, env.Run("/s", Path.Combine(env.Dir, "missing.dll")));
        Assert.Contains("no such file", env.Err.ToString());
    }

    [Fact]
    public void A_file_others_can_write_is_refused_unless_forced_and_then_it_is_not_trusted_when_loading()
    {
        using var env = new Env();
        File.SetUnixFileMode(env.Sample, File.GetUnixFileMode(env.Sample) | UnixFileMode.OtherWrite);

        Assert.Equal(RegSvrVdeCommand.RegistrationFailed, env.Run(env.Sample));
        Assert.Contains("anyone can write to the file", env.Err.ToString());
        var (hkcr, hive) = env.Open();
        using (hive)
            Assert.False(hkcr.KeyExists("CLSID"));

        Assert.Equal(0, env.Run("--force", env.Sample));
        (hkcr, hive) = env.Open();
        using (hive)
        {
            var found = new ComponentCatalog(hkcr).Find(Env.Portal)!;
            Assert.False(found.Trust.IsTrusted);                 // the host would still refuse to load it
            Assert.Contains("anyone can write", found.Trust.Reason);
        }
    }

    [Fact]
    public void An_entry_that_is_not_marked_executable_is_not_trusted()
    {
        using var env = new Env();
        env.Run(env.Sample);

        var (hkcr, hive) = env.Open();
        using (hive)
        {
            var server = $@"CLSID\{Env.Portal}\InprocServer32";
            var current = hkcr.GetValuePermissions(server, "")!.Value;
            hkcr.SetValuePermissions(server, "", current.WithMode(M("644")));   // e.g. restored from a .reg import

            var found = new ComponentCatalog(hkcr).Find(Env.Portal)!;

            Assert.False(found.Trust.IsTrusted);
            Assert.Contains("not executable", found.Trust.Reason);
        }
    }

    [Fact]
    public void The_system_hive_must_exist_already_and_is_the_only_one_written_with_system()
    {
        using var env = new Env();
        var system = Path.Combine(env.Dir, "etc", "hive", "System.fhive");

        Assert.Equal(RegSvrVdeCommand.RegistrationFailed, env.Run("--system", "--system-hive=" + system, env.Sample));
        Assert.Contains("mkhive", env.Err.ToString());
        Assert.False(File.Exists(system));

        using (var seed = Hive.OpenShared("HKLM", system, watch: false))
            seed.SetValue("Software\\Vde", "v", RegistryValueKind.Bool, true);

        Assert.Equal(0, env.Run("--system", "--system-hive=" + system, env.Sample));

        var (hkcr, hive) = env.Open(system);
        using (hive)
            Assert.Equal(env.Sample, hkcr.GetValue($@"CLSID\{Env.Portal}\InprocServer32", "")!.Data);
        Assert.False(File.Exists(env.UserHive));   // the user hive was not touched
    }

    [Fact]
    public void The_installer_hook_runs_only_with_i_and_gets_the_command_line()
    {
        using var env = new Env();

        Assert.Equal(0, env.Run("/i:--mode=quiet", env.Sample));

        var (hkcr, hive) = env.Open();
        using (hive)
        {
            Assert.Equal("yes", hkcr.GetValue("Sample.Settings", "Installing")!.Data);
            Assert.Equal("--mode=quiet", hkcr.GetValue("Sample.Settings", "CommandLine")!.Data);
            Assert.Equal("Sample.Component.dll", hkcr.GetValue("Sample.Settings", "Assembly")!.Data);
            Assert.True(hkcr.KeyExists($@"CLSID\{Env.Portal}"));   // and the components were registered too
        }

        Assert.Equal(0, env.Run("/u", "/i", env.Sample));
        (hkcr, hive) = env.Open();
        using (hive)
        {
            Assert.Equal("no", hkcr.GetValue("Sample.Settings", "Installing")!.Data);   // uninstalling
            Assert.False(hkcr.KeyExists($@"CLSID\{Env.Portal}"));
        }
    }

    [Fact]
    public void No_register_runs_only_the_hook_and_a_failing_hook_is_exit_5()
    {
        using var env = new Env();

        Assert.Equal(0, env.Run("/n", "/i", env.Sample));
        var (hkcr, hive) = env.Open();
        using (hive)
        {
            Assert.False(hkcr.KeyExists("CLSID"));
            Assert.Equal("yes", hkcr.GetValue("Sample.Settings", "Installing")!.Data);
        }

        Assert.Equal(RegSvrVdeCommand.InstallerFailed, env.Run("/i:fail", env.Sample));
        Assert.Contains("told to fail", env.Err.ToString());
    }

    [Fact]
    public void Bad_inputs_have_their_own_exit_codes()
    {
        using var env = new Env();
        var notAssembly = Path.Combine(env.Dir, "text.dll");
        File.WriteAllText(notAssembly, "just some text");

        Assert.Equal(RegSvrVdeCommand.AssemblyError, env.Run(notAssembly));
        Assert.Contains("not a readable .NET assembly", env.Err.ToString());
        Assert.Equal(RegSvrVdeCommand.AssemblyError, env.Run(Path.Combine(env.Dir, "missing.dll")));
        // a real assembly that declares nothing
        Assert.Equal(RegSvrVdeCommand.NoComponents, env.Run(Path.Combine(AppContext.BaseDirectory, "Vde.Components.dll")));
        Assert.Contains("no components declared", env.Err.ToString());
        // with nothing to register, /i stops at the same point: no hook runs on an assembly that declares nothing
        Assert.Equal(RegSvrVdeCommand.NoComponents, env.Run("/i", Path.Combine(AppContext.BaseDirectory, "Vde.Components.dll")));
    }

    [Fact]
    public void Several_assemblies_are_all_processed_and_the_first_failure_is_the_exit_code()
    {
        using var env = new Env();

        var code = env.Run(Path.Combine(env.Dir, "missing.dll"), env.Sample);

        Assert.Equal(RegSvrVdeCommand.AssemblyError, code);
        var (hkcr, hive) = env.Open();
        using (hive)
            Assert.True(hkcr.KeyExists($@"CLSID\{Env.Portal}"));   // the good one was still registered
    }

    [Theory]
    [InlineData(new string[0], 1)]
    [InlineData(new[] { "/n" }, 1)]                  // no assembly
    [InlineData(new[] { "/n", "x.dll" }, 1)]         // /n needs /i
    [InlineData(new[] { "--bogus", "x.dll" }, 1)]
    public void Usage_mistakes_exit_1(string[] args, int expected)
    {
        var err = new StringWriter();
        Assert.Equal(expected, RegSvrVdeCommand.Run(args, new StringWriter(), err));
        Assert.NotEqual(0, err.ToString().Length);
    }

    [Fact]
    public void Help_exits_0()
    {
        var output = new StringWriter();
        Assert.Equal(0, RegSvrVdeCommand.Run(["--help"], output, new StringWriter()));
        Assert.Contains("usage: RegSvrVDE", output.ToString());
    }

    // The safety claim, proved with real processes: the sample's module initializer writes a marker the moment
    // the assembly is loaded for execution. Registering it must not write the marker; only /i may.
    [Fact]
    public async Task Registering_never_runs_the_components_code_but_the_installer_hook_does()
    {
        using var env = new Env();
        var marker = Path.Combine(env.Dir, "marker.txt");

        var register = await Child(env, marker, env.Sample);
        Assert.Equal(0, register.exit);
        Assert.False(File.Exists(marker), "registering ran the component's code");

        var install = await Child(env, marker, "/i", env.Sample);
        Assert.Equal(0, install.exit);
        Assert.True(File.Exists(marker), "the installer hook should have loaded the assembly");
        Assert.Contains("module initializer ran", File.ReadAllText(marker));
    }

    private static async Task<(int exit, string output)> Child(Env env, string marker, params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RegSvrVDE.dll"));
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        info.ArgumentList.Add("--user-hive=" + env.UserHive);
        info.Environment["VDE_SAMPLE_MARKER"] = marker;

        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }
}

public class ComponentLibraryTests
{
    private static ComponentInfo Info(string clsid, string? progId = null, ThreadingModel model = ThreadingModel.Apartment, string path = "/x/A.dll")
        => new(clsid, "N.C", path, "A, Version=1.0.0.0", model, progId, "desc", ["{AAAAAAAA-0000-4000-8000-000000000001}"]);

    private const string Id = "{11111111-0000-4000-8000-000000000001}";
    private const string Other = "{11111111-0000-4000-8000-000000000002}";

    [Fact]
    public void Re_registering_with_a_new_progid_leaves_no_trace_of_the_old_one()
    {
        var hkcr = new ClassesRoot(new Hive("HKCU"));
        ComponentRegistrar.Register(hkcr, Info(Id, "Old.Name"));
        Assert.True(hkcr.KeyExists("Old.Name"));

        ComponentRegistrar.Register(hkcr, Info(Id, "New.Name"));

        Assert.False(hkcr.KeyExists("Old.Name"));
        Assert.Equal(Id, hkcr.GetValue(@"New.Name\CLSID", "")!.Data);
    }

    [Fact]
    public void Unregistering_keeps_a_progid_that_has_since_been_taken_by_another_component()
    {
        var hkcr = new ClassesRoot(new Hive("HKCU"));
        ComponentRegistrar.Register(hkcr, Info(Id, "Shared.Name"));
        ComponentRegistrar.Register(hkcr, Info(Other, "Shared.Name"));   // another component claims the ProgID

        Assert.True(ComponentRegistrar.Unregister(hkcr, Id));

        Assert.True(hkcr.KeyExists("Shared.Name"));                     // still points at Other
        Assert.Equal(Other, hkcr.GetValue(@"Shared.Name\CLSID", "")!.Data);
        Assert.False(ComponentRegistrar.Unregister(hkcr, Id));          // nothing left to remove
    }

    [Fact]
    public void An_unknown_threading_model_reads_back_as_apartment_and_an_empty_registry_has_no_components()
    {
        var hkcr = new ClassesRoot(new Hive("HKCU"));
        Assert.Empty(new ComponentCatalog(hkcr).All());

        ComponentRegistrar.Register(hkcr, Info(Id, path: "/nonexistent/A.dll"));
        hkcr.SetValue($@"CLSID\{Id}\InprocServer32", "ThreadingModel", RegistryValueKind.String, "Neutral");

        var found = new ComponentCatalog(hkcr).Find(Id)!;
        Assert.Equal(ThreadingModel.Apartment, found.Component.ThreadingModel);
        Assert.False(found.Trust.IsTrusted);   // the file is not there
    }

    [Theory]
    [InlineData("{5a0e2d4c-0000-4000-8000-000000000001}", "{5A0E2D4C-0000-4000-8000-000000000001}")]
    [InlineData("5A0E2D4C-0000-4000-8000-000000000001", "{5A0E2D4C-0000-4000-8000-000000000001}")]
    [InlineData("not a guid", null)]
    [InlineData(null, null)]
    public void Guids_are_normalised_to_one_spelling(string? input, string? expected)
        => Assert.Equal(expected, ComponentInfo.NormalizeGuid(input));
}
