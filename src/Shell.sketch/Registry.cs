public sealed class Fhive
{
    public string Name { get; }          // "HKLM", "HKCU", "Fluent.System", etc.
    public string? FilePath { get; }     // backing file, if any
    public FhiveKey Root { get; }

    public Fhive(string name, string? filePath = null)
    {
        Name = name;
        FilePath = filePath;
        Root = new FhiveKey(this, null, string.Empty);
    }
}

public sealed class FhiveKey
{
    public Fhive Hive { get; }
    public FhiveKey? Parent { get; }
    public string Name { get; }
    public string FullPath => Parent == null || Parent.Name == string.Empty
        ? Name
        : Parent.FullPath + "\\" + Name;

    public Dictionary<string,FhiveKey> SubKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string,FhiveValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal FhiveKey(Fhive hive, FhiveKey? parent, string name)
    {
        Hive = hive;
        Parent = parent;
        Name = name;
    }
}

public sealed class FhiveValue
{
    public string Name { get; }                  // "" = default
    public FhiveValueType Type { get; }
    public object? Data { get; }                 // Variant wraps inner type

    public FhiveValue(string name, FhiveValueType type, object? data)
    {
        Name = name;
        Type = type;
        Data = data;
    }
}

public sealed class FhiveStore
{
    private readonly Dictionary<string,Fhive> _hives = new(StringComparer.OrdinalIgnoreCase);

    public Fhive this[string name] => _hives[name];

    public IEnumerable<Fhive> Hives => _hives.Values;

    public Fhive CreateHive(string name, string? filePath = null)
    {
        var hive = new Fhive(name, filePath);
        _hives[name] = hive;
        return hive;
    }

    public bool TryGetHive(string name, out Fhive? hive)
        => _hives.TryGetValue(name, out hive);
}

public sealed class FluentRegistry
{
    public FhiveStore Store { get; } = new();

    // Example: create standard hives
    public void InitializeDefaultHives(string systemPath, string userPath)
    {
        Store.CreateHive("HKLM", systemPath);
        Store.CreateHive("HKCU", userPath);
        Store.CreateHive("HKCR", null);          // overlay view later
        Store.CreateHive("Fluent.System", null);
        Store.CreateHive("Fluent.User", null);
    }

    // Import a .reg file into a target hive/path
    public void ImportRegFile(string regPath, string hiveName, string baseKeyPath = "")
    {
        var regFile = RegFileReader.Load(regPath);

        if (!Store.TryGetHive(hiveName, out var hive))
            throw new InvalidOperationException($"Hive '{hiveName}' not found.");

        foreach (var section in regFile.Sections)
        {
            var fullPath = CombineKeyPaths(baseKeyPath, section.KeyPath);
            var key = EnsureKey(hive, fullPath);

            foreach (var value in section.Values)
            {
                var fhiveValue = MapRegValueToFhiveValue(value);
                key.Values[fhiveValue.Name] = fhiveValue;
            }
        }
    }

    // Export a subtree to .reg v6
    public void ExportRegFile(string hiveName, string keyPath, string outputPath)
    {
        if (!Store.TryGetHive(hiveName, out var hive))
            throw new InvalidOperationException($"Hive '{hiveName}' not found.");

        var rootKey = GetKey(hive, keyPath)
            ?? throw new InvalidOperationException($"Key '{keyPath}' not found in hive '{hiveName}'.");

        var regFile = BuildRegFileFromKey(rootKey);
        RegFileWriter.Save(regFile, outputPath);
    }
}

private static FhiveValue MapRegValueToFhiveValue(RegValueEntry entry)
{
    var name = entry.Name; // "" = default

    return entry.Type switch
    {
        RegValueType.String      => new FhiveValue(name, FhiveValueType.String, (string?)entry.Data),
        RegValueType.ExpandString=> new FhiveValue(name, FhiveValueType.ExpandString, (string?)entry.Data),
        RegValueType.MultiString => new FhiveValue(name, FhiveValueType.MultiSz, (string[]?)entry.Data),
        RegValueType.DWord       => new FhiveValue(name, FhiveValueType.DWord, (uint?)entry.Data),
        RegValueType.QWord       => new FhiveValue(name, FhiveValueType.QWord, (ulong?)entry.Data),
        RegValueType.Binary      => new FhiveValue(name, FhiveValueType.Binary, (byte[]?)entry.Data),

        // Fluent types
        RegValueType.Bool        => new FhiveValue(name, FhiveValueType.Bool, (bool?)entry.Data),
        RegValueType.Int32       => new FhiveValue(name, FhiveValueType.Int32, (int?)entry.Data),
        RegValueType.Int64       => new FhiveValue(name, FhiveValueType.Int64, (long?)entry.Data),
        RegValueType.Float       => new FhiveValue(name, FhiveValueType.Float, (float?)entry.Data),
        RegValueType.Double      => new FhiveValue(name, FhiveValueType.Double, (double?)entry.Data),
        RegValueType.Decimal     => new FhiveValue(name, FhiveValueType.Decimal, (decimal?)entry.Data),
        RegValueType.Date        => new FhiveValue(name, FhiveValueType.Date, (DateOnly?)entry.Data),
        RegValueType.Time        => new FhiveValue(name, FhiveValueType.Time, (TimeOnly?)entry.Data),
        RegValueType.DateTime    => new FhiveValue(name, FhiveValueType.DateTime, (DateTime?)entry.Data),
        RegValueType.Guid        => new FhiveValue(name, FhiveValueType.Guid, (Guid?)entry.Data),
        RegValueType.Json        => new FhiveValue(name, FhiveValueType.Json, (string?)entry.Data),
        RegValueType.Yaml        => new FhiveValue(name, FhiveValueType.Yaml, (string?)entry.Data),
        RegValueType.Xml         => new FhiveValue(name, FhiveValueType.XML, (string?)entry.Data),
        RegValueType.Metadata    => new FhiveValue(name, FhiveValueType.KeyMetadata, (string?)entry.Data),

        RegValueType.Variant     => MapVariant(entry),

        _                        => new FhiveValue(name, FhiveValueType.None, null),
    };
}

private static FhiveValue MapVariant(RegValueEntry entry)
{
    var variant = (VariantValue)entry.Data!;
    // Default value as Variant is already encoded at Reg level;
    // here we just mirror it into Fhive.
    return new FhiveValue(entry.Name, FhiveValueType.Variant, variant);
}

private static RegFile BuildRegFileFromKey(FhiveKey root)
{
    var file = new RegFile();
    BuildRegSectionsRecursive(root, file);
    return file;
}

private static void BuildRegSectionsRecursive(FhiveKey key, RegFile file)
{
    var section = new RegKeySection(key.FullPath);
    foreach (var value in key.Values.Values)
        section.Values.Add(MapFhiveValueToRegValue(value));

    file.Sections.Add(section);

    foreach (var child in key.SubKeys.Values)
        BuildRegSectionsRecursive(child, file);
}

private static RegValueEntry MapFhiveValueToRegValue(FhiveValue value)
{
    // Default value: if Type != Variant, you *could* wrap it as Variant
    // depending on how strict you want to be. For now, mirror type.
    return value.Type switch
    {
        FhiveValueType.String       => new RegValueEntry(value.Name, RegValueType.String, value.Data),
        FhiveValueType.ExpandString => new RegValueEntry(value.Name, RegValueType.ExpandString, value.Data),
        FhiveValueType.MultiSz      => new RegValueEntry(value.Name, RegValueType.MultiString, value.Data),
        FhiveValueType.DWord        => new RegValueEntry(value.Name, RegValueType.DWord, value.Data),
        FhiveValueType.QWord        => new RegValueEntry(value.Name, RegValueType.QWord, value.Data),
        FhiveValueType.Binary       => new RegValueEntry(value.Name, RegValueType.Binary, value.Data),

        FhiveValueType.Bool         => new RegValueEntry(value.Name, RegValueType.Bool, value.Data),
        FhiveValueType.Int32        => new RegValueEntry(value.Name, RegValueType.Int32, value.Data),
        FhiveValueType.Int64        => new RegValueEntry(value.Name, RegValueType.Int64, value.Data),
        FhiveValueType.Float        => new RegValueEntry(value.Name, RegValueType.Float, value.Data),
        FhiveValueType.Double       => new RegValueEntry(value.Name, RegValueType.Double, value.Data),
        FhiveValueType.Decimal      => new RegValueEntry(value.Name, RegValueType.Decimal, value.Data),
        FhiveValueType.Date         => new RegValueEntry(value.Name, RegValueType.Date, value.Data),
        FhiveValueType.Time         => new RegValueEntry(value.Name, RegValueType.Time, value.Data),
        FhiveValueType.DateTime     => new RegValueEntry(value.Name, RegValueType.DateTime, value.Data),
        FhiveValueType.Guid         => new RegValueEntry(value.Name, RegValueType.Guid, value.Data),
        FhiveValueType.Json         => new RegValueEntry(value.Name, RegValueType.Json, value.Data),
        FhiveValueType.YAML         => new RegValueEntry(value.Name, RegValueType.Yaml, value.Data),
        FhiveValueType.XML          => new RegValueEntry(value.Name, RegValueType.Xml, value.Data),
        FhiveValueType.KeyMetadata  => new RegValueEntry(value.Name, RegValueType.Metadata, value.Data),

        FhiveValueType.Variant      => new RegValueEntry(value.Name, RegValueType.Variant, value.Data),

        _                           => new RegValueEntry(value.Name, RegValueType.None, null),
    };
}

public sealed class FhiveView
{
    public string Name { get; }   // "HKCR", "Fluent.Effective.Policy"
    public IReadOnlyList<FhiveLayer> Layers { get; }

    public FhiveView(string name, IEnumerable<FhiveLayer> layers)
    {
        Name = name;
        Layers = layers.ToList().AsReadOnly();
    }
}

public sealed class FhiveLayer
{
    public Fhive Hive { get; }
    public string BasePath { get; }   // e.g. "Software\\Classes"
    public bool IsWritable { get; }

    public FhiveLayer(Fhive hive, string basePath, bool isWritable)
    {
        Hive = hive;
        BasePath = basePath ?? string.Empty;
        IsWritable = isWritable;
    }
}

public sealed class FluentRegistry
{
    public FhiveStore Store { get; } = new();
    private readonly Dictionary<string,FhiveView> _views = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterView(FhiveView view)
        => _views[view.Name] = view;

    public bool TryGetView(string name, out FhiveView? view)
        => _views.TryGetValue(name, out view);
}

public void InitializeDefaultHivesAndViews(string systemPath, string userPath)
{
    var hklm = Store.CreateHive("HKLM", systemPath);
    var hkcu = Store.CreateHive("HKCU", userPath);

    // Physical-only for now:
    Store.CreateHive("HKCR", null); // optional, or omit entirely

    // HKCR view: HKLM\Software\Classes (base) + HKCU\Software\Classes (override)
    var hkcrView = new FhiveView(
        "HKCR",
        new[]
        {
            new FhiveLayer(hklm, "Software\\Classes", isWritable: false),
            new FhiveLayer(hkcu, "Software\\Classes", isWritable: true),
        });

    RegisterView(hkcrView);
}

public FhiveKey? ResolveViewKey(FhiveView view, string relativePath)
{
    FhiveKey? merged = null;

    foreach (var layer in view.Layers)
    {
        var key = GetKey(layer.Hive, CombineKeyPaths(layer.BasePath, relativePath));
        if (key == null)
            continue;

        merged = merged == null
            ? CloneKeyShallow(key)
            : OverlayKey(merged, key);
    }

    return merged;
}

public void SetViewValue(FhiveView view, string relativeKeyPath, FhiveValue value)
{
    foreach (var layer in view.Layers)
    {
        if (!layer.IsWritable)
            continue;

        var key = EnsureKey(layer.Hive, CombineKeyPaths(layer.BasePath, relativeKeyPath));
        key.Values[value.Name] = value;
        return;
    }

    throw new InvalidOperationException($"View '{view.Name}' has no writable layer for '{relativeKeyPath}'.");
}

