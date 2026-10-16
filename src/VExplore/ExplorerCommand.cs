namespace VExplore;

public static class ExplorerCommand
{
    // The product name; the executable is vexplore.
    public const string ProductName = "Vibe Explorer";

    public const string UsageText = """
        Vibe Explorer: the VDE shell (taskbar, notification flyout, file manager).
        usage: vexplore [--list-components]

          --list-components   show the registered components and whether the shell would load them
          -h, --help

        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            output.Write(UsageText);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--list-components")
        {
            using var host = ExplorerHost.Open();
            return ListComponents(host, output);
        }
        if (args.Length > 0)
        {
            error.Write(UsageText);
            return 1;
        }

        error.WriteLine("Vibe Explorer: the shell windows are not built yet (see --list-components)");
        return 0;
    }

    internal static int ListComponents(ExplorerHost host, TextWriter output)
    {
        foreach (var r in host.LoadableComponents)
            output.WriteLine($"load     {r.Component.Clsid} {r.Component.ClassName} ({r.Component.ThreadingModel})");
        foreach (var r in host.RefusedComponents)
            output.WriteLine($"refused  {r.Component.Clsid} {r.Component.ClassName}: {r.Trust.Reason}");
        return 0;
    }
}
