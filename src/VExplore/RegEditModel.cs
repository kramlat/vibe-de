using System.Globalization;
using System.Numerics;
using Vibe.Registry;

namespace VExplore;

// The parts of the registry editor that a window does not need to be on the screen for: what a value's data
// looks like as text and back, naming a key by rename (the registry has no move, so it is a copy and delete),
// and moving .reg files in and out of the hives.
public static class RegEditModel
{
    public const string UserRoot = "HKEY_CURRENT_USER";
    public const string MachineRoot = "HKEY_LOCAL_MACHINE";
    public const string ClassesRootName = "HKEY_CLASSES_ROOT";
    // HKEY_CLASSES_ROOT sections of a .reg file land in the user hive where ClassesRoot maps them.
    public const string ClassesBasePath = @"Software\Classes";

    // The kinds a new value can have, in the order the menu lists them (the Windows ones first, where they are
    // the ones people look for, then the Vibe ones sorted by name as the file format lists them).
    public static IReadOnlyList<RegistryValueKind> Kinds { get; } =
    [
        RegistryValueKind.String, RegistryValueKind.ExpandString, RegistryValueKind.Binary,
        RegistryValueKind.DWord, RegistryValueKind.QWord, RegistryValueKind.MultiString,
        RegistryValueKind.Bool, RegistryValueKind.Int32, RegistryValueKind.Int64,
        RegistryValueKind.Float, RegistryValueKind.Double, RegistryValueKind.Decimal,
        RegistryValueKind.Date, RegistryValueKind.Time, RegistryValueKind.DateTime, RegistryValueKind.Guid,
        RegistryValueKind.Json, RegistryValueKind.Yaml, RegistryValueKind.Xml, RegistryValueKind.Link,
    ];

    public static string KindName(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.None => "REG_NONE",
        RegistryValueKind.String => "REG_SZ",
        RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        RegistryValueKind.Binary => "REG_BINARY",
        RegistryValueKind.DWord => "REG_DWORD",
        RegistryValueKind.DWordBigEndian => "REG_DWORD_BIG_ENDIAN",
        RegistryValueKind.Link => "REG_LINK",
        RegistryValueKind.MultiString => "REG_MULTI_SZ",
        RegistryValueKind.ResourceList => "REG_RESOURCE_LIST",
        RegistryValueKind.FullResourceDescriptor => "REG_FULL_RESOURCE_DESCRIPTOR",
        RegistryValueKind.ResourceRequirementsList => "REG_RESOURCE_REQUIREMENTS_LIST",
        RegistryValueKind.QWord => "REG_QWORD",
        _ => kind.ToString(),
    };

    // The value list's Data column. Text kinds show as they are; regedit shows DWORDs as hex with the decimal
    // after, and so do we. Data the editor cannot edit as text still shows (as hex) so nothing is invisible.
    public static string Format(RegistryValue value) => Format(value.Kind, value.Data);

    public static string Format(RegistryValueKind kind, object? data) => kind switch
    {
        RegistryValueKind.None => "(no data)",
        RegistryValueKind.String or RegistryValueKind.ExpandString or RegistryValueKind.Link
            or RegistryValueKind.Json or RegistryValueKind.Yaml or RegistryValueKind.Xml => (string)data!,
        RegistryValueKind.DWord => DWordText((uint)data!),
        RegistryValueKind.DWordBigEndian => DWordText((uint)data!),
        RegistryValueKind.QWord => $"0x{(ulong)data!:x16} ({data})",
        RegistryValueKind.MultiString => string.Join("\n", (string[])data!),
        RegistryValueKind.Bool => (bool)data! ? "True" : "False",
        RegistryValueKind.Binary => BytesText((byte[])data!),
        RegistryValueKind.Guid => ((Guid)data!).ToString("B").ToUpperInvariant(),
        RegistryValueKind.Date => ((DateOnly)data!).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        RegistryValueKind.Time => ((TimeOnly)data!).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        RegistryValueKind.DateTime => ((DateTime)data!).ToString("o", CultureInfo.InvariantCulture),
        _ => data is byte[] bytes ? BytesText(bytes) : data?.ToString() ?? "",
    };

    private static string DWordText(uint value) => $"0x{value:x8} ({value})";
    private static string BytesText(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    // The text the edit box starts with: like Format, but always parseable by Parse (a DWORD shows as raw hex
    // without the decimal after it).
    public static string ParseableText(RegistryValueKind kind, object? data) => kind switch
    {
        RegistryValueKind.DWord or RegistryValueKind.DWordBigEndian => $"0x{(uint)data!:x8}",
        RegistryValueKind.QWord => $"0x{(ulong)data!:x16}",
        _ => Format(kind, data),
    };

    // Turns edited text back into data, throwing ArgumentException with a readable message when it will not
    // parse (the dialogs show the message under the box). A RegistryValue keeps the hive from ever storing
    // nonsense: constructing one is the last check.
    public static object? Parse(RegistryValueKind kind, string text)
    {
        try
        {
            var data = ParseText(kind, text);
            new RegistryValue("check", kind, data);   // Normalize: rejects values the kind cannot hold
            return data;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        {
            throw new ArgumentException($"{e.Message}", e);
        }
    }

    private static object? ParseText(RegistryValueKind kind, string text) => kind switch
    {
        RegistryValueKind.None => null,
        RegistryValueKind.String or RegistryValueKind.ExpandString or RegistryValueKind.Link
            or RegistryValueKind.Json or RegistryValueKind.Yaml or RegistryValueKind.Xml => text,
        RegistryValueKind.MultiString => Lines(text),
        RegistryValueKind.Binary or RegistryValueKind.ResourceList or RegistryValueKind.FullResourceDescriptor
            or RegistryValueKind.ResourceRequirementsList => HexBytes(text),
        RegistryValueKind.DWord => (object)Number.UInt(text),
        RegistryValueKind.DWordBigEndian => Number.UInt(text),
        RegistryValueKind.QWord => (object)Number.ULong(text),
        RegistryValueKind.Bool => text.Trim() is "1" ? true : text.Trim() is "0" ? false : bool.Parse(text),
        RegistryValueKind.Int32 => Number.Int(text),
        RegistryValueKind.Int64 => Number.Long(text),
        RegistryValueKind.Float => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
        RegistryValueKind.Double => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
        RegistryValueKind.Decimal => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture),
        RegistryValueKind.Guid => Guid.Parse(text.Trim()),
        RegistryValueKind.Date => DateOnly.Parse(text, CultureInfo.InvariantCulture),
        RegistryValueKind.Time => TimeOnly.ParseExact(text.Trim(), ["HH:mm:ss", "HH:mm"], CultureInfo.InvariantCulture),
        RegistryValueKind.DateTime => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown value kind."),
    };

    private static string[] Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int end = lines.Length;
        while (end > 0 && lines[end - 1].Length == 0) end--;   // trailing blank lines are the editor's, not data
        return lines[..end];
    }

    // Hex lists, the way the .reg editor writes them: pairs like "0A FF" or "0a,ff-0b", spaces and commas free.
    private static byte[] HexBytes(string text)
    {
        var clean = new string(text.Trim().Where(c => !char.IsWhiteSpace(c) && c != ',' && c != '-').ToArray());
        if (clean.Length % 2 != 0)
            throw new FormatException("A binary value is an even number of hex digits.");
        var bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return bytes;
    }

    // Unsigned numbers in decimal or with a 0x prefix, as the DWORD dialog of regedit accepts both.
    private static class Number
    {
        public static uint UInt(string text) => Parse(text, uint.Parse, Convert.ToUInt32);
        public static ulong ULong(string text) => Parse(text, ulong.Parse, Convert.ToUInt64);
        public static int Int(string text) => Parse(text, int.Parse, Convert.ToInt32);
        public static long Long(string text) => Parse(text, long.Parse, Convert.ToInt64);

        private static T Parse<T>(string text, Func<string, NumberStyles, IFormatProvider, T> plain,
            Func<string, int, T> hex) where T : INumber<T>
        {
            var trimmed = text.Trim();
            return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? hex(trimmed[2..], 16)
                : plain(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
    }

    // The data an empty value starts with, per kind.
    public static object? DefaultData(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.None => null,
        RegistryValueKind.String or RegistryValueKind.ExpandString or RegistryValueKind.Link
            or RegistryValueKind.Json or RegistryValueKind.Yaml or RegistryValueKind.Xml => "",
        RegistryValueKind.Binary or RegistryValueKind.ResourceList or RegistryValueKind.FullResourceDescriptor
            or RegistryValueKind.ResourceRequirementsList => Array.Empty<byte>(),
        RegistryValueKind.MultiString => Array.Empty<string>(),
        RegistryValueKind.DWord or RegistryValueKind.DWordBigEndian => 0u,
        RegistryValueKind.QWord => 0ul,
        RegistryValueKind.Bool => false,
        RegistryValueKind.Int32 => 0,
        RegistryValueKind.Int64 => 0L,
        RegistryValueKind.Float => 0f,
        RegistryValueKind.Double => 0d,
        RegistryValueKind.Decimal => 0m,
        RegistryValueKind.Date => DateOnly.MinValue,
        RegistryValueKind.Time => TimeOnly.MinValue,
        RegistryValueKind.DateTime => DateTime.MinValue.ToUniversalTime(),
        RegistryValueKind.Guid => Guid.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown value kind."),
    };

    // ---- keys ----

    public static string? NameProblem(string name)
        => name.Length == 0 ? "The name cannot be empty."
        : name.Contains('\\') ? "The name cannot contain a backslash."
        : name.StartsWith('\\') || name.EndsWith('\\') ? "The name cannot begin or end with a backslash."
        : null;

    public static string ParentOf(string keyPath)
        => keyPath.Contains('\\') ? keyPath[..keyPath.LastIndexOf('\\')] : "";

    // A key is renamed by copying it below the same parent and deleting the original: the registry has no move.
    // Values (and their permissions, where they can be carried over) and sub-keys go to the copy first; the
    // copy is deleted the same way if something refuses partway, which keeps either name holding the whole key.
    public static void Rename(IHive hive, string keyPath, string name)
    {
        if (keyPath.Length == 0) throw new ArgumentException("The root key cannot be renamed.", nameof(keyPath));
        if (NameProblem(name) is { } problem) throw new ArgumentException(problem, nameof(name));
        var parent = ParentOf(keyPath);
        var target = parent.Length == 0 ? name : parent + "\\" + name;
        if (hive.KeyExists(target))
            throw new ArgumentException($"The key '{target}' already exists.");

        try
        {
            Copy(hive, keyPath, target);
            hive.DeleteKey(keyPath);
        }
        catch
        {
            try { hive.DeleteKey(target); } catch (Exception e) when (e is UnauthorizedAccessException or KeyNotFoundException) { }
            throw;
        }
    }

    private static void Copy(IHive hive, string from, string to)
    {
        hive.CreateKey(to);
        foreach (var value in hive.GetValues(from))
        {
            hive.SetValue(to, value.Name, value.Kind, value.Data);
            if (hive.GetValuePermissions(from, value.Name) is { } permissions)
                try { hive.SetValuePermissions(to, value.Name, permissions); }
                catch (UnauthorizedAccessException) { }
        }
        if (hive.GetKeyPermissions(from) is { } keyPerms)
            try { hive.SetKeyPermissions(to, keyPerms); }
            catch (UnauthorizedAccessException) { }
        foreach (var sub in hive.GetSubKeyNames(from))
            Copy(hive, from + "\\" + sub, to + "\\" + sub);
    }

    // ---- .reg import / export ----

    // Saves a key (and everything below it) as a .reg file. keyPath is hive-relative ("" is the whole hive).
    // FromHive puts rootPrefix in front of every section path, so it gets the root name alone: the keyPath it
    // also receives carries the rest.
    public static void Export(IHive hive, string keyPath, string path)
        => RegFile.FromHive(hive, keyPath, RootPrefixOf(hive)).Save(path);

    // Applies a parsed .reg file: sections under HKEY_CURRENT_USER to the user hive, HKEY_LOCAL_MACHINE to the
    // system one, HKEY_CLASSES_ROOT to Software\Classes in the user hive (as ClassesRoot maps it). Sections
    // under other roots are skipped. Returns how many sections were applied.
    public static int Import(IHive user, IHive machine, RegFile file)
    {
        int applied = 0;
        foreach (var (prefix, hive, basePath) in new (string, IHive, string)[]
                 {
                     (UserRoot, user, ""),
                     (MachineRoot, machine, ""),
                     (ClassesRootName, user, ClassesBasePath),
                 })
        {
            // ApplyTo wants the sections already stripped of their HKEY_ root; it shifts them under basePath.
            var group = new RegFile();
            foreach (var section in file.Sections)
                if (TryMatch(section.KeyPath, prefix, out var relative))
                {
                    var copy = new RegSection { KeyPath = relative, DeleteKey = section.DeleteKey };
                    copy.Entries.AddRange(section.Entries);
                    group.Sections.Add(copy);
                    applied++;
                }
            if (group.Sections.Count > 0)
                group.ApplyTo(hive, "", basePath);
        }
        return applied;
    }

    private static string RootPrefixOf(IHive hive) => hive.Name.ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => UserRoot,
        "HKLM" or "HKEY_LOCAL_MACHINE" => MachineRoot,
        "HKCR" or "HKEY_CLASSES_ROOT" => ClassesRootName,
        _ => hive.Name.ToUpperInvariant(),
    };

    private static bool TryMatch(string sectionPath, string prefix, out string relative)
    {
        relative = "";
        if (sectionPath.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            return true;
        if (sectionPath.StartsWith(prefix + @"\", StringComparison.OrdinalIgnoreCase))
        {
            relative = sectionPath[(prefix.Length + 1)..];
            return true;
        }
        return false;
    }
}
