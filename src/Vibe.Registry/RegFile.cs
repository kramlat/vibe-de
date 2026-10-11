using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Vibe.Registry;

public sealed class RegEntry
{
    public string Name { get; init; } = string.Empty;   // "" is the default value (@)
    public RegistryValueKind Kind { get; init; }
    public object? Data { get; init; }
    public bool Delete { get; init; }                   // "name"=-
}

public sealed class RegSection
{
    public string KeyPath { get; init; } = string.Empty;   // as written, including the HKEY_ root
    public bool DeleteKey { get; init; }                    // [-path]
    public List<RegEntry> Entries { get; } = new();
}

// Reads and writes Windows Registry Editor 5.00 files. Standard kinds use their regedit text forms;
// Vibe extension kinds are stored as hex(N) with the hive file's byte encoding.
public sealed class RegFile
{
    private const string Header5 = "Windows Registry Editor Version 5.00";
    private const string Header4 = "REGEDIT4";

    public List<RegSection> Sections { get; } = new();

    // ---- reading ----

    public static RegFile Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static RegFile Load(Stream stream)
    {
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();

        Encoding encoding = new UTF8Encoding(false);
        int skip = 0;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = Encoding.Unicode; skip = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = Encoding.BigEndianUnicode; skip = 2; }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { skip = 3; }

        return Parse(encoding.GetString(bytes, skip, bytes.Length - skip));
    }

    public static RegFile Parse(string text)
    {
        var file = new RegFile();
        var rawLines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        bool sawHeader = false;
        RegSection? section = null;

        for (int i = 0; i < rawLines.Length; i++)
        {
            int lineNo = i + 1;
            var line = rawLines[i].Trim();

            // A trailing backslash continues a hex value onto the next line.
            while (line.EndsWith('\\') && i + 1 < rawLines.Length)
            {
                line = line[..^1] + rawLines[++i].Trim();
            }

            if (line.Length == 0 || line[0] == ';')
                continue;

            if (!sawHeader)
            {
                if (line != Header5 && line != Header4)
                    throw new FormatException($"Line {lineNo}: missing '{Header5}' header.");
                sawHeader = true;
                continue;
            }

            if (line[0] == '[')
            {
                if (!line.EndsWith(']'))
                    throw new FormatException($"Line {lineNo}: unterminated section header.");
                var path = line[1..^1];
                bool delete = path.StartsWith('-');
                section = new RegSection { KeyPath = delete ? path[1..] : path, DeleteKey = delete };
                file.Sections.Add(section);
                continue;
            }

            if (section is null)
                throw new FormatException($"Line {lineNo}: value outside a section.");
            section.Entries.Add(ParseEntry(line, lineNo));
        }

        if (!sawHeader)
            throw new FormatException("Empty .reg file.");
        return file;
    }

    private static RegEntry ParseEntry(string line, int lineNo)
    {
        int pos = 0;
        string name;
        if (line[0] == '@')
        {
            name = string.Empty;
            pos = 1;
        }
        else if (line[0] == '"')
        {
            name = ParseQuoted(line, ref pos, lineNo);
        }
        else
        {
            throw new FormatException($"Line {lineNo}: expected a value name.");
        }

        while (pos < line.Length && line[pos] == ' ') pos++;
        if (pos >= line.Length || line[pos] != '=')
            throw new FormatException($"Line {lineNo}: expected '='.");
        var rest = line[(pos + 1)..].Trim();

        if (rest == "-")
            return new RegEntry { Name = name, Delete = true };

        if (rest.StartsWith('"'))
        {
            int p = 0;
            var s = ParseQuoted(rest, ref p, lineNo);
            if (rest[p..].Trim().Length != 0)
                throw new FormatException($"Line {lineNo}: unexpected text after string value.");
            return new RegEntry { Name = name, Kind = RegistryValueKind.String, Data = s };
        }

        if (rest.StartsWith("dword:", StringComparison.Ordinal))
        {
            if (!uint.TryParse(rest[6..].Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var d))
                throw new FormatException($"Line {lineNo}: bad dword.");
            return new RegEntry { Name = name, Kind = RegistryValueKind.DWord, Data = d };
        }

        if (rest.StartsWith("hex", StringComparison.Ordinal))
        {
            int p = 3;
            int type = 3;
            if (p < rest.Length && rest[p] == '(')
            {
                int close = rest.IndexOf(')');
                if (close < 0 || !int.TryParse(rest[(p + 1)..close], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out type))
                    throw new FormatException($"Line {lineNo}: bad hex type.");
                p = close + 1;
            }
            if (p >= rest.Length || rest[p] != ':')
                throw new FormatException($"Line {lineNo}: expected ':' after hex.");

            var bytes = ParseHexBytes(rest[(p + 1)..], lineNo);
            var (kind, data) = DecodeHex(type, bytes, lineNo);
            return new RegEntry { Name = name, Kind = kind, Data = data };
        }

        throw new FormatException($"Line {lineNo}: unrecognised value.");
    }

    private static string ParseQuoted(string s, ref int pos, int lineNo)
    {
        var sb = new StringBuilder();
        pos++; // opening quote
        while (pos < s.Length)
        {
            char c = s[pos++];
            if (c == '"')
                return sb.ToString();
            if (c == '\\' && pos < s.Length)
            {
                char n = s[pos];
                if (n == '\\' || n == '"') { sb.Append(n); pos++; continue; }
            }
            sb.Append(c);
        }
        throw new FormatException($"Line {lineNo}: unterminated string.");
    }

    private static byte[] ParseHexBytes(string s, int lineNo)
    {
        var bytes = new List<byte>();
        foreach (var part in s.Split(','))
        {
            var t = part.Trim();
            if (t.Length == 0) continue;
            if (!byte.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
                throw new FormatException($"Line {lineNo}: bad hex byte '{t}'.");
            bytes.Add(b);
        }
        return bytes.ToArray();
    }

    private static (RegistryValueKind, object?) DecodeHex(int type, byte[] b, int lineNo)
    {
        try
        {
            switch (type)
            {
                case 0: return (RegistryValueKind.None, null);
                case 1: return (RegistryValueKind.String, Utf16(b));
                case 2: return (RegistryValueKind.ExpandString, Utf16(b));
                case 3: return (RegistryValueKind.Binary, b);
                case 4: return (RegistryValueKind.DWord, BinaryPrimitives.ReadUInt32LittleEndian(Exactly(b, 4)));
                case 5: return (RegistryValueKind.DWordBigEndian, BinaryPrimitives.ReadUInt32BigEndian(Exactly(b, 4)));
                case 6: return (RegistryValueKind.Link, Utf16(b));
                case 7: return (RegistryValueKind.MultiString, Utf16Multi(b));
                case 8: return (RegistryValueKind.ResourceList, b);
                case 9: return (RegistryValueKind.FullResourceDescriptor, b);
                case 10: return (RegistryValueKind.ResourceRequirementsList, b);
                case 11: return (RegistryValueKind.QWord, BinaryPrimitives.ReadUInt64LittleEndian(Exactly(b, 8)));
                default:
                    var kind = (RegistryValueKind)type;
                    if (type >= 100 && type <= ushort.MaxValue && Enum.IsDefined(kind))
                        return (kind, ValueCodec.Decode(kind, b));
                    throw new FormatException($"unsupported hex type {type:x}");
            }
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            throw new FormatException($"Line {lineNo}: {e.Message}", e);
        }
    }

    private static ReadOnlySpan<byte> Exactly(byte[] b, int n)
        => b.Length == n ? b : throw new InvalidDataException($"expected {n} data bytes, found {b.Length}");

    private static string Utf16(byte[] b)
    {
        if (b.Length % 2 != 0)
            throw new InvalidDataException("UTF-16 data has odd length");
        return Encoding.Unicode.GetString(b).TrimEnd('\0');
    }

    private static string[] Utf16Multi(byte[] b)
    {
        var text = Utf16(b);
        return text.Length == 0 ? [] : text.Split('\0');
    }

    // ---- writing ----

    public void Save(string path)
    {
        using var stream = File.Create(path);
        Save(stream);
    }

    // UTF-16 LE with a byte order mark and CRLF line endings, as regedit writes it.
    public void Save(Stream stream)
    {
        using var writer = new StreamWriter(stream, new UnicodeEncoding(false, true), leaveOpen: true);
        writer.Write(Header5 + "\r\n");

        foreach (var section in Sections)
        {
            writer.Write("\r\n[" + (section.DeleteKey ? "-" : "") + section.KeyPath + "]\r\n");
            foreach (var entry in section.Entries)
                writer.Write(FormatEntry(entry) + "\r\n");
        }
    }

    private static string FormatEntry(RegEntry e)
    {
        var name = e.Name.Length == 0 ? "@" : "\"" + Escape(e.Name) + "\"";
        if (e.Delete)
            return name + "=-";

        switch (e.Kind)
        {
            case RegistryValueKind.String:
                return name + "=\"" + Escape((string)e.Data!) + "\"";
            case RegistryValueKind.DWord:
                return name + "=dword:" + ((uint)e.Data!).ToString("x8", CultureInfo.InvariantCulture);
            case RegistryValueKind.Binary:
                return Wrap(name + "=hex:", (byte[])e.Data!);
        }

        var (type, bytes) = EncodeHex(e);
        return Wrap($"{name}=hex({type:x}):", bytes);
    }

    private static (int type, byte[] bytes) EncodeHex(RegEntry e)
    {
        switch (e.Kind)
        {
            case RegistryValueKind.None: return (0, []);
            case RegistryValueKind.ExpandString: return (2, Sz((string)e.Data!));
            case RegistryValueKind.Link: return (6, Sz((string)e.Data!));
            case RegistryValueKind.MultiString:
                var ms = new MemoryStream();
                foreach (var s in (string[])e.Data!)
                    ms.Write(Sz(s));
                ms.Write([0, 0]);
                return (7, ms.ToArray());
            case RegistryValueKind.DWordBigEndian:
                var be = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(be, (uint)e.Data!);
                return (5, be);
            case RegistryValueKind.ResourceList: return (8, (byte[])e.Data!);
            case RegistryValueKind.FullResourceDescriptor: return (9, (byte[])e.Data!);
            case RegistryValueKind.ResourceRequirementsList: return (10, (byte[])e.Data!);
            case RegistryValueKind.QWord: return (11, ValueCodec.Encode(RegistryValueKind.QWord, e.Data));
            default: return ((int)e.Kind, ValueCodec.Encode(e.Kind, e.Data));
        }
    }

    private static byte[] Sz(string s) => Encoding.Unicode.GetBytes(s + "\0");

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // Wraps long hex lists the way regedit does: continuation lines start with two spaces and end in '\'.
    private static string Wrap(string head, byte[] bytes)
    {
        var sb = new StringBuilder(head);
        int lineLen = head.Length;
        for (int i = 0; i < bytes.Length; i++)
        {
            var token = bytes[i].ToString("x2", CultureInfo.InvariantCulture) + (i < bytes.Length - 1 ? "," : "");
            if (lineLen + token.Length > 77)
            {
                sb.Append("\\\r\n  ");
                lineLen = 2;
            }
            sb.Append(token);
            lineLen += token.Length;
        }
        return sb.ToString();
    }

    // ---- hive interchange ----

    // Applies the sections under rootPrefix (e.g. "HKEY_CURRENT_USER") to a hive. Other sections are skipped.
    // Returns how many sections were applied.
    public int ApplyTo(Hive hive, string rootPrefix)
    {
        rootPrefix = rootPrefix.Trim('\\');
        int applied = 0;

        foreach (var section in Sections)
        {
            if (!TryStrip(section.KeyPath, rootPrefix, out var relative))
                continue;

            if (section.DeleteKey)
            {
                hive.DeleteKey(relative);
            }
            else
            {
                hive.CreateKey(relative);
                foreach (var entry in section.Entries)
                {
                    if (entry.Delete)
                        hive.DeleteValue(relative, entry.Name);
                    else
                        hive.SetValue(relative, entry.Name, entry.Kind, entry.Data);
                }
            }
            applied++;
        }
        return applied;
    }

    // Snapshots a key and all its descendants. Section paths are prefixed with rootPrefix.
    public static RegFile FromHive(Hive hive, string keyPath, string rootPrefix)
    {
        keyPath = keyPath.Trim('\\');
        if (!hive.KeyExists(keyPath))
            throw new KeyNotFoundException($"Key '{keyPath}' does not exist.");

        var file = new RegFile();
        AddSection(file, hive, keyPath, rootPrefix.Trim('\\'));
        return file;
    }

    private static void AddSection(RegFile file, Hive hive, string path, string rootPrefix)
    {
        var section = new RegSection
        {
            KeyPath = path.Length == 0 ? rootPrefix : rootPrefix.Length == 0 ? path : rootPrefix + "\\" + path,
        };
        foreach (var value in hive.GetValues(path))
            section.Entries.Add(new RegEntry { Name = value.Name, Kind = value.Kind, Data = value.Data });
        file.Sections.Add(section);

        foreach (var child in hive.GetSubKeyNames(path))
            AddSection(file, hive, path.Length == 0 ? child : path + "\\" + child, rootPrefix);
    }

    private static bool TryStrip(string path, string prefix, out string relative)
    {
        relative = path;
        if (prefix.Length == 0)
            return true;
        if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            relative = string.Empty;
            return true;
        }
        if (path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
        {
            relative = path[(prefix.Length + 1)..];
            return true;
        }
        return false;
    }
}
