using System.Diagnostics;

namespace Vibe.Registry;

// Whether something stored in the registry may be run or loaded as code, judged by who owns it and who can
// change it: the same few things a program file needs on Unix. Used for shell commands (the value holding
// the command line) and for components (the value naming the assembly to load).
public sealed record ExecutionTrust(bool IsTrusted, string? Reason)
{
    public static readonly ExecutionTrust Trusted = new(true, null);

    // A value: the caller may execute it, it belongs to the caller or to root, and nobody else can write to it.
    // Root still needs some x bit, so an imported 0644 entry stays inert even for root.
    public static ExecutionTrust Evaluate(PosixPermissions? permissions, RegistryIdentity identity)
    {
        if (permissions is not { } p)
            return new(false, "it has no permissions");
        if (!identity.Allows(p, Access.Execute) || (identity.IsRoot && (p.Mode & 0x49) == 0))   // root needs some x bit too
            return new(false, $"it is not executable by you (mode {p.ModeText})");
        if (p.Uid != 0 && p.Uid != identity.Uid)
            return new(false, $"it belongs to another user (uid {p.Uid})");
        if ((p.Mode & 0x2) != 0)
            return new(false, "anyone can write to it");
        return Trusted;
    }

    // A file on disk that is about to be treated as code (an assembly): not writable by others and owned by
    // the caller or root. (No x bit is asked of it: assemblies are loaded, not run.)
    public static ExecutionTrust EvaluateFile(string path, RegistryIdentity identity)
    {
        if (!File.Exists(path))
            return new(false, "the file does not exist");
        if ((File.GetUnixFileMode(path) & UnixFileMode.OtherWrite) != 0)
            return new(false, "anyone can write to the file");
        if (FileOwner(path) is not { } owner)
            return new(false, "its owner cannot be determined");
        if (owner != 0 && owner != identity.Uid)
            return new(false, $"the file belongs to another user (uid {owner})");
        return Trusted;
    }

    // The uid that owns a file. .NET has no call for this and struct stat differs between architectures,
    // so ask coreutils.
    internal static uint? FileOwner(string path)
    {
        try
        {
            var info = new ProcessStartInfo("stat") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("%u");
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(path);
            using var process = Process.Start(info);
            if (process is null)
                return null;
            var text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && uint.TryParse(text, out var uid) ? uid : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
