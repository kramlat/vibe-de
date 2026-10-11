using System.Buffers.Binary;
using System.Text;

namespace Vibe.Registry;

// Maps each value kind to its CLR type and to the canonical byte encoding used in hive files.
// All multi-byte numbers are little-endian; text is UTF-8.
internal static class ValueCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static object? Normalize(RegistryValueKind kind, object? data)
    {
        switch (kind)
        {
            case RegistryValueKind.None:
                if (data is not null)
                    throw new ArgumentException("A None value cannot carry data.");
                return null;

            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
            case RegistryValueKind.Link:
            case RegistryValueKind.Json:
            case RegistryValueKind.Yaml:
            case RegistryValueKind.Xml:
                return Expect<string>(kind, data);

            case RegistryValueKind.Binary:
            case RegistryValueKind.ResourceList:
            case RegistryValueKind.FullResourceDescriptor:
            case RegistryValueKind.ResourceRequirementsList:
                return (byte[])Expect<byte[]>(kind, data).Clone();

            case RegistryValueKind.MultiString:
                var items = Expect<string[]>(kind, data);
                foreach (var s in items)
                    if (s.Length == 0 || s.Contains('\0'))
                        throw new ArgumentException("MultiString entries must be non-empty and contain no NUL.");
                return (string[])items.Clone();

            case RegistryValueKind.DWord:
            case RegistryValueKind.DWordBigEndian: return Expect<uint>(kind, data);
            case RegistryValueKind.QWord: return Expect<ulong>(kind, data);
            case RegistryValueKind.Bool: return Expect<bool>(kind, data);
            case RegistryValueKind.Int32: return Expect<int>(kind, data);
            case RegistryValueKind.Int64: return Expect<long>(kind, data);
            case RegistryValueKind.Float: return Expect<float>(kind, data);
            case RegistryValueKind.Double: return Expect<double>(kind, data);
            case RegistryValueKind.Decimal: return Expect<decimal>(kind, data);
            case RegistryValueKind.Date: return Expect<DateOnly>(kind, data);
            case RegistryValueKind.Time: return Expect<TimeOnly>(kind, data);
            case RegistryValueKind.Guid: return Expect<Guid>(kind, data);

            case RegistryValueKind.DateTime:
                var dt = Expect<DateTime>(kind, data);
                if (dt.Kind == DateTimeKind.Local)
                    dt = dt.ToUniversalTime();
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown value kind.");
        }
    }

    public static object? CloneData(object? data) => data switch
    {
        byte[] b => b.Clone(),
        string[] s => s.Clone(),
        _ => data,
    };

    public static byte[] Encode(RegistryValueKind kind, object? data)
    {
        switch (kind)
        {
            case RegistryValueKind.None: return [];

            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
            case RegistryValueKind.Link:
            case RegistryValueKind.Json:
            case RegistryValueKind.Yaml:
            case RegistryValueKind.Xml:
                return Utf8.GetBytes((string)data!);

            case RegistryValueKind.Binary:
            case RegistryValueKind.ResourceList:
            case RegistryValueKind.FullResourceDescriptor:
            case RegistryValueKind.ResourceRequirementsList:
                return (byte[])data!;

            case RegistryValueKind.MultiString:
                var ms = new MemoryStream();
                foreach (var s in (string[])data!)
                {
                    ms.Write(Utf8.GetBytes(s));
                    ms.WriteByte(0);
                }
                ms.WriteByte(0);
                return ms.ToArray();

            case RegistryValueKind.DWord:
            case RegistryValueKind.DWordBigEndian:
                return Bytes(4, b => BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)data!));
            case RegistryValueKind.QWord:
                return Bytes(8, b => BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)data!));
            case RegistryValueKind.Bool: return [(bool)data! ? (byte)1 : (byte)0];
            case RegistryValueKind.Int32:
                return Bytes(4, b => BinaryPrimitives.WriteInt32LittleEndian(b, (int)data!));
            case RegistryValueKind.Int64:
                return Bytes(8, b => BinaryPrimitives.WriteInt64LittleEndian(b, (long)data!));
            case RegistryValueKind.Float:
                return Bytes(4, b => BinaryPrimitives.WriteSingleLittleEndian(b, (float)data!));
            case RegistryValueKind.Double:
                return Bytes(8, b => BinaryPrimitives.WriteDoubleLittleEndian(b, (double)data!));
            case RegistryValueKind.Decimal:
                return Bytes(16, b =>
                {
                    var bits = decimal.GetBits((decimal)data!);
                    for (int i = 0; i < 4; i++)
                        BinaryPrimitives.WriteInt32LittleEndian(b[(i * 4)..], bits[i]);
                });
            case RegistryValueKind.Date:
                return Bytes(4, b => BinaryPrimitives.WriteInt32LittleEndian(b, ((DateOnly)data!).DayNumber));
            case RegistryValueKind.Time:
                return Bytes(8, b => BinaryPrimitives.WriteInt64LittleEndian(b, ((TimeOnly)data!).Ticks));
            case RegistryValueKind.DateTime:
                return Bytes(8, b => BinaryPrimitives.WriteInt64LittleEndian(b, ((DateTime)data!).Ticks));
            case RegistryValueKind.Guid:
                return ((Guid)data!).ToByteArray();

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown value kind.");
        }
    }

    public static object? Decode(RegistryValueKind kind, ReadOnlySpan<byte> bytes)
    {
        switch (kind)
        {
            case RegistryValueKind.None:
                Length(kind, bytes, 0);
                return null;

            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
            case RegistryValueKind.Link:
            case RegistryValueKind.Json:
            case RegistryValueKind.Yaml:
            case RegistryValueKind.Xml:
                return Utf8.GetString(bytes);

            case RegistryValueKind.Binary:
            case RegistryValueKind.ResourceList:
            case RegistryValueKind.FullResourceDescriptor:
            case RegistryValueKind.ResourceRequirementsList:
                return bytes.ToArray();

            case RegistryValueKind.MultiString:
                return DecodeMultiString(bytes);

            case RegistryValueKind.DWord:
            case RegistryValueKind.DWordBigEndian:
                Length(kind, bytes, 4);
                return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            case RegistryValueKind.QWord:
                Length(kind, bytes, 8);
                return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            case RegistryValueKind.Bool:
                Length(kind, bytes, 1);
                return bytes[0] != 0;
            case RegistryValueKind.Int32:
                Length(kind, bytes, 4);
                return BinaryPrimitives.ReadInt32LittleEndian(bytes);
            case RegistryValueKind.Int64:
                Length(kind, bytes, 8);
                return BinaryPrimitives.ReadInt64LittleEndian(bytes);
            case RegistryValueKind.Float:
                Length(kind, bytes, 4);
                return BinaryPrimitives.ReadSingleLittleEndian(bytes);
            case RegistryValueKind.Double:
                Length(kind, bytes, 8);
                return BinaryPrimitives.ReadDoubleLittleEndian(bytes);
            case RegistryValueKind.Decimal:
                Length(kind, bytes, 16);
                var bits = new int[4];
                for (int i = 0; i < 4; i++)
                    bits[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 4)..]);
                return new decimal(bits);
            case RegistryValueKind.Date:
                Length(kind, bytes, 4);
                return DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(bytes));
            case RegistryValueKind.Time:
                Length(kind, bytes, 8);
                return new TimeOnly(BinaryPrimitives.ReadInt64LittleEndian(bytes));
            case RegistryValueKind.DateTime:
                Length(kind, bytes, 8);
                return new DateTime(BinaryPrimitives.ReadInt64LittleEndian(bytes), DateTimeKind.Utc);
            case RegistryValueKind.Guid:
                Length(kind, bytes, 16);
                return new Guid(bytes);

            default:
                throw new InvalidDataException($"Unknown value kind {(ushort)kind}.");
        }
    }

    private static string[] DecodeMultiString(ReadOnlySpan<byte> bytes)
    {
        var list = new List<string>();
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
                continue;
            if (i == start)
                return list.ToArray();   // empty string terminates the list
            list.Add(Utf8.GetString(bytes[start..i]));
            start = i + 1;
        }
        if (start != bytes.Length)
            throw new InvalidDataException("MultiString is not NUL-terminated.");
        return list.ToArray();
    }

    private static T Expect<T>(RegistryValueKind kind, object? data)
    {
        if (data is T value)
            return value;
        throw new ArgumentException($"{kind} requires {typeof(T).Name}, got {data?.GetType().Name ?? "null"}.");
    }

    private static void Length(RegistryValueKind kind, ReadOnlySpan<byte> bytes, int expected)
    {
        if (bytes.Length != expected)
            throw new InvalidDataException($"{kind} data must be {expected} bytes, found {bytes.Length}.");
    }

    private static byte[] Bytes(int size, SpanAction write)
    {
        var buffer = new byte[size];
        write(buffer);
        return buffer;
    }

    private delegate void SpanAction(Span<byte> span);
}
