using Vibe.Registry;

namespace Vde.PamSam;

// HKEY_LOCAL_MACHINE\SAM, made from the Linux account model: the accounts and groups PAM's own modules
// (pam_unix) work from, /etc/passwd, /etc/group and /etc/shadow. The hive this fills is the SAM key itself
// (its root is HKLM\SAM), laid out the way Windows' is:
//
//   Domains\Account                       Name (the machine's), UserCount, AdministratorGroups
//   Domains\Account\Users\<name>          Uid, Gid, FullName, Home, Shell, Groups, Administrator
//                                         and, readable only by the account itself and root:
//                                         PasswordState (Set, Locked or None), PasswordLastSet, PasswordExpires, AccountExpires
//   Domains\Account\Aliases\<group>       Gid, Members
//
// The password hash is never copied. Everything is regenerated when the account files change: the account
// database stays the one place accounts are managed, and nothing written here is kept.
public static class SamBuilder
{
    public const string AccountKey = @"Domains\Account";

    private static readonly PosixPermissions Browse = new(0, 0, 0x16D);   // r-xr-xr-x, root's

    public static void Fill(Hive hive, AccountDirectory accounts, string shadowPath = "/etc/shadow", string? machineName = null)
    {
        var shadow = ReadShadow(shadowPath);
        var all = accounts.All();
        var groups = accounts.AllGroups();
        var before = hive.Identity;
        hive.Identity = RegistryIdentity.Root;
        try
        {
            if (hive.KeyExists("Domains"))
                hive.DeleteKey("Domains");
            hive.CreateKey(AccountKey);
            hive.SetKeyPermissions("Domains", Browse);
            hive.SetKeyPermissions(AccountKey, Browse);
            Public(hive, AccountKey, "Name", RegistryValueKind.String, machineName ?? Environment.MachineName);
            Public(hive, AccountKey, "UserCount", RegistryValueKind.Int32, all.Count);
            Public(hive, AccountKey, "AdministratorGroups", RegistryValueKind.MultiString, accounts.AdministratorGroups.ToArray());

            hive.CreateKey(AccountKey + @"\Users");
            hive.SetKeyPermissions(AccountKey + @"\Users", Browse);
            foreach (var a in all)
            {
                var key = AccountKey + @"\Users\" + a.Name;
                hive.CreateKey(key);
                hive.SetKeyPermissions(key, new PosixPermissions(a.Uid, a.Gid, 0x16D));
                Public(hive, key, "Uid", RegistryValueKind.Int64, (long)a.Uid);
                Public(hive, key, "Gid", RegistryValueKind.Int64, (long)a.Gid);
                Public(hive, key, "FullName", RegistryValueKind.String, a.FullName);
                Public(hive, key, "Home", RegistryValueKind.String, a.Home);
                Public(hive, key, "Shell", RegistryValueKind.String, a.Shell);
                if (a.Groups.Count > 0)
                    Public(hive, key, "Groups", RegistryValueKind.MultiString, a.Groups.ToArray());
                Public(hive, key, "Administrator", RegistryValueKind.Bool, a.Administrator);
                if (shadow.TryGetValue(a.Name, out var s))
                {
                    Private(hive, key, a, "PasswordState", RegistryValueKind.String, s.State);
                    if (s.LastSet is { } set) Private(hive, key, a, "PasswordLastSet", RegistryValueKind.DateTime, set);
                    if (s.PasswordExpires is { } pe) Private(hive, key, a, "PasswordExpires", RegistryValueKind.DateTime, pe);
                    if (s.AccountExpires is { } ae) Private(hive, key, a, "AccountExpires", RegistryValueKind.DateTime, ae);
                }
            }

            hive.CreateKey(AccountKey + @"\Aliases");
            hive.SetKeyPermissions(AccountKey + @"\Aliases", Browse);
            foreach (var g in groups)
            {
                var key = AccountKey + @"\Aliases\" + g.Name;
                hive.CreateKey(key);
                hive.SetKeyPermissions(key, new PosixPermissions(0, g.Gid, 0x16D));
                Public(hive, key, "Gid", RegistryValueKind.Int64, (long)g.Gid);
                if (g.Members.Count > 0)
                    Public(hive, key, "Members", RegistryValueKind.MultiString, g.Members.ToArray());
            }
        }
        finally { hive.Identity = before; }
    }

    // World-readable, root's to change: the same facts /etc/passwd and /etc/group give everyone.
    private static void Public(Hive hive, string key, string name, RegistryValueKind kind, object data)
    {
        hive.SetValue(key, name, kind, data);
        hive.SetValuePermissions(key, name, new PosixPermissions(0, 0, 0x124));   // r--r--r--
    }

    // What /etc/shadow keeps from everyone else: the account itself (and root) may read it.
    private static void Private(Hive hive, string key, AccountInfo owner, string name, RegistryValueKind kind, object data)
    {
        hive.SetValue(key, name, kind, data);
        hive.SetValuePermissions(key, name, new PosixPermissions(owner.Uid, owner.Gid, 0x100));   // r--------
    }

    private sealed record ShadowEntry(string State, DateTime? LastSet, DateTime? PasswordExpires, DateTime? AccountExpires);

    // name:hash:lastchg:min:max:warn:inactive:expire:reserved, with dates as days since 1970. The hash is looked at
    // only to tell whether a password is set, locked ("!" or "*" in front) or absent; it is not kept.
    private static Dictionary<string, ShadowEntry> ReadShadow(string path)
    {
        var entries = new Dictionary<string, ShadowEntry>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var f = line.Split(':');
                if (f.Length < 8 || f[0].Length == 0)
                    continue;
                var state = f[1].Length == 0 ? "None" : f[1][0] is '!' or '*' ? "Locked" : "Set";
                DateTime? Day(string text, long add = 0) => long.TryParse(text, out var d) && d > 0 ? DateTime.UnixEpoch.AddDays(d + add) : null;
                var last = Day(f[2]);
                DateTime? passwordExpires = long.TryParse(f[4], out var max) && max is > 0 and < 99999 && last is { } l ? l.AddDays(max) : null;
                entries[f[0]] = new ShadowEntry(state, last, passwordExpires, Day(f[7]));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // not root: no password facts, the rest is still there
        return entries;
    }
}
