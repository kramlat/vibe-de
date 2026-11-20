using Vibe.Registry;
using Vde.Components;
using Vde.Shell.Notifications;
using VExplore;

namespace VExplore.Tests;

public class HostTests
{
    private static ExplorerHost Host(out Hive user, out Hive system)
    {
        user = new Hive("HKCU");
        system = new Hive("HKLM");
        return new ExplorerHost(new RegistryStore(system, user));
    }

    [Fact]
    public void Notification_settings_follow_the_registry()
    {
        using var host = Host(out var user, out _);
        user.SetValue(NotificationSettingsStore.KeyPath, "DoNotDisturb", RegistryValueKind.DWord, 1u);
        Assert.True(host.Notifications.Settings.DoNotDisturb);
    }

    [Fact]
    public void Components_are_split_into_loadable_and_refused()
    {
        using var host = Host(out var user, out _);
        var dll = Path.Combine(Path.GetTempPath(), "vexplore-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(dll, "x");
        try
        {
            var hkcr = host.Classes;
            ComponentRegistrar.Register(hkcr, new ComponentInfo("{11111111-0000-4000-8000-000000000001}", "N.Good", dll, "A", ThreadingModel.Apartment));
            ComponentRegistrar.Register(hkcr, new ComponentInfo("{11111111-0000-4000-8000-000000000002}", "N.Inert", dll, "A", ThreadingModel.Free));
            var inert = $@"CLSID\{{11111111-0000-4000-8000-000000000002}}\InprocServer32";
            hkcr.SetValuePermissions(inert, "", hkcr.GetValuePermissions(inert, "")!.Value.WithMode(PosixPermissions.ValueDefault));   // not executable

            Assert.Equal("N.Good", Assert.Single(host.LoadableComponents).Component.ClassName);
            Assert.Equal("N.Inert", Assert.Single(host.RefusedComponents).Component.ClassName);

            var text = new StringWriter();
            ExplorerCommand.ListComponents(host, text);
            Assert.Contains("load     {11111111-0000-4000-8000-000000000001}", text.ToString());
            Assert.Contains("refused  {11111111-0000-4000-8000-000000000002}", text.ToString());
        }
        finally { File.Delete(dll); }
    }

    [Fact]
    public void Help_and_bad_arguments()
    {
        var o = new StringWriter(); var e = new StringWriter();
        Assert.Equal(0, ExplorerCommand.Run(["--help"], o, e));
        Assert.Contains("Vibe Explorer", o.ToString());
    }
}
