namespace Vibe.Registry;

// Immutable. Array data is copied on the way in and out.
public sealed class RegistryValue
{
    private readonly object? _data;

    public string Name { get; }
    public RegistryValueKind Kind { get; }
    public object? Data => ValueCodec.CloneData(_data);

    // Owner, group and mode. A value that is not in a hive yet has no real owner: the hive that stores it
    // fills in the identity of whoever stored it.
    public PosixPermissions Permissions { get; }

    public RegistryValue(string name, RegistryValueKind kind, object? data)
        : this(name, kind, data, new PosixPermissions(0, 0, PosixPermissions.ValueDefault))
    {
    }

    public RegistryValue(string name, RegistryValueKind kind, object? data, PosixPermissions permissions)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Kind = kind;
        _data = ValueCodec.Normalize(kind, data);
        Permissions = permissions;
    }

    public RegistryValue WithPermissions(PosixPermissions permissions)
        => new(Name, Kind, _data, permissions);

    internal object? RawData => _data;

    // Same kind and same data. Ownership and mode are compared separately.
    internal bool SameAs(RegistryValue other)
        => Kind == other.Kind && ValueCodec.Encode(Kind, _data).AsSpan().SequenceEqual(ValueCodec.Encode(Kind, other._data));

    public override string ToString() => $"{(Name.Length == 0 ? "@" : Name)} ({Kind})";
}
