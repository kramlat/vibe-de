using System.Globalization;
using Vibe.Registry;

namespace Mkhive;

// mkhive: creates and fills registry hive files. Meant for installers and build scripts:
//   - it never needs root: ownership is data inside the hive, so a package build as an ordinary user can
//     write a hive whose objects belong to root (--owner 0:0), and the install step only has to place the file;
//   - the output is reproducible: SOURCE_DATE_EPOCH fixes every timestamp and the layout is deterministic;
//   - it never overwrites an existing hive unless told to (--merge adds to it, --force replaces it).
public static class MkhiveCommand
{
    public const int Ok = 0, UsageError = 1, IoError = 2, InputError = 3, Exists = 4;

    public const string UsageText = """
        usage: mkhive create [options] FILE [INPUT.reg ...]
               mkhive dump FILE

        create options:
          --type system|user   which registry roots the .reg files map to (default: system)
                                 system: HKEY_LOCAL_MACHINE\x -> x
                                 user:   HKEY_CURRENT_USER\x  -> x
                                 both:   HKEY_CLASSES_ROOT\x  -> Software\Classes\x
          --owner UID[:GID]    owner of everything created (default: 0:0 for system, the current user for user)
          --mode MODE          permission of the hive file, octal (default: 644 for system, 600 for user)
          --rules FILE         permission rules, see below (may be repeated)
          --auto-exec          make every shell\<verb>\command value executable (755): without this, commands
                               imported from .reg files stay inert until marked executable
          --merge              add to FILE if it exists, setting permissions only on what is new
          --force              replace FILE if it exists
          --strict             sections for other registry roots are errors, not warnings
          -v, --verbose        say what was done

        Rules files:
          key   PATH-GLOB            MODE [UID[:GID]]
          value PATH-GLOB NAME-GLOB  MODE [UID[:GID]]
          Paths are relative to the hive root; * matches inside one segment, ** across segments; NAME-GLOB @ is the
          default value. The last matching rule wins. Example:
            value Software\Classes\**\shell\*\command @ 755

        Environment: SOURCE_DATE_EPOCH makes every timestamp that second (reproducible builds).
        Exit status: 0 ok, 1 usage, 2 file error, 3 bad input, 4 hive already exists.

        """;

    private enum HiveType { System, User }

    private sealed class CreateOptions
    {
        public string? File;
        public List<string> Inputs = new();
        public HiveType Type = HiveType.System;
        public (uint uid, uint gid)? Owner;
        public ushort? FileMode;
        public List<string> RuleFiles = new();
        public bool AutoExec, Merge, Force, Strict, Verbose;
    }

    public static int Run(string[] args, TextWriter output, TextWriter error, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (args.Length == 0)
        {
            error.Write(UsageText);
            return UsageError;
        }

        switch (args[0])
        {
            case "-h" or "--help" or "help":
                output.Write(UsageText);
                return Ok;
            case "create":
                return Create(args[1..], output, error, env);
            case "dump":
                return Dump(args[1..], output, error);
            default:
                error.WriteLine($"mkhive: unknown command '{args[0]}'");
                error.Write(UsageText);
                return UsageError;
        }
    }

    // ---- dump ----

    private static int Dump(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 1)
        {
            error.WriteLine("mkhive: dump takes exactly one hive file");
            return UsageError;
        }
        try
        {
            var hive = Hive.Load("dump", args[0]);
            if (!File.Exists(args[0]))
            {
                error.WriteLine($"mkhive: {args[0]}: no such file");
                return IoError;
            }
            hive.Identity = RegistryIdentity.Root;
            HiveDump.Write(hive, output);
            return Ok;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine($"mkhive: {args[0]}: {e.Message}");
            return IoError;
        }
    }

    // ---- create ----

    private static int Create(string[] args, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var options = ParseCreate(args, error);
        if (options is null)
            return UsageError;
        var file = options.File!;

        try
        {
            return CreateCore(options, file, output, error, env);
        }
        catch (FormatException e)
        {
            error.WriteLine($"mkhive: {e.Message}");
            return InputError;
        }
        catch (FileNotFoundException e)
        {
            error.WriteLine($"mkhive: {e.FileName ?? e.Message}: no such file");
            return IoError;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine($"mkhive: {file}: {e.Message}");
            return IoError;
        }
    }

    private static int CreateCore(CreateOptions options, string file, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        bool exists = File.Exists(file);
        if (exists && !options.Merge && !options.Force)
        {
            error.WriteLine($"mkhive: {file} already exists (use --merge to add to it or --force to replace it)");
            return Exists;
        }

        var moment = Timestamp(env);
        var owner = options.Owner ?? (options.Type == HiveType.System
            ? (0u, 0u)
            : (RegistryIdentity.Current.Uid, RegistryIdentity.Current.Gid));

        // Read the rules first: a mistake in them should stop everything before any work is done.
        var rules = new PermissionRules();
        if (options.AutoExec)
            rules.Add(@"value Software\Classes\**\shell\*\command @ 755", "--auto-exec");
        foreach (var ruleFile in options.RuleFiles)
            rules.Add(ReadText(ruleFile), ruleFile);

        var inputs = options.Inputs.Select(path => (path, reg: LoadReg(path))).ToList();

        bool merging = exists && options.Merge;
        var hive = merging ? Hive.Load(options.Type.ToString(), file) : new Hive(options.Type.ToString(), () => moment);
        hive.Clock = () => moment;
        hive.Identity = RegistryIdentity.Root;   // creating on behalf of the owner: ownership is set explicitly below

        var before = merging ? HiveDump.Walk(hive).Select(Key).ToHashSet() : new HashSet<string>();

        int sections = 0, applied = 0;
        var skipped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, reg) in inputs)
        {
            sections += reg.Sections.Count;
            int here = Apply(reg, hive, options.Type);
            applied += here;
            foreach (var section in reg.Sections.Where(s => !MapsToThisHive(s.KeyPath, options.Type)))
                skipped.Add(section.KeyPath.Split('\\')[0]);
        }

        foreach (var root in skipped)
            error.WriteLine($"mkhive: warning: sections under {root} do not belong in a {options.Type.ToString().ToLowerInvariant()} hive and were skipped");
        if (skipped.Count > 0 && options.Strict)
            throw new FormatException("sections for other registry roots found (--strict)");

        SetPermissions(hive, rules, owner, before);

        Save(hive, file, options, exists);

        if (options.Verbose)
        {
            var all = HiveDump.Walk(hive).ToList();
            output.WriteLine($"mkhive: wrote {file}: {all.Count(e => e.value is null)} keys, {all.Count(e => e.value is not null)} values from {applied} sections ({inputs.Count} files)");
        }
        return Ok;
    }

    private static string Key((string path, RegistryValue? value) entry)
        => entry.value is null ? "k:" + entry.path : "v:" + entry.path + "\\" + entry.value.Name;

    private static bool MapsToThisHive(string keyPath, HiveType type)
    {
        var root = keyPath.Split('\\')[0];
        return root.Equals("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase) || root.Equals("HKCR", StringComparison.OrdinalIgnoreCase)
            || (type == HiveType.System
                ? root.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) || root.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
                : root.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) || root.Equals("HKCU", StringComparison.OrdinalIgnoreCase));
    }

    private static int Apply(RegFile reg, Hive hive, HiveType type)
    {
        int applied = 0;
        var main = type == HiveType.System ? new[] { "HKEY_LOCAL_MACHINE", "HKLM" } : ["HKEY_CURRENT_USER", "HKCU"];
        foreach (var name in main)
            applied += reg.ApplyTo(hive, name);
        foreach (var name in new[] { "HKEY_CLASSES_ROOT", "HKCR" })
            applied += reg.ApplyTo(hive, name, @"Software\Classes");
        return applied;
    }

    // Everything that was not there before this run gets its owner and mode: the default owner and modes
    // unless a rule says otherwise. (A fresh hive has nothing "before", so it covers the whole tree.)
    private static void SetPermissions(Hive hive, PermissionRules rules, (uint uid, uint gid) owner, HashSet<string> before)
    {
        foreach (var (path, value) in HiveDump.Walk(hive).ToList())
        {
            if (before.Contains(Key((path, value))))
                continue;

            if (value is null)
            {
                var match = rules.ForKey(path);
                hive.SetKeyPermissions(path, new PosixPermissions(match.Uid ?? owner.uid, match.Gid ?? owner.gid, match.Mode ?? PosixPermissions.KeyDefault));
            }
            else
            {
                var match = rules.ForValue(path, value.Name);
                hive.SetValuePermissions(path, value.Name,
                    new PosixPermissions(match.Uid ?? owner.uid, match.Gid ?? owner.gid, match.Mode ?? PosixPermissions.ValueDefault));
            }
        }
    }

    private static void Save(Hive hive, string file, CreateOptions options, bool existed)
    {
        var full = Path.GetFullPath(file);
        var mode = options.FileMode is { } m
            ? (UnixFileMode)m
            : existed && options.Merge
                ? File.GetUnixFileMode(full)                    // merging keeps the file as it was
                : options.Type == HiveType.System
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead   // 644
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite;                                                   // 600

        var directory = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(directory))
        {
            var dirMode = mode;
            if ((mode & UnixFileMode.UserRead) != 0) dirMode |= UnixFileMode.UserExecute;
            if ((mode & UnixFileMode.GroupRead) != 0) dirMode |= UnixFileMode.GroupExecute;
            if ((mode & UnixFileMode.OtherRead) != 0) dirMode |= UnixFileMode.OtherExecute;
            Directory.CreateDirectory(directory, dirMode);
        }

        // Written under its final permission and renamed into place: no moment where it is too readable or half written.
        var temp = full + ".tmp";
        File.Delete(temp);
        using (var stream = new FileStream(temp, new FileStreamOptions
               {
                   Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = mode,
               }))
        {
            hive.Write(stream);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, full, overwrite: true);
    }

    private static DateTime Timestamp(Func<string, string?> env)
        => long.TryParse(env("SOURCE_DATE_EPOCH"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : DateTime.UtcNow;

    private static string ReadText(string path)
        => File.Exists(path) ? File.ReadAllText(path) : throw new FileNotFoundException(null, path);

    private static RegFile LoadReg(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(null, path);
        try
        {
            return RegFile.Load(path);
        }
        catch (FormatException e)
        {
            throw new FormatException($"{path}: {e.Message}", e);
        }
    }

    private static CreateOptions? ParseCreate(string[] args, TextWriter error)
    {
        var options = new CreateOptions();
        bool noMoreOptions = false;
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (noMoreOptions || !arg.StartsWith('-') || arg == "-")
            {
                if (options.File is null) options.File = arg;
                else options.Inputs.Add(arg);
                continue;
            }

            string? Value()
            {
                if (i + 1 < args.Length)
                    return args[++i];
                error.WriteLine($"mkhive: {arg} needs a value");
                return null;
            }

            try
            {
                switch (arg)
                {
                    case "--":
                        noMoreOptions = true;
                        break;
                    case "--type":
                        var type = Value();
                        if (type is null) return null;
                        if (!Enum.TryParse(type, true, out options.Type) || !Enum.IsDefined(options.Type))
                        {
                            error.WriteLine($"mkhive: --type must be system or user, not '{type}'");
                            return null;
                        }
                        break;
                    case "--owner":
                        var owner = Value();
                        if (owner is null) return null;
                        var parts = owner.Split(':');
                        if (parts.Length > 2) throw new FormatException($"'{owner}' is not UID[:GID]");
                        uint uid = PermissionRules.Id(parts[0]);
                        options.Owner = (uid, parts.Length == 2 ? PermissionRules.Id(parts[1]) : uid);
                        break;
                    case "--mode":
                        var mode = Value();
                        if (mode is null) return null;
                        if (mode.Length is < 3 or > 4 || mode.Any(c => c is < '0' or > '7'))
                            throw new FormatException($"'{mode}' is not an octal mode like 644");
                        options.FileMode = (ushort)(Convert.ToUInt16(mode, 8) & 0x1FF);
                        break;
                    case "--rules":
                        var rules = Value();
                        if (rules is null) return null;
                        options.RuleFiles.Add(rules);
                        break;
                    case "--auto-exec": options.AutoExec = true; break;
                    case "--merge": options.Merge = true; break;
                    case "--force": options.Force = true; break;
                    case "--strict": options.Strict = true; break;
                    case "-v" or "--verbose": options.Verbose = true; break;
                    default:
                        error.WriteLine($"mkhive: unknown option '{arg}'");
                        return null;
                }
            }
            catch (FormatException e)
            {
                error.WriteLine($"mkhive: {arg}: {e.Message}");
                return null;
            }
        }

        if (options.File is null)
        {
            error.WriteLine("mkhive: no hive file given");
            return null;
        }
        if (options.Merge && options.Force)
        {
            error.WriteLine("mkhive: --merge and --force cannot be used together");
            return null;
        }
        return options;
    }
}
