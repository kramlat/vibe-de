using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Vde.SysMan;

// One source for part of HKLM\System. A plugin reads the machine (files below `Root`, and udev's database) and
// writes what Windows would have there through the context. Whole numbers are Int32 and yes/no is Bool: the
// registry's own types, not Windows' DWORD, so whatever sits on top (a wineserver) maps them as it needs.
public interface ISystemPlugin
{
    string Name { get; }
    bool IsAvailable(string root);
    void Fill(SystemContext context);
    IEnumerable<string> WatchedFiles(string root);
    IEnumerable<string> WatchedDirectories(string root);
}

public sealed class SystemContext(Hive hive, string root)
{
    public const string ControlSet = "ControlSet001";
    public const string CurrentControlSet = "CurrentControlSet";

    private Udev? _udev;

    public Hive Hive => hive;
    public string Root => root;
    public Udev Udev => _udev ??= new Udev(root);

    // A key and the keys above it, readable by everyone (it is all public machine state).
    public void Key(string path) => GeneratedHiveFill.Key(hive, path);
    public void Value(string key, string name, RegistryValueKind kind, object data) => GeneratedHiveFill.Public(hive, key, name, kind, data);
    public void Text(string key, string name, string? text) { if (!string.IsNullOrEmpty(text)) Value(key, name, RegistryValueKind.String, text); }
    public void Number(string key, string name, int number) => Value(key, name, RegistryValueKind.Int32, number);

    // The control set exists twice, as ControlSet001 and CurrentControlSet: the registry has no linked keys, so
    // what is written here is written to both, and the two cannot differ.
    public void ControlSetKey(string relative) { Key($@"{ControlSet}\{relative}"); Key($@"{CurrentControlSet}\{relative}"); }
    public void ControlSetValue(string relative, string name, RegistryValueKind kind, object data)
    {
        Value($@"{ControlSet}\{relative}", name, kind, data);
        Value($@"{CurrentControlSet}\{relative}", name, kind, data);
    }
    public void ControlSetText(string relative, string name, string? text) { if (!string.IsNullOrEmpty(text)) ControlSetValue(relative, name, RegistryValueKind.String, text); }
    public void ControlSetNumber(string relative, string name, int number) => ControlSetValue(relative, name, RegistryValueKind.Int32, number);

    public string? Read(string path)
    {
        try { return File.ReadAllText(Path.Combine(root, path.TrimStart('/'))).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public string FilePath(string path) => Path.Combine(root, path.TrimStart('/'));
}

public static class SystemBuilder
{
    public static IReadOnlyList<ISystemPlugin> DefaultPlugins { get; } =
        [new ControlPlugin(), new ClassPlugin(), new EnumPlugin(), new ServicesPlugin(), new NetworkPlugin(), new HardwareProfilePlugin()];

    // Vde\Providers\<name>: Available (Bool) and Error (String) if the plugin failed; a failing plugin does not stop the rest.
    public static void Fill(Hive hive, IEnumerable<ISystemPlugin> plugins, string root = "/")
    {
        foreach (var key in hive.GetSubKeyNames(""))
            hive.DeleteKey(key);
        var context = new SystemContext(hive, root);
        context.Key(@"Vde\Providers");
        foreach (var plugin in plugins)
        {
            var provider = @"Vde\Providers\" + plugin.Name;
            context.Key(provider);
            bool available = false;
            try { available = plugin.IsAvailable(root); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            context.Value(provider, "Available", RegistryValueKind.Bool, available);
            if (!available)
                continue;
            try { plugin.Fill(context); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                context.Value(provider, "Error", RegistryValueKind.String, e.Message);
            }
        }
    }
}
