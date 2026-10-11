public enum RegValueType
{
    // Windows
    String,
    ExpandString,
    MultiString,
    DWord,
    QWord,
    Binary,

    // Fluent
    Bool,
    Int32,
    Int64,
    Float,
    Double,
    Decimal,
    Date,
    Time,
    DateTime,
    Guid,
    Json,
    Yaml,
    Xml,
    Variant,
    Metadata,

    None
}

public sealed class RegV6Parser
{
    private readonly List<string> _lines;
    private int _index;
    private bool _isV6;

    public RegV6Parser(IEnumerable<string> lines)
    {
        _lines = lines.ToList();
    }

    public static RegFile Parse(string text)
    {
        var lines = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();

        return new RegV6Parser(lines).ParseInternal();
    }
}

private RegFile ParseInternal()
{
    var file = new RegFile();

    while (!EOF)
    {
        var line = NextNonEmptyLine();
        if (line == null)
            break;

        if (line.StartsWith("Windows Registry Editor Version 6.00"))
        {
            _isV6 = true;
            continue;
        }

        if (line.StartsWith("Windows Registry Editor Version 5.00") ||
            line.StartsWith("REGEDIT4"))
        {
            _isV6 = false;
            continue;
        }

        // Key header
        if (IsKeyHeader(line))
        {
            var section = new RegKeySection(ParseKeyPath(line));
            file.Sections.Add(section);
            ParseSection(section);
        }
    }

    return file;
}

private void ParseSection(RegKeySection section)
{
    while (!EOF)
    {
        var line = PeekLine()?.Trim();
        if (line == null || line.StartsWith("["))
            return;

        _index++;

        if (IsComment(line) || line.Length == 0)
            continue;

        if (IsValueLine(line))
        {
            var entry = _isV6
                ? ParseValueLineV6(line)
                : ParseValueLineLegacy(line);

            section.Values.Add(entry);
        }
    }
}

private RegValueEntry ParseValueLineV6(string line)
{
    int eq = line.IndexOf('=');
    string name = ParseValueName(line[..eq].Trim());
    string rhs = line[(eq + 1)..].Trim();

    // Default value → always Variant
    bool isDefault = name.Length == 0;

    // Fluent type prefixes
    if (rhs.StartsWith("bool:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Bool, ParseBool(rhs[5..]), isDefault);

    if (rhs.StartsWith("int32:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Int32, int.Parse(rhs[6..]), isDefault);

    if (rhs.StartsWith("int64:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Int64, long.Parse(rhs[6..]), isDefault);

    if (rhs.StartsWith("float:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Float, float.Parse(rhs[6..]), isDefault);

    if (rhs.StartsWith("double:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Double, double.Parse(rhs[7..]), isDefault);

    if (rhs.StartsWith("decimal:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Decimal, decimal.Parse(rhs[8..]), isDefault);

    if (rhs.StartsWith("date:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Date, DateOnly.Parse(rhs[5..]), isDefault);

    if (rhs.StartsWith("time:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Time, TimeOnly.Parse(rhs[5..]), isDefault);

    if (rhs.StartsWith("datetime:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.DateTime, DateTime.Parse(rhs[9..]), isDefault);

    if (rhs.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Guid, Guid.Parse(rhs[5..]), isDefault);

    if (rhs.StartsWith("json:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Json, ParseQuotedString(rhs[5..]), isDefault);

    if (rhs.StartsWith("yaml:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Yaml, ParseQuotedString(rhs[5..]), isDefault);

    if (rhs.StartsWith("xml:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Xml, ParseQuotedString(rhs[4..]), isDefault);

    if (rhs.StartsWith("meta:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Metadata, ParseQuotedString(rhs[5..]), isDefault);

    // Windows-compatible types
    if (rhs.StartsWith("\""))
        return VariantOrDirect(name, RegValueType.String, ParseQuotedString(rhs), isDefault);

    if (rhs.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.DWord, ParseDword(rhs[6..]), isDefault);

    if (rhs.StartsWith("qword:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.QWord, ParseQword(rhs[6..]), isDefault);

    if (rhs.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.Binary, ParseBinary(rhs[4..]), isDefault);

    if (rhs.StartsWith("hex(7):", StringComparison.OrdinalIgnoreCase))
        return VariantOrDirect(name, RegValueType.MultiString, ParseMultiSz(rhs[7..]), isDefault);

    return new RegValueEntry(name, RegValueType.None, null);
}

private RegValueEntry VariantOrDirect(
    string name,
    RegValueType type,
    object value,
    bool forceVariant)
{
    if (forceVariant)
        return new RegValueEntry(name, RegValueType.Variant, new VariantValue(type, value));

    return new RegValueEntry(name, type, value);
}

public sealed class VariantValue
{
    public RegValueType InnerType { get; }
    public object Value { get; }

    public VariantValue(RegValueType innerType, object value)
    {
        InnerType = innerType;
        Value = value;
    }
}

public static class RegFileReader
{
    public static RegFile Load(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        return LoadFromString(text);
    }

    public static RegFile LoadFromString(string text)
    {
        // Normalize line endings
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        // Detect version
        if (text.StartsWith("Windows Registry Editor Version 6.00"))
            return RegV6Parser.Parse(text);

        if (text.StartsWith("Windows Registry Editor Version 5.00") ||
            text.StartsWith("REGEDIT4"))
            return RegLegacyParser.Parse(text);

        throw new FormatException("Unknown .reg file format");
    }
}

public static class RegFileWriter
{
    public static void Save(RegFile file, string path)
    {
        string text = WriteToString(file);
        File.WriteAllText(path, text, Encoding.UTF8);
    }

    public static string WriteToString(RegFile file)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("Windows Registry Editor Version 6.00");
        sb.AppendLine("; Fluent Registry Export");
        sb.AppendLine();

        foreach (var section in file.Sections)
        {
            sb.AppendLine($"[{section.KeyPath}]");

            foreach (var value in section.Values)
                WriteValue(sb, value);

            sb.AppendLine();
        }

        return sb.ToString();
    }
}

private static void WriteValue(StringBuilder sb, RegValueEntry entry)
{
    string name = entry.Name.Length == 0 ? "@" : $"\"{entry.Name}\"";

    // Variant wrapper
    if (entry.Type == RegValueType.Variant)
    {
        var variant = (VariantValue)entry.Data!;
        sb.Append(name);
        sb.Append('=');
        WriteTypedValue(sb, variant.InnerType, variant.Value);
        sb.AppendLine();
        return;
    }

    // Direct type
    sb.Append(name);
    sb.Append('=');
    WriteTypedValue(sb, entry.Type, entry.Data);
    sb.AppendLine();
}

private static void WriteTypedValue(StringBuilder sb, RegValueType type, object? value)
{
    switch (type)
    {
        case RegValueType.String:
            sb.Append('"').Append(Escape((string)value!)).Append('"');
            break;

        case RegValueType.ExpandString:
            sb.Append("hex(2):").Append(ToHex(Encoding.Unicode.GetBytes((string)value!)));
            break;

        case RegValueType.MultiString:
            sb.Append("hex(7):").Append(ToHex(ToMultiSz((string[])value!)));
            break;

        case RegValueType.DWord:
            sb.Append("dword:").Append(((uint)value!).ToString("x8"));
            break;

        case RegValueType.QWord:
            sb.Append("qword:").Append(((ulong)value!).ToString("x16"));
            break;

        case RegValueType.Binary:
            sb.Append("hex:").Append(ToHex((byte[])value!));
            break;

        // Fluent types
        case RegValueType.Bool:
            sb.Append("bool:").Append(((bool)value!) ? "true" : "false");
            break;

        case RegValueType.Int32:
            sb.Append("int32:").Append(value);
            break;

        case RegValueType.Int64:
            sb.Append("int64:").Append(value);
            break;

        case RegValueType.Float:
            sb.Append("float:").Append(((float)value!).ToString("R"));
            break;

        case RegValueType.Double:
            sb.Append("double:").Append(((double)value!).ToString("R"));
            break;

        case RegValueType.Decimal:
            sb.Append("decimal:").Append(((decimal)value!).ToString());
            break;

        case RegValueType.Date:
            sb.Append("date:").Append(((DateOnly)value!).ToString("yyyy-MM-dd"));
            break;

        case RegValueType.Time:
            sb.Append("time:").Append(((TimeOnly)value!).ToString("HH:mm:ss"));
            break;

        case RegValueType.DateTime:
            sb.Append("datetime:").Append(((DateTime)value!).ToString("o"));
            break;

        case RegValueType.Guid:
            sb.Append("guid:").Append(((Guid)value!).ToString("B"));
            break;

        case RegValueType.Json:
            sb.Append("json:\"").Append(Escape((string)value!)).Append('"');
            break;

        case RegValueType.Yaml:
            sb.Append("yaml:\"").Append(Escape((string)value!)).Append('"');
            break;

        case RegValueType.Xml:
            sb.Append("xml:\"").Append(Escape((string)value!)).Append('"');
            break;

        case RegValueType.Metadata:
            sb.Append("meta:\"").Append(Escape((string)value!)).Append('"');
            break;

        default:
            sb.Append(";");
            break;
    }
}

private static string Escape(string s)
    => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

private static string ToHex(byte[] bytes)
    => string.Join(",", bytes.Select(b => b.ToString("x2")));

private static byte[] ToMultiSz(string[] strings)
{
    var ms = new MemoryStream();
    foreach (var s in strings)
    {
        var bytes = Encoding.Unicode.GetBytes(s);
        ms.Write(bytes, 0, bytes.Length);
        ms.WriteByte(0);
        ms.WriteByte(0);
    }
    ms.WriteByte(0);
    ms.WriteByte(0);
    return ms.ToArray();
}

public static class RegLegacyParser
{
    public static RegFile Parse(string text)
    {
        var lines = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();

        return new RegLegacyParserImpl(lines).ParseInternal();
    }

    private sealed class RegLegacyParserImpl
    {
        private readonly List<string> _lines;
        private int _index;

        public RegLegacyParserImpl(List<string> lines)
        {
            _lines = lines;
        }

        private bool EOF => _index >= _lines.Count;

        public RegFile ParseInternal()
        {
            var file = new RegFile();

            while (!EOF)
            {
                var line = NextNonEmptyLine();
                if (line == null)
                    break;

                if (IsComment(line))
                    continue;

                if (IsHeader(line))
                    continue;

                if (IsKeyHeader(line))
                {
                    var section = new RegKeySection(ParseKeyPath(line));
                    file.Sections.Add(section);
                    ParseSection(section);
                }
            }

            return file;
        }

        private void ParseSection(RegKeySection section)
        {
            while (!EOF)
            {
                var line = PeekLine()?.Trim();
                if (line == null || line.StartsWith("["))
                    return;

                _index++;

                if (IsComment(line) || line.Length == 0)
                    continue;

                if (IsValueLine(line))
                {
                    var entry = ParseValueLine(line);
                    section.Values.Add(entry);
                }
            }
        }

        private RegValueEntry ParseValueLine(string line)
        {
            int eq = line.IndexOf('=');
            if (eq < 0)
                throw new FormatException($"Invalid value line: {line}");

            string name = ParseValueName(line[..eq].Trim());
            string rhs = line[(eq + 1)..].Trim();

            // REG_SZ
            if (rhs.StartsWith("\""))
                return new RegValueEntry(name, RegValueType.String, ParseQuotedString(rhs));

            // REG_EXPAND_SZ
            if (rhs.StartsWith("hex(2):", StringComparison.OrdinalIgnoreCase))
                return new RegValueEntry(name, RegValueType.ExpandString, ParseHexString(rhs[7..]));

            // REG_MULTI_SZ
            if (rhs.StartsWith("hex(7):", StringComparison.OrdinalIgnoreCase))
                return new RegValueEntry(name, RegValueType.MultiString, ParseMultiSz(rhs[7..]));

            // REG_BINARY
            if (rhs.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
                return new RegValueEntry(name, RegValueType.Binary, ParseBinary(rhs[4..]));

            // REG_DWORD
            if (rhs.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
                return new RegValueEntry(name, RegValueType.DWord, ParseDword(rhs[6..]));

            // REG_QWORD
            if (rhs.StartsWith("qword:", StringComparison.OrdinalIgnoreCase))
                return new RegValueEntry(name, RegValueType.QWord, ParseQword(rhs[6..]));

            return new RegValueEntry(name, RegValueType.None, null);
        }

        private static string ParseValueName(string raw)
        {
            raw = raw.Trim();
            if (raw == "@")
                return string.Empty;

            if (raw.StartsWith("\"") && raw.EndsWith("\""))
                return raw.Trim('"');

            throw new FormatException($"Invalid value name: {raw}");
        }

        private static string ParseQuotedString(string rhs)
        {
            if (!rhs.StartsWith("\""))
                throw new FormatException("Expected quoted string");

            var sb = new StringBuilder();
            bool escape = false;

            for (int i = 1; i < rhs.Length; i++)
            {
                char c = rhs[i];

                if (escape)
                {
                    sb.Append(c switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        _ => c
                    });
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                    break;

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static byte[] ParseBinary(string hex)
        {
            var parts = hex.Split(',', StringSplitOptions.RemoveEmptyEntries);
            return parts.Select(p => Convert.ToByte(p, 16)).ToArray();
        }

        private static string[] ParseMultiSz(string hex)
        {
            var bytes = ParseBinary(hex);
            var utf16 = Encoding.Unicode.GetString(bytes);
            return utf16.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        private static uint ParseDword(string hex)
            => Convert.ToUInt32(hex, 16);

        private static ulong ParseQword(string hex)
            => Convert.ToUInt64(hex, 16);

        private string? NextNonEmptyLine()
        {
            while (!EOF)
            {
                var line = _lines[_index++].Trim();
                if (line.Length == 0)
                    continue;
                return line;
            }
            return null;
        }

        private string? PeekLine()
            => EOF ? null : _lines[_index];

        private static bool IsComment(string line)
            => line.StartsWith(";") || line.StartsWith("//");

        private static bool IsHeader(string line)
            => line.StartsWith("REGEDIT4", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Windows Registry Editor Version 5.00", StringComparison.OrdinalIgnoreCase);

        private static bool IsKeyHeader(string line)
            => line.StartsWith("[") && line.EndsWith("]");

        private static string ParseKeyPath(string line)
            => line.Trim('[', ']');

        private static bool IsValueLine(string line)
            => line.Contains("=") && !line.StartsWith("[");
    }
}
