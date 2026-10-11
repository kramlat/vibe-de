using System.Buffers.Binary;
using System.Text;

namespace Vibe.Registry.Remote;

// The fhive socket protocol, version 1. Everything is little-endian.
//
//   frame    u32 length, then length bytes: u8 opcode, u32 requestId, body
//   string   u32 byte length, UTF-8 bytes
//   perms    u32 uid, u32 gid, u16 mode
//   value    string name, u16 kind, perms, u32 length, data (the same encoding as in a hive file)
//
// The client sends requests (opcodes below) and the server answers each with a Reply carrying the same
// requestId. Once subscribed, the server also pushes Event frames (requestId 0). The server checks every
// request against the identity of the process on the other end of the socket, never against anything the
// client says about itself.
internal static class Wire
{
    public const ushort Version = 1;
    public const int MaxFrame = 8 * 1024 * 1024;

    // requests
    public const byte Hello = 0x01;
    public const byte KeyExists = 0x10, CreateKey = 0x11, DeleteKey = 0x12, GetSubKeyNames = 0x13, GetKeyInfo = 0x14,
        SetKeyFlags = 0x15, GetKeyPermissions = 0x16, SetKeyPermissions = 0x17;
    public const byte GetValue = 0x20, GetValues = 0x21, GetValuePermissions = 0x22, SetValue = 0x23,
        SetValuePermissions = 0x24, DeleteValue = 0x25;
    public const byte Subscribe = 0x30, Unsubscribe = 0x31;

    // server frames
    public const byte Reply = 0xF0, Event = 0xF1;

    // reply status
    public const byte Ok = 0, NotFound = 1, Denied = 2, BadRequest = 3, Internal = 4, Unsupported = 5;
}

public sealed class ProtocolException(string message) : IOException(message);

internal sealed class WireWriter
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;

    public WireWriter() => _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);

    public WireWriter U8(byte value) { _writer.Write(value); return this; }
    public WireWriter U16(ushort value) { _writer.Write(value); return this; }
    public WireWriter U32(uint value) { _writer.Write(value); return this; }
    public WireWriter I32(int value) { _writer.Write(value); return this; }
    public WireWriter I64(long value) { _writer.Write(value); return this; }
    public WireWriter Bool(bool value) => U8(value ? (byte)1 : (byte)0);

    public WireWriter String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        U32((uint)bytes.Length);
        _writer.Write(bytes);
        return this;
    }

    public WireWriter Bytes(byte[] value)
    {
        U32((uint)value.Length);
        _writer.Write(value);
        return this;
    }

    public WireWriter Perms(PosixPermissions p) => U32(p.Uid).U32(p.Gid).U16(p.Mode);

    public WireWriter Value(RegistryValue value)
        => String(value.Name).U16((ushort)value.Kind).Perms(value.Permissions).Bytes(ValueCodec.Encode(value.Kind, value.RawData));

    public byte[] ToArray() => _stream.ToArray();

    // A whole frame, ready to send.
    public static byte[] Frame(byte opcode, uint requestId, byte[] body)
    {
        var frame = new byte[4 + 1 + 4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(1 + 4 + body.Length));
        frame[4] = opcode;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), requestId);
        body.CopyTo(frame, 9);
        return frame;
    }
}

internal sealed class WireReader(ReadOnlyMemory<byte> data)
{
    private int _position;

    public bool AtEnd => _position == data.Length;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _position + count > data.Length)
            throw new ProtocolException("Truncated message.");
        var span = data.Span.Slice(_position, count);
        _position += count;
        return span;
    }

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public bool Bool() => U8() != 0;

    public string String()
    {
        var length = checked((int)U32());
        try
        {
            return new UTF8Encoding(false, true).GetString(Take(length));
        }
        catch (ArgumentException)
        {
            throw new ProtocolException("Invalid UTF-8 in a string.");
        }
    }

    public byte[] Bytes() => Take(checked((int)U32())).ToArray();

    public PosixPermissions Perms()
    {
        var uid = U32();
        var gid = U32();
        var mode = U16();
        if ((mode & ~PosixPermissions.Mask) != 0)
            throw new ProtocolException("Invalid mode.");
        return new PosixPermissions(uid, gid, mode);
    }

    public RegistryValue Value()
    {
        var name = String();
        var kind = (RegistryValueKind)U16();
        if (!Enum.IsDefined(kind))
            throw new ProtocolException($"Unknown value kind {(ushort)kind}.");
        var permissions = Perms();
        var bytes = Bytes();
        try
        {
            return new RegistryValue(name, kind, ValueCodec.Decode(kind, bytes), permissions);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            throw new ProtocolException("Invalid value data: " + e.Message);
        }
    }

    public void ExpectEnd()
    {
        if (!AtEnd)
            throw new ProtocolException("Unexpected data at the end of a message.");
    }
}

internal static class FrameIO
{
    // Reads one frame, or null when the other side closed cleanly between frames.
    public static async Task<(byte opcode, uint requestId, byte[] body)?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[4];
        if (!await ReadFullyAsync(stream, header, cancel))
            return null;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is < 5 or > Wire.MaxFrame)
            throw new ProtocolException($"Bad frame length {length}.");

        var payload = new byte[length];
        if (!await ReadFullyAsync(stream, payload, cancel))
            throw new ProtocolException("The connection closed in the middle of a frame.");
        return (payload[0], BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1)), payload[5..]);
    }

    private static async Task<bool> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancel);
            if (n == 0)
                return read == 0 ? false : throw new ProtocolException("The connection closed in the middle of a frame.");
            read += n;
        }
        return true;
    }
}
