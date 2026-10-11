using System.Globalization;
using Vibe.Registry;

namespace Mkhive;

internal static class HiveDump
{
    // Every key and value, depth first, as (path, value or null for the key itself).
    public static IEnumerable<(string path, RegistryValue? value)> Walk(Hive hive, string path = "")
    {
        yield return (path, null);
        foreach (var value in hive.GetValues(path))
            yield return (path, value);
        foreach (var child in hive.GetSubKeyNames(path))
            foreach (var entry in Walk(hive, path.Length == 0 ? child : path + "\\" + child))
                yield return entry;
    }

    // ls -l style: "drwxr-xr-x 0:0 \Software" for keys, "-rw-r--r-- 0:0 \Software\Name = String "x"" for values.
    public static void Write(Hive hive, TextWriter output)
    {
        foreach (var (path, value) in Walk(hive))
        {
            if (value is null)
            {
                var p = hive.GetKeyPermissions(path);
                output.WriteLine($"d{p.Symbolic} {p.Uid}:{p.Gid} \\{path}");
            }
            else
            {
                var p = value.Permissions;
                var name = value.Name.Length == 0 ? "@" : value.Name;
                output.WriteLine($"-{p.Symbolic} {p.Uid}:{p.Gid} \\{path}\\{name} = {value.Kind} {Describe(value)}");
            }
        }
    }

    private static string Describe(RegistryValue value) => value.Data switch
    {
        null => "",
        string s => "\"" + s.Replace("\n", "\\n") + "\"",
        string[] m => "[" + string.Join(", ", m.Select(s => "\"" + s + "\"")) + "]",
        byte[] b => string.Join(' ', b.Take(16).Select(x => x.ToString("x2", CultureInfo.InvariantCulture))) + (b.Length > 16 ? $" ...({b.Length} bytes)" : ""),
        uint d => "0x" + d.ToString("x8", CultureInfo.InvariantCulture),
        ulong q => "0x" + q.ToString("x16", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString() ?? "",
    };
}
