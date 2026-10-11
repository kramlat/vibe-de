public enum FhiveEndian : byte
{
    Little = 0,
    Big = 1
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FhiveHeader
{
    public uint Magic;          // 'FHIV'
    public ushort VersionMajor; // e.g. 1
    public ushort VersionMinor; // e.g. 0
    public FhiveEndian Endian;
    public byte Reserved0;
    public uint KeyTableOffset;
    public uint KeyCount;
    public uint ValueTableOffset;
    public uint ValueCount;
    public uint StringPoolOffset;
    public uint StringPoolLength;
    public uint BlobPoolOffset;
    public uint BlobPoolLength;
    public uint RootKeyId;      // index into key table
}

[Flags]
public enum FhiveKeyFlags : uint
{
    None        = 0,
    System      = 1 << 0,
    Hidden      = 1 << 1,
    Volatile    = 1 << 2,
    Policy      = 1 << 3,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FhiveKeyRecord
{
    public uint Id;                 // index
    public uint ParentId;           // 0xFFFFFFFF for root
    public uint NameOffset;         // into StringPool
    public uint SecurityDescriptor; // future use / ACL ref
    public FhiveKeyFlags Flags;
    public long CreatedUtcTicks;
    public long ModifiedUtcTicks;
    public uint FirstValueIndex;    // index into value table
    public uint ValueCount;
    public uint FirstChildIndex;    // optional: for pre-sorted children
    public uint ChildCount;
}

public enum FhiveValueType : ushort
{
    // -------------------------
    // Windows-compatible types
    // -------------------------
    None                = 0,
    String              = 1,   // REG_SZ
    ExpandString        = 2,   // REG_EXPAND_SZ
    Binary              = 3,   // REG_BINARY
    DWord               = 4,   // REG_DWORD
    DWordBigEndian      = 5,   // REG_DWORD_BIG_ENDIAN
    Link                = 6,   // REG_LINK
    MultiSz             = 7,   // REG_MULTI_SZ
    ResourceList        = 8,   // REG_RESOURCE_LIST
    FullResourceDesc    = 9,   // REG_FULL_RESOURCE_DESCRIPTOR
    ResourceReqList     = 10,  // REG_RESOURCE_REQUIREMENTS_LIST
    QWord               = 11,  // REG_QWORD

    // -------------------------
    // Fluent SQL-inspired types
    // -------------------------
    Bool                = 100,   // REG_BOOL
    Int32               = 101,   // REG_INT32
    Int64               = 102,   // REG_INT64
    Float               = 103,   // REG_FLOAT
    Double              = 104,   // REG_DOUBLE
    Decimal             = 105,   // REG_DECIMAL
    Date                = 106,   // REG_DATE
    Time                = 107,   // REG_TIME
    DateTime            = 108,   // REG_DATETIME
    Guid                = 109,   // REG_GUID
    Json                = 110,   // REG_JSON
    YAML                = 111,   // REG_YAML
    XML                 = 112,   // REG_XML
    Variant             = 113,   // REG_VARIANT

    // -------------------------
    // Fluent metadata types
    // -------------------------
    KeyMetadata         = 150,   // REG_KEYMETA (JSON-encoded metadata)
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FhiveValueRecord
{
    public uint KeyId;          // owning key
    public uint NameOffset;     // into StringPool (0 for default)
    public FhiveValueType Type;
    public ushort Reserved0;
    public uint DataOffset;     // into StringPool or BlobPool
    public uint DataLength;
}

public sealed class FhiveKey
{
    public uint Id { get; }
    public string Name { get; }
    public FhiveKey? Parent { get; }
    public IReadOnlyList<FhiveKey> Children => _children;
    public IReadOnlyList<FhiveValue> Values => _values;

    private readonly List<FhiveKey> _children = new();
    private readonly List<FhiveValue> _values = new();

    internal FhiveKey(uint id, string name, FhiveKey? parent)
    {
        Id = id;
        Name = name;
        Parent = parent;
    }
}

public sealed class FhiveValue
{
    public string Name { get; }
    public FhiveValueType Type { get; }
    public object? Value { get; }

    internal FhiveValue(string name, FhiveValueType type, object? value)
    {
        Name = name;
        Type = type;
        Value = value;
    }
}

public sealed class FhiveReader
{
    private readonly Stream _stream;
    private readonly BinaryReader _reader;
    private FhiveHeader _header = default!;
    private byte[] _stringPool = Array.Empty<byte>();
    private byte[] _blobPool = Array.Empty<byte>();
    private FhiveKeyRecord[] _keys = Array.Empty<FhiveKeyRecord>();
    private FhiveValueRecord[] _values = Array.Empty<FhiveValueRecord>();

    public FhiveReader(Stream stream)
    {
        _stream = stream;
        _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
    }

    public FhiveKey LoadRoot()
    {
        ReadHeader();
        ReadTables();
        return BuildTree(_header.RootKeyId);
    }

    private void ReadHeader()
    {
        _stream.Seek(0, SeekOrigin.Begin);
        _header = ReadStruct<FhiveHeader>();

        if (_header.Magic != 0x46484956) // 'FHIV'
            throw new InvalidDataException("Not an fhive file.");
    }

    private void ReadTables()
    {
        // Keys
        _stream.Seek(_header.KeyTableOffset, SeekOrigin.Begin);
        _keys = new FhiveKeyRecord[_header.KeyCount];
        for (int i = 0; i < _keys.Length; i++)
            _keys[i] = ReadStruct<FhiveKeyRecord>();

        // Values
        _stream.Seek(_header.ValueTableOffset, SeekOrigin.Begin);
        _values = new FhiveValueRecord[_header.ValueCount];
        for (int i = 0; i < _values.Length; i++)
            _values[i] = ReadStruct<FhiveValueRecord>();

        // String pool
        _stream.Seek(_header.StringPoolOffset, SeekOrigin.Begin);
        _stringPool = _reader.ReadBytes((int)_header.StringPoolLength);

        // Blob pool
        _stream.Seek(_header.BlobPoolOffset, SeekOrigin.Begin);
        _blobPool = _reader.ReadBytes((int)_header.BlobPoolLength);
    }

    private FhiveKey BuildTree(uint rootId)
    {
        var keyMap = new Dictionary<uint, FhiveKey>(_keys.Length);

        // First pass: create all keys
        foreach (var rec in _keys)
        {
            string name = ReadString(rec.NameOffset);
            keyMap[rec.Id] = new FhiveKey(rec.Id, name, parent: null);
        }

        // Second pass: wire parents/children
        foreach (var rec in _keys)
        {
            if (rec.ParentId != 0xFFFFFFFF && keyMap.TryGetValue(rec.ParentId, out var parent))
            {
                var child = keyMap[rec.Id];
                typeof(FhiveKey)
                    .GetField("_children", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(parent) is List<FhiveKey> list
                    && list.Add(child);
            }
        }

        // Values
        foreach (var vrec in _values)
        {
            if (!keyMap.TryGetValue(vrec.KeyId, out var key))
                continue;

            string name = vrec.NameOffset == 0 ? string.Empty : ReadString(vrec.NameOffset);
            object? value = ReadValueData(vrec);
            typeof(FhiveKey)
                .GetField("_values", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(key) is List<FhiveValue> vlist
                && vlist.Add(new FhiveValue(name, vrec.Type, value));
        }

        return keyMap[rootId];
    }

    private string ReadString(uint offset)
    {
        int off = checked((int)offset);
        int len = 0;
        while (off + len < _stringPool.Length && _stringPool[off + len] != 0)
            len++;

        return Encoding.UTF8.GetString(_stringPool, off, len);
    }

    private object? ReadValueData(FhiveValueRecord vrec)
    {
        int off = checked((int)vrec.DataOffset);
        int len = checked((int)vrec.DataLength);

        return vrec.Type switch
        {
            FhiveValueType.String => Encoding.UTF8.GetString(_stringPool, off, len),
            FhiveValueType.DWord  => BitConverter.ToUInt32(_blobPool, off),
            FhiveValueType.QWord  => BitConverter.ToUInt64(_blobPool, off),
            FhiveValueType.Binary => _blobPool.AsSpan(off, len).ToArray(),
            FhiveValueType.Json   => Encoding.UTF8.GetString(_blobPool, off, len),
            FhiveValueType.MultiSz => ReadMultiSz(off, len),
            _ => null
        };
    }

    private string[] ReadMultiSz(int off, int len)
    {
        var slice = _stringPool.AsSpan(off, len);
        var list = new List<string>();
        int start = 0;
        for (int i = 0; i < slice.Length; i++)
        {
            if (slice[i] == 0)
            {
                if (i == start) break; // double null
                list.Add(Encoding.UTF8.GetString(slice.Slice(start, i - start)));
                start = i + 1;
            }
        }
        return list.ToArray();
    }

    private static T ReadStruct<T>() where T : struct
    {
        int size = Marshal.SizeOf<T>();
        Span<byte> buffer = stackalloc byte[size];
        if (Console.IsOutputRedirected) { } // just to avoid warnings; ignore
        // In real code: read fully, handle endianness if needed
        throw new NotImplementedException();
    }
}

public sealed class FhiveWriter
{
    private readonly Stream _stream;
    private readonly BinaryWriter _writer;

    public FhiveWriter(Stream stream)
    {
        _stream = stream;
        _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
    }

    public void Write(FhiveKey root)
    {
        // 1. Flatten keys/values
        var keys = new List<FhiveKeyRecord>();
        var values = new List<FhiveValueRecord>();
        var stringPool = new StringPoolBuilder();
        var blobPool = new BlobPoolBuilder();

        FlattenTree(root, parentId: 0xFFFFFFFF, keys, values, stringPool, blobPool);

        // 2. Compute offsets
        var header = new FhiveHeader
        {
            Magic = 0x46484956,
            VersionMajor = 1,
            VersionMinor = 0,
            Endian = FhiveEndian.Little,
            KeyTableOffset = (uint)Marshal.SizeOf<FhiveHeader>(),
            KeyCount = (uint)keys.Count,
        };

        header.ValueTableOffset = header.KeyTableOffset + (uint)(keys.Count * Marshal.SizeOf<FhiveKeyRecord>());
        header.ValueCount = (uint)values.Count;

        header.StringPoolOffset = header.ValueTableOffset + (uint)(values.Count * Marshal.SizeOf<FhiveValueRecord>());
        var stringBytes = stringPool.Build();
        header.StringPoolLength = (uint)stringBytes.Length;

        header.BlobPoolOffset = header.StringPoolOffset + header.StringPoolLength;
        var blobBytes = blobPool.Build();
        header.BlobPoolLength = (uint)blobBytes.Length;

        header.RootKeyId = root.Id;

        // 3. Write everything
        _stream.Seek(0, SeekOrigin.Begin);
        WriteStruct(header);

        foreach (var k in keys)
            WriteStruct(k);

        foreach (var v in values)
            WriteStruct(v);

        _writer.Write(stringBytes);
        _writer.Write(blobBytes);
        _writer.Flush();
        _stream.Flush();
    }

    private void FlattenTree(
        FhiveKey key,
        uint parentId,
        List<FhiveKeyRecord> keys,
        List<FhiveValueRecord> values,
        StringPoolBuilder strings,
        BlobPoolBuilder blobs)
    {
        uint id = (uint)keys.Count;
        // In real code, we’d assign Ids deterministically and store them back into FhiveKey
        var rec = new FhiveKeyRecord
        {
            Id = id,
            ParentId = parentId,
            NameOffset = strings.Intern(key.Name),
            // timestamps, flags, etc.
        };
        keys.Add(rec);

        // Values
        foreach (var v in key.Values)
        {
            var vrec = new FhiveValueRecord
            {
                KeyId = id,
                NameOffset = string.IsNullOrEmpty(v.Name) ? 0u : strings.Intern(v.Name),
                Type = v.Type,
            };

            switch (v.Type)
            {
                case FhiveValueType.String:
                    {
                        var bytes = Encoding.UTF8.GetBytes((string)v.Value!);
                        vrec.DataOffset = blobs.Append(bytes);
                        vrec.DataLength = (uint)bytes.Length;
                        break;
                    }
                // handle other types similarly
            }

            values.Add(vrec);
        }

        // Children
        foreach (var child in key.Children)
            FlattenTree(child, id, keys, values, strings, blobs);
    }

    private void WriteStruct<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        Span<byte> buffer = stackalloc byte[size];
        // In real code: marshal struct to bytes, handle endianness
        throw new NotImplementedException();
    }
}

public sealed class StringPoolBuilder
{
    private readonly Dictionary<string, uint> _offsets = new();
    private readonly MemoryStream _ms = new();

    public uint Intern(string s)
    {
        if (_offsets.TryGetValue(s, out var off))
            return off;

        off = (uint)_ms.Position;
        var bytes = Encoding.UTF8.GetBytes(s);
        _ms.Write(bytes, 0, bytes.Length);
        _ms.WriteByte(0); // null terminator
        _offsets[s] = off;
        return off;
    }

    public byte[] Build() => _ms.ToArray();
}

public sealed class BlobPoolBuilder
{
    private readonly MemoryStream _ms = new();

    public uint Append(byte[] data)
    {
        uint off = (uint)_ms.Position;
        _ms.Write(data, 0, data.Length);
        return off;
    }

    public byte[] Build() => _ms.ToArray();
}

public interface IFhiveStore
{
    FhiveKey Root { get; }

    FhiveKey? OpenKey(string path);
    FhiveKey CreateKey(string path);
    void DeleteKey(string path);

    FhiveValue? GetValue(string keyPath, string valueName);
    void SetValue(string keyPath, string valueName, FhiveValueType type, object? value);
    void DeleteValue(string keyPath, string valueName);

    void Save();
}
