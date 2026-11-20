using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Vde.SecMan;

// One source of security facts. A plugin writes its own key (named after the plugin) into the Security hive;
// it is asked only when it is available on this system, and a plugin that fails leaves the others alone.
// `root` is where the system's files are (always "/" outside tests).
public interface ISecurityPlugin
{
    string Name { get; }
    bool IsAvailable(string root);
    void Fill(Hive hive, string root);
    // Files whose change should refill the hive (sysfs does not notify; the service also refills on a timer).
    IEnumerable<string> WatchedFiles(string root);
}

public static class SecurityBuilder
{
    public static IReadOnlyList<ISecurityPlugin> DefaultPlugins { get; } = [new PamPlugin(), new SeLinuxPlugin(), new AppArmorPlugin(), new LsmPlugin()];

    // Providers\<name>: Available (Bool), and Error (String) if the plugin failed. Then each available plugin's own key.
    public static void Fill(Hive hive, IEnumerable<ISecurityPlugin> plugins, string root = "/")
    {
        foreach (var key in hive.GetSubKeyNames(""))
            hive.DeleteKey(key);
        GeneratedHiveFill.Key(hive, "Providers");
        foreach (var plugin in plugins)
        {
            var provider = "Providers\\" + plugin.Name;
            GeneratedHiveFill.Key(hive, provider);
            bool available = false;
            try { available = plugin.IsAvailable(root); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            GeneratedHiveFill.Public(hive, provider, "Available", RegistryValueKind.Bool, available);
            if (!available)
                continue;
            try
            {
                GeneratedHiveFill.Key(hive, plugin.Name);
                plugin.Fill(hive, root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
            {
                GeneratedHiveFill.Public(hive, provider, "Error", RegistryValueKind.String, e.Message);
            }
        }
    }

    internal static string? Read(string root, string path)
    {
        try { return File.ReadAllText(Path.Combine(root, path.TrimStart('/'))).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string FilePath(string root, string path) => Path.Combine(root, path.TrimStart('/'));
}
