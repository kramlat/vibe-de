namespace Vde.PamSam;

// One local account, as the Linux account model has it.
public sealed record AccountInfo(string Name, uint Uid, uint Gid, string Home, string Shell, string FullName, IReadOnlyList<string> Groups, bool Administrator);

// The local accounts, read from /etc/passwd and /etc/group (accounts from other directory services are not listed).
public sealed class AccountDirectory
{
    private readonly string _passwd, _group;
    private readonly string[] _adminGroups;

    // adminGroups: membership of any of these makes an account an administrator (who may be offered elevation).
    public AccountDirectory(string passwdPath = "/etc/passwd", string groupPath = "/etc/group", IEnumerable<string>? adminGroups = null)
    {
        _passwd = passwdPath;
        _group = groupPath;
        _adminGroups = (adminGroups ?? ["wheel", "sudo", "admin"]).ToArray();
    }

    public IReadOnlyList<string> AdministratorGroups => _adminGroups;

    public sealed record GroupInfo(string Name, uint Gid, IReadOnlyList<string> Members);

    public AccountInfo? FindByUid(uint uid) => Read().FirstOrDefault(a => a.Uid == uid);

    public IReadOnlyList<AccountInfo> All() => Read().ToList();

    public IReadOnlyList<GroupInfo> AllGroups() => Groups().Select(g => new GroupInfo(g.Name, g.Gid, g.Members.Order(StringComparer.Ordinal).ToList())).OrderBy(g => g.Gid).ToList();

    // Real accounts: uid 1000 up (and root), the ones with something to log in to.
    public IReadOnlyList<AccountInfo> People(uint minUid = 1000, uint maxUid = 60000)
        => Read().Where(a => a.Uid == 0 || (a.Uid >= minUid && a.Uid < maxUid)).OrderBy(a => a.Uid).ToList();

    private IEnumerable<AccountInfo> Read()
    {
        if (!File.Exists(_passwd))
            yield break;
        var groups = Groups();
        foreach (var line in File.ReadLines(_passwd))
        {
            var f = line.Split(':');   // name:password:uid:gid:gecos:home:shell
            if (line.Length == 0 || line[0] == '#' || f.Length < 7 || !uint.TryParse(f[2], out var uid) || !uint.TryParse(f[3], out var gid))
                continue;
            var names = groups.Where(g => g.Gid == gid || g.Members.Contains(f[0])).Select(g => g.Name).Distinct().Order(StringComparer.Ordinal).ToList();
            yield return new AccountInfo(f[0], uid, gid, f[5], f[6], f[4].Split(',')[0], names, names.Any(n => _adminGroups.Contains(n)));
        }
    }

    private List<(string Name, uint Gid, HashSet<string> Members)> Groups()
    {
        var list = new List<(string, uint, HashSet<string>)>();
        if (File.Exists(_group))
            foreach (var line in File.ReadLines(_group))
                if (line.Split(':') is { Length: >= 4 } f && uint.TryParse(f[2], out var gid))
                    list.Add((f[0], gid, f[3].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet()));
        return list;
    }
}
