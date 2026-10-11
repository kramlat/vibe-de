using System.Net.Sockets;
using System.Text;

namespace Fhived;

// sd_notify, enough of it for Type=notify: tells systemd the service is ready, so units ordered after it
// do not start before the socket is really being served.
internal static class SystemdNotify
{
    public static void Ready(Func<string, string?> env) => Send(env, "READY=1\nSTATUS=serving");

    public static void Stopping(Func<string, string?> env) => Send(env, "STOPPING=1");

    private static void Send(Func<string, string?> env, string message)
    {
        var path = env("NOTIFY_SOCKET");
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            // a leading '@' means the abstract namespace, which is a NUL byte on the wire
            var endpoint = path[0] == '@' ? "\0" + path[1..] : path;
            socket.SendTo(Encoding.UTF8.GetBytes(message), new UnixDomainSocketEndPoint(endpoint));
        }
        catch (Exception e) when (e is SocketException or IOException or ArgumentException)
        {
            // nobody listening is not our problem
        }
    }
}
