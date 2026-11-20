using Vde.SecMan;
using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Vde.SecMan.Tests;

// A small fake system: the files the plugins read, below a temporary root.
public sealed class FakeSystem : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "secman-" + Guid.NewGuid().ToString("N"));

    public FakeSystem(bool selinux = true, bool apparmor = true)
    {
        Write("etc/pam.d/system-auth", "#%PAM-1.0\nauth required pam_unix.so try_first_pass\nauth [success=1 default=ignore] pam_succeed_if.so uid >= 1000\naccount required pam_unix.so\n");
        Write("etc/pam.d/vdedm", "auth include system-auth\n-session optional pam_systemd.so class=greeter\nsession required pam_unix.so\n");
        Write("sys/kernel/security/lsm", "lockdown,capability,landlock,yama,apparmor\n");
        Write("sys/kernel/security/lockdown", "none [integrity] confidentiality\n");
        Write("proc/sys/kernel/yama/ptrace_scope", "1\n");
        Write("proc/sys/user/max_user_namespaces", "63000\n");
        if (selinux)
        {
            Write("sys/fs/selinux/enforce", "0");
            Write("sys/fs/selinux/policyvers", "33\n");
            Write("etc/selinux/config", "# comment\nSELINUX=enforcing\nSELINUXTYPE=targeted\n");
        }
        if (apparmor)
        {
            Write("sys/module/apparmor/parameters/enabled", "Y\n");
            Write("sys/kernel/security/apparmor/profiles", "/usr/bin/man (enforce)\n/usr/sbin/cupsd (complain)\nunconfined-helper (enforce)\n");
        }
    }

    public void Write(string path, string text)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

[Collection("one at a time")]   // HiveServer.Listen sets a process-wide umask for a moment; other tests must not create folders then
public class PluginTests
{
    private static Hive Build(FakeSystem fs, params ISecurityPlugin[] plugins)
    {
        var hive = new Hive("Security") { Identity = RegistryIdentity.Root };
        SecurityBuilder.Fill(hive, plugins.Length > 0 ? plugins : SecurityBuilder.DefaultPlugins, fs.Root);
        return hive;
    }

    [Fact]
    public void PAM_lists_each_services_stack_in_order_and_every_module_used()
    {
        using var fs = new FakeSystem();
        var hive = Build(fs, new PamPlugin());

        Assert.Equal(2, hive.GetValue("PAM", "ServiceCount")!.Data);
        Assert.Equal(new[] { "system-auth", "vdedm" }, hive.GetSubKeyNames(@"PAM\Services"));
        Assert.Equal(new[] { "required pam_unix.so try_first_pass", "[success=1 default=ignore] pam_succeed_if.so uid >= 1000" }, (string[])hive.GetValue(@"PAM\Services\system-auth", "auth")!.Data!);
        Assert.Equal(new[] { "include system-auth" }, (string[])hive.GetValue(@"PAM\Services\vdedm", "auth")!.Data!);
        Assert.Equal(new[] { "optional pam_systemd.so class=greeter", "required pam_unix.so" }, (string[])hive.GetValue(@"PAM\Services\vdedm", "session")!.Data!);   // "-session" is session
        Assert.Equal(new[] { "pam_succeed_if.so", "pam_systemd.so", "pam_unix.so" }, (string[])hive.GetValue("PAM", "Modules")!.Data!);   // includes are not modules
    }

    [Fact]
    public void SELinux_reports_the_kernels_mode_and_the_configured_one()
    {
        using var fs = new FakeSystem();
        var hive = Build(fs, new SeLinuxPlugin());
        Assert.Equal("Permissive", hive.GetValue("SELinux", "Mode")!.Data);          // the kernel says 0
        Assert.Equal(false, hive.GetValue("SELinux", "Enforcing")!.Data);
        Assert.Equal("Enforcing", hive.GetValue("SELinux", "ConfiguredMode")!.Data);  // the config says what it will be at boot
        Assert.Equal("targeted", hive.GetValue("SELinux", "PolicyType")!.Data);
        Assert.Equal(33, hive.GetValue("SELinux", "PolicyVersion")!.Data);
    }

    [Fact]
    public void AppArmor_counts_profiles_publicly_and_lists_them_for_root_only()
    {
        using var fs = new FakeSystem();
        var hive = Build(fs, new AppArmorPlugin());
        Assert.Equal(true, hive.GetValue("AppArmor", "Enabled")!.Data);
        Assert.Equal(3, hive.GetValue("AppArmor", "ProfileCount")!.Data);
        Assert.Equal(2, hive.GetValue("AppArmor", "EnforcedCount")!.Data);
        Assert.Equal(1, hive.GetValue("AppArmor", "ComplainingCount")!.Data);
        Assert.Equal("complain", hive.GetValue(@"AppArmor\Profiles\/usr/sbin/cupsd", "Mode")!.Data);

        var ordinary = hive.As(new RegistryIdentity(1000, 1000, [1000]));
        Assert.Equal(3, ordinary.GetValue("AppArmor", "ProfileCount")!.Data);
        Assert.Throws<UnauthorizedAccessException>(() => ordinary.GetValue(@"AppArmor\Profiles\/usr/sbin/cupsd", "Mode"));
    }

    [Fact]
    public void LSM_reports_the_active_modules_lockdown_and_kernel_switches()
    {
        using var fs = new FakeSystem();
        var hive = Build(fs, new LsmPlugin());
        Assert.Equal(new[] { "lockdown", "capability", "landlock", "yama", "apparmor" }, (string[])hive.GetValue("LSM", "Active")!.Data!);
        Assert.Equal("integrity", hive.GetValue("LSM", "Lockdown")!.Data);
        Assert.Equal(1, hive.GetValue("LSM", "YamaPtraceScope")!.Data);
        Assert.Equal(true, hive.GetValue("LSM", "UserNamespaces")!.Data);
    }

    [Fact]
    public void A_module_that_is_not_there_is_reported_unavailable_and_a_failing_plugin_does_not_stop_the_rest()
    {
        using var fs = new FakeSystem(selinux: false, apparmor: false);
        var hive = Build(fs, new SeLinuxPlugin(), new AppArmorPlugin(), new FailingPlugin(), new LsmPlugin());

        Assert.Equal(false, hive.GetValue(@"Providers\SELinux", "Available")!.Data);
        Assert.False(hive.KeyExists("SELinux"));
        Assert.Equal(false, hive.GetValue(@"Providers\AppArmor", "Available")!.Data);
        Assert.Equal(true, hive.GetValue(@"Providers\Broken", "Available")!.Data);
        Assert.Equal("boom", hive.GetValue(@"Providers\Broken", "Error")!.Data);
        Assert.Equal("integrity", hive.GetValue("LSM", "Lockdown")!.Data);   // the others still ran
    }

    private sealed class FailingPlugin : ISecurityPlugin
    {
        public string Name => "Broken";
        public bool IsAvailable(string root) => true;
        public void Fill(Hive hive, string root) => throw new IOException("boom");
        public IEnumerable<string> WatchedFiles(string root) => [];
    }
}

[Collection("one at a time")]   // HiveServer.Listen sets a process-wide umask for a moment; other tests must not create folders then
public class SecurityServiceTests
{
    [Fact]
    public void A_client_sees_Security_as_a_key_of_HKLM_and_a_refill_replaces_the_content()
    {
        using var fs = new FakeSystem();
        var socket = Path.Combine(fs.Root, "run", "security.sock");
        using var service = new SecurityService(root: fs.Root, identityOf: _ => new RegistryIdentity(1000, 1000, [1000]), periodic: TimeSpan.FromHours(1));
        service.Server.Listen(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        using var stop = new CancellationTokenSource();
        _ = Task.Run(() => service.Server.RunAsync(stop.Token));

        var system = new Hive("HKLM");
        system.SetValue(@"Software\Vde", "Version", RegistryValueKind.String, "1");
        using var hklm = new MountedHive(system, new Dictionary<string, IHive> { ["Security"] = RemoteHive.Connect(socket) });

        Assert.Contains("Security", hklm.GetSubKeyNames(""));
        Assert.Equal("1", hklm.GetValue(@"Software\Vde", "Version")!.Data);
        Assert.Equal(new[] { "AppArmor", "LSM", "PAM", "Providers", "SELinux" }, hklm.GetSubKeyNames("Security"));
        Assert.Equal("integrity", hklm.GetValue(@"Security\LSM", "Lockdown")!.Data);

        fs.Write("sys/kernel/security/lockdown", "none integrity [confidentiality]\n");
        service.Refresh();
        Assert.Equal("confidentiality", hklm.GetValue(@"Security\LSM", "Lockdown")!.Data);
        stop.Cancel();
    }
}

public class RealSystemTests
{
    [Fact]
    public void The_plugins_read_this_machine_without_failing()
    {
        var hive = new Hive("Security") { Identity = RegistryIdentity.Root };
        SecurityBuilder.Fill(hive, SecurityBuilder.DefaultPlugins, "/");

        Assert.Equal(new[] { "AppArmor", "LSM", "PAM", "SELinux" }, hive.GetSubKeyNames("Providers"));
        Assert.All(hive.GetSubKeyNames("Providers"), p => Assert.Null(hive.GetValue("Providers\\" + p, "Error")));   // none failed here
    }
}
