using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace Vde.Shell.Tray.Tests;

// A private session bus, so tests never touch (or depend on) the real desktop's.
internal sealed class PrivateBus : IDisposable
{
    private readonly Process _daemon;
    private readonly string _config;
    public string Address { get; }

    public PrivateBus()
    {
        _config = Path.Combine(Path.GetTempPath(), "vde-tray-bus-" + Guid.NewGuid().ToString("N") + ".conf");
        File.WriteAllText(_config, """
            <busconfig>
              <type>session</type>
              <listen>unix:tmpdir=/tmp</listen>
              <auth>EXTERNAL</auth>
              <policy context="default">
                <allow send_destination="*" eavesdrop="true"/>
                <allow eavesdrop="true"/>
                <allow own="*"/>
              </policy>
            </busconfig>
            """);
        var info = new ProcessStartInfo("dbus-daemon") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("--nofork");
        info.ArgumentList.Add("--print-address=1");
        info.ArgumentList.Add("--config-file=" + _config);
        _daemon = Process.Start(info)!;
        Address = _daemon.StandardOutput.ReadLine()!.Trim();
    }

    public DBusConnection Connect()
    {
        var connection = new DBusConnection(new DBusConnectionOptions(Address) { AutoConnect = false });
        connection.ConnectAsync().AsTask().GetAwaiter().GetResult();
        return connection;
    }

    public void Dispose()
    {
        try { _daemon.Kill(); } catch (InvalidOperationException) { }
        _daemon.Dispose();
        File.Delete(_config);
    }
}
