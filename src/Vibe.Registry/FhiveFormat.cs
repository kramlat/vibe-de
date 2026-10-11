using System.Buffers.Binary;
using System.Text;

namespace Vibe.Registry;

// Fhive v1.1 on-disk layout (all little-endian; 1.0, which had no permissions, was never released):
//
//   header (64 bytes)
//   key table      44 bytes per key, in pre-order (a parent always precedes its children)
//   value table    28 bytes per value
//   string pool    NUL-terminated UTF-8 names; offset 0 is the empty string
//   blob pool      value data, addressed by (offset, length)
//
// Header: "FHIV", u16 major, u16 minor, u32 flags, u32 keyCount, u32 valueCount,
//   u32 keyTableOffset, u32 valueTableOffset, u32 stringPoolOffset, u32 stringPoolLength,
//   u32 blobPoolOffset, u32 blobPoolLength, u32 rootKeyId, u32 crc32 (of all bytes after the header).
// Key:   u32 id, u32 parentId (0xFFFFFFFF for root), u32 nameOffset, u32 flags,
//        i64 createdUtcTicks, i64 modifiedUtcTicks, u32 uid, u32 gid, u16 mode (rwxrwxrwx), u16 reserved
// Value: u32 keyId, u32 nameOffset, u16 kind, u16 mode (rwxrwxrwx), u32 dataOffset, u32 dataLength,
//        u32 uid, u32 gid
internal static class FhiveFormat
{
    private const int HeaderSize = 64;
    private const int KeyRecordSize = 44;
    private const int ValueRecordSize = 28;
    private const uint NoParent = 0xFFFFFFFF;
    private const ushort VersionMajor = 1;
    private const ushort VersionMinor = 1;
    private static readonly byte[] Magic = "FHIV"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static void Write(Node root, Stream output)
    {
        var strings = new MemoryStream();
        strings.WriteByte(0);
        var stringOffsets = new Dictionary<string, uint> { [""] = 0 };
        var blobs = new MemoryStream();
        var keyTable = new MemoryStream();
        var valueTable = new MemoryStream();
        var keyWriter = new BinaryWriter(keyTable);
        var valueWriter = new BinaryWriter(valueTable);
        uint keyCount = 0, valueCount = 0;

        uint Intern(string s)
        {
            if (stringOffsets.TryGetValue(s, out var offset))
                return offset;
            offset = (uint)strings.Position;
            strings.Write(Utf8.GetBytes(s));
            strings.WriteByte(0);
            stringOffsets[s] = offset;
            return offset;
        }

        void Emit(Node node, uint parentId)
        {
            uint id = keyCount++;
            keyWriter.Write(id);
            keyWriter.Write(parentId);
            keyWriter.Write(Intern(node.Name));
            keyWriter.Write((uint)node.Flags);
            keyWriter.Write(node.Created.Ticks);
            keyWriter.Write(node.Modified.Ticks);
            keyWriter.Write(node.Perms.Uid);
            keyWriter.Write(node.Perms.Gid);
            keyWriter.Write(node.Perms.Mode);
            keyWriter.Write((ushort)0);

            foreach (var value in node.Values.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                var data = ValueCodec.Encode(value.Kind, value.RawData);
                valueWriter.Write(id);
                valueWriter.Write(Intern(value.Name));
                valueWriter.Write((ushort)value.Kind);
                valueWriter.Write(value.Permissions.Mode);
                valueWriter.Write((uint)blobs.Position);
                valueWriter.Write((uint)data.Length);
                valueWriter.Write(value.Permissions.Uid);
                valueWriter.Write(value.Permissions.Gid);
                blobs.Write(data);
                valueCount++;
            }

            foreach (var child in node.Children.Values
                         .Where(c => (c.Flags & RegistryKeyFlags.Volatile) == 0)
                         .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                Emit(child, id);
        }

        Emit(root, NoParent);

        uint keyOffset = HeaderSize;
        uint valueOffset = keyOffset + (uint)keyTable.Length;
        uint stringOffset = valueOffset + (uint)valueTable.Length;
        uint blobOffset = stringOffset + (uint)strings.Length;

        var body = new MemoryStream();
        keyTable.WriteTo(body);
        valueTable.WriteTo(body);
        strings.WriteTo(body);
        blobs.WriteTo(body);
        var bodyBytes = body.ToArray();

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), VersionMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), VersionMinor);
        // 8: flags (0)
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), keyCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), valueCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), keyOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), valueOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), stringOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), (uint)strings.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), blobOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)blobs.Length);
        // 44: rootKeyId (0)
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), Crc32(bodyBytes));

        output.Write(header);
        output.Write(bodyBytes);
        output.Flush();
    }

    public static Node Read(Stream input)
    {
        var ms = new MemoryStream();
        input.CopyTo(ms);
        var data = ms.ToArray();

        try
        {
            return Parse(data);
        }
        catch (ArgumentException e)
        {
            // Bad UTF-8 or out-of-range timestamps surface as ArgumentException from the BCL.
            throw new InvalidDataException("Corrupt hive: " + e.Message, e);
        }
    }

    private static Node Parse(byte[] data)
    {
        if (data.Length < HeaderSize || !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Not an fhive file.");

        var major = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        if (major != VersionMajor || minor < VersionMinor)
            throw new InvalidDataException($"Unsupported fhive version {major}.{minor}.");

        uint keyCount = U32(data, 12), valueCount = U32(data, 16);
        uint keyOffset = U32(data, 20), valueOffset = U32(data, 24);
        uint stringOffset = U32(data, 28), stringLength = U32(data, 32);
        uint blobOffset = U32(data, 36), blobLength = U32(data, 40);
        uint rootId = U32(data, 44), crc = U32(data, 48);

        var keyBytes = Region(data, keyOffset, (long)keyCount * KeyRecordSize, "key table");
        var valueBytes = Region(data, valueOffset, (long)valueCount * ValueRecordSize, "value table");
        var stringBytes = Region(data, stringOffset, stringLength, "string pool");
        var blobBytes = Region(data, blobOffset, blobLength, "blob pool");

        if (crc != Crc32(data.AsSpan(HeaderSize)))
            throw new InvalidDataException("Hive checksum mismatch.");
        if (keyCount == 0 || rootId != 0)
            throw new InvalidDataException("Hive has no root key.");
        if (stringBytes.Length == 0 || stringBytes[0] != 0)
            throw new InvalidDataException("String pool must begin with the empty string.");

        var nodes = new Node[keyCount];
        for (int i = 0; i < nodes.Length; i++)
        {
            var rec = keyBytes.Slice(i * KeyRecordSize, KeyRecordSize);
            uint id = U32(rec, 0), parentId = U32(rec, 4);
            if (id != i)
                throw new InvalidDataException("Key ids must be sequential.");

            string name = ReadString(stringBytes, U32(rec, 8));
            var keyMode = BinaryPrimitives.ReadUInt16LittleEndian(rec[40..]);
            if ((keyMode & ~PosixPermissions.Mask) != 0)
                throw new InvalidDataException("Invalid key permissions.");
            Node? parent = null;
            if (i == 0)
            {
                if (parentId != NoParent || name.Length != 0)
                    throw new InvalidDataException("Invalid root key.");
            }
            else
            {
                if (parentId >= i)
                    throw new InvalidDataException("A key's parent must precede it.");
                if (name.Length == 0 || name.Contains('\\'))
                    throw new InvalidDataException("Invalid key name.");
                parent = nodes[parentId];
            }

            var node = new Node(name, parent)
            {
                Flags = (RegistryKeyFlags)U32(rec, 12),
                Created = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(rec[16..]), DateTimeKind.Utc),
                Modified = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(rec[24..]), DateTimeKind.Utc),
                Perms = new PosixPermissions(U32(rec, 32), U32(rec, 36), keyMode),
            };
            if (parent is not null && !parent.Children.TryAdd(name, node))
                throw new InvalidDataException($"Duplicate key '{name}'.");
            nodes[i] = node;
        }

        for (int i = 0; i < valueCount; i++)
        {
            var rec = valueBytes.Slice(i * ValueRecordSize, ValueRecordSize);
            uint keyId = U32(rec, 0);
            if (keyId >= nodes.Length)
                throw new InvalidDataException("Value refers to a missing key.");

            var kind = (RegistryValueKind)BinaryPrimitives.ReadUInt16LittleEndian(rec[8..]);
            if (!Enum.IsDefined(kind))
                throw new InvalidDataException($"Unknown value kind {(ushort)kind}.");

            uint dataOffset = U32(rec, 12), dataLength = U32(rec, 16);
            if ((long)dataOffset + dataLength > blobBytes.Length)
                throw new InvalidDataException("Value data lies outside the blob pool.");

            string name = ReadString(stringBytes, U32(rec, 4));
            var valueMode = BinaryPrimitives.ReadUInt16LittleEndian(rec[10..]);
            if ((valueMode & ~PosixPermissions.Mask) != 0)
                throw new InvalidDataException("Invalid value permissions.");
            var decoded = ValueCodec.Decode(kind, blobBytes.Slice((int)dataOffset, (int)dataLength));
            var permissions = new PosixPermissions(U32(rec, 20), U32(rec, 24), valueMode);
            if (!nodes[keyId].Values.TryAdd(name, new RegistryValue(name, kind, decoded, permissions)))
                throw new InvalidDataException($"Duplicate value '{name}'.");
        }

        return nodes[0];
    }

    private static ReadOnlySpan<byte> Region(byte[] data, uint offset, long length, string what)
    {
        if (offset < HeaderSize || offset + length > data.Length)
            throw new InvalidDataException($"The {what} lies outside the file.");
        return data.AsSpan((int)offset, (int)length);
    }

    private static string ReadString(ReadOnlySpan<byte> pool, uint offset)
    {
        if (offset >= pool.Length)
            throw new InvalidDataException("String offset outside the pool.");
        var tail = pool[(int)offset..];
        int end = tail.IndexOf((byte)0);
        if (end < 0)
            throw new InvalidDataException("Unterminated string in the pool.");
        return Utf8.GetString(tail[..end]);
    }

    private static uint U32(ReadOnlySpan<byte> span, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in bytes)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
