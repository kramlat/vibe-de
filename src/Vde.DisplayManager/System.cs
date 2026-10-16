using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vde.DisplayManager;

// The parts that need to be root: switching the screen to a virtual terminal, handing a terminal to a user, and
// starting a process as that user. Thin wrappers; the decisions are in SessionPlans and LoginManager.
public static class VirtualTerminals
{
    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, ulong request, IntPtr arg);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int chown([MarshalAs(UnmanagedType.LPStr)] string path, int uid, int gid);

    private const ulong VtActivate = 0x5606, VtWaitActive = 0x5607;
    private const int ORdwr = 2, ONoctty = 0x100;

    // Makes terminal n the one on screen, and waits until it is.
    public static void Switch(int vt)
    {
        var fd = open("/dev/tty0", ORdwr | ONoctty);
        if (fd < 0) throw new IOException("cannot open /dev/tty0: errno " + Marshal.GetLastWin32Error());
        try
        {
            if (ioctl(fd, VtActivate, vt) < 0 || ioctl(fd, VtWaitActive, vt) < 0)
                throw new IOException($"cannot switch to tty{vt}: errno " + Marshal.GetLastWin32Error());
        }
        finally { close(fd); }
    }

    // The user who logs in on a terminal owns it (and only they and their group can write to it), like login(1) does.
    public static void Give(string tty, int uid, int gid)
    {
        if (chown(tty, uid, gid) != 0) throw new IOException($"cannot give {tty} to uid {uid}: errno " + Marshal.GetLastWin32Error());
        File.SetUnixFileMode(tty, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);
    }
}

public static class UserProcesses
{
    // Starts the plan's command as the plan's user, with exactly the plan's environment. The process inherits our
    // standard streams (the daemon's log). Must run as root for anyone but ourselves.
    public static Process Start(LaunchPlan plan)
    {
        var info = new ProcessStartInfo(plan.Command[0]) { UseShellExecute = false, WorkingDirectory = plan.WorkingDirectory, UserName = plan.UserName };
        foreach (var arg in plan.Command.Skip(1)) info.ArgumentList.Add(arg);
        info.Environment.Clear();
        foreach (var (k, v) in plan.Environment) info.Environment[k] = v;
        return Process.Start(info) ?? throw new IOException("cannot start " + plan.Command[0]);
    }
}
