using System.Text.RegularExpressions;
using Vde.Components;
using Vibe.Registry;

namespace RegSvrVDE;

// RegSvrVDE: registers and unregisters VDE components, the way regsvr32 does for COM servers.
public static partial class RegSvrVdeCommand
{
    public const int Ok = 0, UsageError = 1, AssemblyError = 2, NoComponents = 3, RegistrationFailed = 4, InstallerFailed = 5;

    public const string UsageText = """
        usage: RegSvrVDE [/u] [/s] [/n] [/i[:commandline]] [options] assembly [assembly ...]

          /u, --unregister         remove the registration instead of adding it
          /s, --silent             no message on success (errors are still reported)
          /i[:cmd], --install[=cmd]  also run the assembly's installer hook (Install(installing, cmd)); the only
                                   time any code of the component is run
          /n, --no-register        with /i: run only the installer hook, do not (un)register

        options:
          --system                 register in the system hive (for installers, run with the needed rights)
          --user-hive PATH         hive to use for the user (default: ~/.config/hive/User.fhive)
          --system-hive PATH       system hive (default: /etc/hive/System.fhive; must already exist: see mkhive)
          --force                  register even if the assembly file is writable by others or owned by another user

        An assembly declares its components with [VdeComponent("{guid}", ProgId = ..., ThreadingModel = ...)].
        Reading them does not run the assembly. Registering writes CLSID\{guid}\InprocServer32 in HKCR and marks
        it executable: from then on the host (VExplore) will load it.
        Exit status: 0 ok, 1 usage, 2 cannot read assembly, 3 no components, 4 registration failed, 5 installer failed.

        """;

    private sealed class Options
    {
        public List<string> Assemblies = new();
        public bool Unregister, Silent, NoRegister, Install, System, Force;
        public string? InstallCommandLine;
        public string? UserHive, SystemHive;
    }

    // "/u" "/s" "/n" "/i" "/i:..." are switches; anything else starting with '/' is a path.
    [GeneratedRegex(@"^/([usn]|i(:.*)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SlashSwitch();

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0)
        {
            error.Write(UsageText);
            return UsageError;
        }
        if (args.Any(a => a is "-h" or "--help" or "/?" or "/h"))
        {
            output.Write(UsageText);
            return Ok;
        }

        var options = Parse(args, error);
        if (options is null)
            return UsageError;

        Hive hive;
        try
        {
            hive = OpenTarget(options, error, out var failure);
            if (failure != Ok)
                return failure;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error.WriteLine($"RegSvrVDE: cannot open the {(options.System ? "system" : "user")} hive: {e.Message}");
            return RegistrationFailed;
        }

        using (hive)
        {
            var hkcr = new ClassesRoot(hive);
            int result = Ok;
            foreach (var assembly in options.Assemblies)
            {
                int code = Process(assembly, options, hkcr, output, error);
                if (result == Ok)
                    result = code;
            }
            return result;
        }
    }

    private static int Process(string path, Options options, ClassesRoot hkcr, TextWriter output, TextWriter error)
    {
        Inspection inspection;
        try
        {
            inspection = AssemblyInspector.Inspect(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            error.WriteLine($"RegSvrVDE: {path}: no such file");
            return AssemblyError;
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or InvalidOperationException or ArgumentException)
        {
            error.WriteLine($"RegSvrVDE: {path}: not a readable .NET assembly ({e.Message})");
            return AssemblyError;
        }

        if (!options.Unregister)   // skipped classes only matter when registering
            foreach (var warning in inspection.Warnings)
                error.WriteLine($"RegSvrVDE: {path}: warning: {warning}");

        if (!options.NoRegister)
        {
            if (inspection.Components.Count == 0)
            {
                error.WriteLine($"RegSvrVDE: {path}: no components declared (see [VdeComponent])");
                return NoComponents;
            }

            if (!options.Unregister && !options.Force)
            {
                var trust = ExecutionTrust.EvaluateFile(path, RegistryIdentity.Current);
                if (!trust.IsTrusted)
                {
                    error.WriteLine($"RegSvrVDE: {path}: refusing to register: {trust.Reason} (use --force to override)");
                    return RegistrationFailed;
                }
            }

            try
            {
                foreach (var component in inspection.Components)
                {
                    if (options.Unregister)
                    {
                        bool removed = ComponentRegistrar.Unregister(hkcr, component.Clsid);
                        if (!options.Silent)
                            output.WriteLine($"RegSvrVDE: {path}: {(removed ? "unregistered" : "was not registered:")} {component.Clsid} {component.ClassName}");
                    }
                    else
                    {
                        ComponentRegistrar.Register(hkcr, component);
                        if (!options.Silent)
                            output.WriteLine($"RegSvrVDE: {path}: registered {component.Clsid} {component.ClassName} ({component.ThreadingModel})");
                    }
                }
            }
            catch (UnauthorizedAccessException e)
            {
                error.WriteLine($"RegSvrVDE: {path}: permission denied: {e.Message}");
                return RegistrationFailed;
            }
        }

        if (options.Install)
            return RunInstaller(path, inspection, options, hkcr, error);
        return Ok;
    }

    // The one place component code runs: the installer hook, and only with /i.
    private static int RunInstaller(string path, Inspection inspection, Options options, ClassesRoot hkcr, TextWriter error)
    {
        if (inspection.InstallerType is null)
        {
            error.WriteLine($"RegSvrVDE: {path}: /i given but the assembly declares no installer ([assembly: VdeInstaller])");
            return InstallerFailed;
        }

        try
        {
            var assembly = System.Reflection.Assembly.LoadFrom(path);
            var type = assembly.GetType(inspection.InstallerType, throwOnError: true)!;
            if (Activator.CreateInstance(type) is not IVdeInstaller installer)
                throw new InvalidOperationException($"{type.FullName} does not implement IVdeInstaller");
            installer.Install(new VdeInstallContext(hkcr, !options.Unregister, options.InstallCommandLine, Path.GetFullPath(path)));
            return Ok;
        }
        catch (Exception e)   // component code: whatever it throws is its failure, not ours
        {
            error.WriteLine($"RegSvrVDE: {path}: the installer failed: {(e is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : e).Message}");
            return InstallerFailed;
        }
    }

    private static Hive OpenTarget(Options options, TextWriter error, out int failure)
    {
        failure = Ok;
        if (!options.System)
        {
            var userPath = options.UserHive ?? Path.Combine(
                Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config && Path.IsPathRooted(config)
                    ? config
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
                "hive", "User.fhive");
            return Hive.OpenShared("HKCU", userPath, watch: false);
        }

        var systemPath = options.SystemHive ?? "/etc/hive/System.fhive";
        if (!File.Exists(systemPath))
        {
            error.WriteLine($"RegSvrVDE: the system hive {systemPath} does not exist; create it with mkhive first");
            failure = RegistrationFailed;
            return null!;
        }
        return Hive.OpenShared("HKLM", systemPath, watch: false);
    }

    private static Options? Parse(string[] args, TextWriter error)
    {
        var options = new Options();
        foreach (var arg in args)
        {
            if (SlashSwitch().IsMatch(arg))
            {
                switch (char.ToLowerInvariant(arg[1]))
                {
                    case 'u': options.Unregister = true; break;
                    case 's': options.Silent = true; break;
                    case 'n': options.NoRegister = true; break;
                    case 'i':
                        options.Install = true;
                        if (arg.Length > 3) options.InstallCommandLine = arg[3..];
                        break;
                }
                continue;
            }

            switch (arg)
            {
                case "-u" or "--unregister": options.Unregister = true; break;
                case "-s" or "--silent": options.Silent = true; break;
                case "-n" or "--no-register": options.NoRegister = true; break;
                case "-i" or "--install": options.Install = true; break;
                case "--system": options.System = true; break;
                case "--force": options.Force = true; break;
                case var a when a.StartsWith("--install="):
                    options.Install = true;
                    options.InstallCommandLine = a["--install=".Length..];
                    break;
                case var a when a.StartsWith("--user-hive="): options.UserHive = a["--user-hive=".Length..]; break;
                case var a when a.StartsWith("--system-hive="): options.SystemHive = a["--system-hive=".Length..]; break;
                case var a when a.StartsWith('-') && a.Length > 1 && !File.Exists(a):
                    error.WriteLine($"RegSvrVDE: unknown option '{a}'");
                    return null;
                default:
                    options.Assemblies.Add(arg);
                    break;
            }
        }

        if (options.Assemblies.Count == 0)
        {
            error.WriteLine("RegSvrVDE: no assembly given");
            return null;
        }
        if (options.NoRegister && !options.Install)
        {
            error.WriteLine("RegSvrVDE: /n only makes sense together with /i");
            return null;
        }
        return options;
    }
}
