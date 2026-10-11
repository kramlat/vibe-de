using System.Diagnostics;

namespace libFluent.Shell;

public static class ShellExecute
{
    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}
