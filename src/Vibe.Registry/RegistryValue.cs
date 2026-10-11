namespace Vibe.Registry;

// Immutable. Array data is copied on the way in and out.
public sealed class RegistryValue
{
    private readonly object? _data;

    public string Name { get; }
    public RegistryValueKind Kind { get; }
    public object? Data => ValueCodec.CloneData(_data);

    public RegistryValue(string name, RegistryValueKind kind, object? data)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Kind = kind;
        _data = ValueCodec.Normalize(kind, data);
    }

    internal object? RawData => _data;

    internal bool SameAs(RegistryValue other)
        => Kind == other.Kind && ValueCodec.Encode(Kind, _data).AsSpan().SequenceEqual(ValueCodec.Encode(Kind, other._data));

    public override string ToString() => $"{(Name.Length == 0 ? "@" : Name)} ({Kind})";
}
