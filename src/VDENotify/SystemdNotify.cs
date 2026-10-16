using System.Net.Sockets;
using System.Text;

namespace VdeNotify;

// sd_notify, enough for Type=notify (the same few lines fhived has; neither is worth a shared assembly).
internal static class SystemdNotify
{
    public static void Ready(Func<string, string?> env) => Send(env, "READY=1\nSTATUS=serving org.freedesktop.Notifications");

    public static void Stopping(Func<string, string?> env) => Send(env, "STOPPING=1");

    private static void Send(Func<string, string?> env, string message)
    {
        var path = env("NOTIFY_SOCKET");
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            var endpoint = path[0] == '@' ? "\0" + path[1..] : path;   // '@' is the abstract namespace
            socket.SendTo(Encoding.UTF8.GetBytes(message), new UnixDomainSocketEndPoint(endpoint));
        }
        catch (Exception e) when (e is SocketException or IOException or ArgumentException)
        {
        }
    }
}
