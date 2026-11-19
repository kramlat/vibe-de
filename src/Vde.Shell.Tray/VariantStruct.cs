using System.Reflection;
using Tmds.DBus.Protocol;

namespace Vde.Shell.Tray;

// Tmds.DBus.Protocol 0.95 hands variants over as VariantValue, but struct values have no public
// accessor: internally the struct's fields sit in a private object field as a VariantValue array.
// This reads it out with reflection. It is the only place that knows. Falls back to no fields,
// which every caller treats as "the app sent nothing usable".
internal static class VariantStruct
{
    private static FieldInfo? _elements;

    public static VariantValue[] Fields(this VariantValue value)
    {
        if (value.Type != VariantValueType.Struct)
            return [];
        _elements ??= typeof(VariantValue).GetField("_o", BindingFlags.NonPublic | BindingFlags.Instance);
        return _elements?.GetValue(value) is VariantValue[] fields ? fields : [];
    }

    public static VariantValue[] Array(this VariantValue value)
        => value.Type == VariantValueType.Array ? value.GetArray<VariantValue>() : [];

    public static Dictionary<string, VariantValue> Dictionary(this VariantValue value)
        => value.Type == VariantValueType.Dictionary ? value.GetDictionary<string, VariantValue>() : [];

    public static string String(this VariantValue value)
        => value.Type == VariantValueType.String ? value.GetString() : "";

    // Object paths are stored as plain strings internally.
    // GetString() throws InvalidOperationException for ObjectPath values; go through the reader.
    private static readonly System.Reflection.MethodInfo? GetObjectPath =
        typeof(VariantValue).GetMethod("UnsafeGetObjectPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    public static string Path(this VariantValue value)
    {
        if (value.Type != VariantValueType.ObjectPath) return "";
        if (GetObjectPath?.Invoke(value, null) is ObjectPath p) return p;
        if (value.GetString() is string s) return s;
        return "";
    }

    public static int Int32(this VariantValue value)
        => value.Type == VariantValueType.Int32 ? value.GetInt32() : 0;

    public static byte[] Bytes(this VariantValue value)
        => value.Type == VariantValueType.Array ? value.GetArray<byte>() : [];}
