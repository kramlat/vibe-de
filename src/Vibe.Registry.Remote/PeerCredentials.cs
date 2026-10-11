using System.Buffers.Binary;
using System.Net.Sockets;

namespace Vibe.Registry.Remote;

// Who is on the other end of a Unix socket, as the kernel says (SO_PEERCRED and SO_PEERGROUPS), not as the
// peer claims. This is what makes the daemon's permission checks binding.
public static class PeerCredentials
{
    private const int SolSocket = 1, SoPeerCred = 17, SoPeerGroups = 59;

    public static RegistryIdentity Read(Socket socket)
    {
        var credentials = new byte[12];   // struct ucred: pid, uid, gid
        socket.GetRawSocketOption(SolSocket, SoPeerCred, credentials);
        int pid = BinaryPrimitives.ReadInt32LittleEndian(credentials);
        uint uid = BinaryPrimitives.ReadUInt32LittleEndian(credentials.AsSpan(4));
        uint gid = BinaryPrimitives.ReadUInt32LittleEndian(credentials.AsSpan(8));
        return new RegistryIdentity(uid, gid, Groups(socket, pid));
    }

    private static IEnumerable<uint> Groups(Socket socket, int pid)
    {
        // The kernel lists the peer's supplementary groups directly (Linux 4.13+).
        for (int size = 256; size <= 64 * 1024; size *= 4)
        {
            var buffer = new byte[size];
            try
            {
                int length = socket.GetRawSocketOption(SolSocket, SoPeerGroups, buffer);
                return Enumerable.Range(0, length / 4).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 4))).ToList();
            }
            catch (SocketException) when (size < 64 * 1024)
            {
                // buffer too small (or an older kernel): try bigger, then fall back below
            }
            catch (SocketException)
            {
                break;
            }
        }
        return GroupsFromProc(pid);
    }

    // Older kernels: read them from /proc. The process could in theory have changed since, which is why
    // SO_PEERGROUPS is preferred.
    private static IEnumerable<uint> GroupsFromProc(int pid)
    {
        try
        {
            var line = File.ReadLines($"/proc/{pid}/status").FirstOrDefault(l => l.StartsWith("Groups:", StringComparison.Ordinal));
            return line is null ? [] : line["Groups:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToList();
        }
        catch (Exception e) when (e is IOException or FormatException)
        {
            return [];
        }
    }
}
