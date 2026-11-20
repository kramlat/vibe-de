using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Vde.SecMan;

// PAM: what each service's stack is made of, read from /etc/pam.d. PAM\Services\<service> has one value per
// management group (auth, account, password, session), each a list of "control module arguments" lines in order;
// PAM\Modules is every module any service uses. These are the same files everyone can read.
public sealed class PamPlugin : ISecurityPlugin
{
    private static readonly string[] Groups = ["auth", "account", "password", "session"];

    public string Name => "PAM";
    public bool IsAvailable(string root) => Directory.Exists(SecurityBuilder.FilePath(root, "etc/pam.d"));
    public IEnumerable<string> WatchedFiles(string root)
        => Directory.Exists(SecurityBuilder.FilePath(root, "etc/pam.d")) ? Directory.EnumerateFiles(SecurityBuilder.FilePath(root, "etc/pam.d")) : [];

    public void Fill(Hive hive, string root)
    {
        var dir = SecurityBuilder.FilePath(root, "etc/pam.d");
        var modules = new SortedSet<string>(StringComparer.Ordinal);
        int services = 0;
        GeneratedHiveFill.Key(hive, @"PAM\Services");
        foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
        {
            var stacks = Groups.ToDictionary(g => g, _ => new List<string>());
            foreach (var line in File.ReadLines(file))
                if (Parse(line) is var (group, control, module, args) && stacks.TryGetValue(group, out var list))
                {
                    list.Add(string.Join(' ', new[] { control, module }.Concat(args)));
                    if (control is not ("include" or "substack"))
                        modules.Add(module);
                }
            var key = @"PAM\Services\" + Path.GetFileName(file);
            GeneratedHiveFill.Key(hive, key);
            foreach (var (group, lines) in stacks.Where(s => s.Value.Count > 0))
                GeneratedHiveFill.Public(hive, key, group, RegistryValueKind.MultiString, lines.ToArray());
            services++;
        }
        GeneratedHiveFill.Public(hive, "PAM", "ConfigDirectory", RegistryValueKind.String, "/etc/pam.d");
        GeneratedHiveFill.Public(hive, "PAM", "ServiceCount", RegistryValueKind.Int32, services);
        GeneratedHiveFill.Public(hive, "PAM", "Modules", RegistryValueKind.MultiString, modules.ToArray());
    }

    // "-session optional pam_systemd.so class=greeter" and "auth include system-auth"; a "[success=1 default=ignore]"
    // control is one word. The leading "-" (ignore a missing module) is not part of the group.
    internal static (string Group, string Control, string Module, string[] Args)? Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] == '#' || line[0] == '@')
            return null;
        var words = new List<string>();
        for (int i = 0; i < line.Length;)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;
            int start = i;
            if (line[i] == '[') { while (i < line.Length && line[i] != ']') i++; i = Math.Min(i + 1, line.Length); }
            else while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            words.Add(line[start..i]);
        }
        return words.Count >= 3 ? (words[0].TrimStart('-'), words[1], words[2], words.Skip(3).ToArray()) : null;
    }
}
