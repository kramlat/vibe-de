using System.Runtime.InteropServices;

namespace Vibe.Registry;

[Flags]
public enum Access
{
    None = 0,
    Execute = 1,
    Write = 2,
    Read = 4,
}

// Owner, group and the nine rwxrwxrwx bits, on every key and every value.
//
// For a key, as for a directory: Read lists its sub-keys and values, Write adds and removes entries,
// Execute lets you reach what is inside (and is needed on every key along a path).
// For a value, as for a file: Read reads it, Write changes it. A value holding a shell command also needs
// Execute before the shell will run it.
public readonly record struct PosixPermissions(uint Uid, uint Gid, ushort Mode)
{
    public const ushort KeyDefault = 0x1ED;     // 0755
    public const ushort ValueDefault = 0x1A4;   // 0644
    public const ushort Mask = 0x1FF;           // 0777

    // "755" or "0644" -> the mode bits.
    public static ushort ParseMode(string octal) => (ushort)(Convert.ToUInt16(octal, 8) & Mask);

    public string ModeText => Convert.ToString(Mode, 8).PadLeft(4, '0');

    // "rwxr-xr-x"
    public string Symbolic
    {
        get
        {
            var text = new char[9];
            for (int i = 0; i < 9; i++)
                text[i] = (Mode & (0x100 >> i)) != 0 ? "rwx"[i % 3] : '-';
            return new string(text);
        }
    }

    public PosixPermissions WithMode(ushort mode) => this with { Mode = mode };
}

// Who is asking. By default the user and groups of this process.
public sealed class RegistryIdentity
{
    private static RegistryIdentity? _current;

    public uint Uid { get; }
    public uint Gid { get; }
    public IReadOnlyCollection<uint> Groups { get; }

    public RegistryIdentity(uint uid, uint gid, IEnumerable<uint>? groups = null)
    {
        Uid = uid;
        Gid = gid;
        Groups = new HashSet<uint>((groups ?? []).Append(gid));
    }

    public bool IsRoot => Uid == 0;
    public bool InGroup(uint gid) => Groups.Contains(gid);

    public static RegistryIdentity Root { get; } = new(0, 0);

    public static RegistryIdentity Current => _current ??= ReadCurrent();

    // The bits that apply to this identity: owner's if it owns the object, else group's if it is in the group, else other's.
    public Access AccessTo(PosixPermissions p)
    {
        int shift = Uid == p.Uid ? 6 : InGroup(p.Gid) ? 3 : 0;
        return (Access)((p.Mode >> shift) & 7);
    }

    // Root is always allowed: in practice only mkhive or an elevated Regedit ever runs as root.
    public bool Allows(PosixPermissions p, Access wanted) => IsRoot || (AccessTo(p) & wanted) == wanted;

    private static RegistryIdentity ReadCurrent()
    {
        var groups = new List<uint>();
        int count = getgroups(0, null);
        if (count > 0)
        {
            var list = new uint[count];
            count = getgroups(count, list);
            groups.AddRange(list.Take(Math.Max(count, 0)));
        }
        return new RegistryIdentity(geteuid(), getegid(), groups);
    }

    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc")] private static extern uint getegid();
    [DllImport("libc")] private static extern int getgroups(int size, uint[]? list);
}
