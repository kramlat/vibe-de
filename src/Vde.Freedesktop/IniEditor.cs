namespace Vde.Freedesktop;

// Changes one key in a key file while leaving every other line (comments, unknown groups) untouched.
public static class IniEditor
{
    public static void SetValue(string path, string group, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var header = "[" + group + "]";

        int start = lines.FindIndex(l => l.Trim() == header);
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add(string.Empty);
            lines.Add(header);
            lines.Add($"{key}={value}");
        }
        else
        {
            int end = start + 1;
            int lastContent = start;
            int keyLine = -1;
            for (; end < lines.Count; end++)
            {
                var line = lines[end].Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                    break;
                if (line.Length > 0)
                    lastContent = end;
                int eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].TrimEnd() == key && keyLine < 0)
                    keyLine = end;
            }

            if (keyLine >= 0)
                lines[keyLine] = $"{key}={value}";
            else
                lines.Insert(lastContent + 1, $"{key}={value}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, string.Join('\n', lines) + "\n");
        File.Move(temp, path, overwrite: true);
    }
}
