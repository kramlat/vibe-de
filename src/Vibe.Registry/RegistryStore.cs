namespace Vibe.Registry;

// The registry as applications see it: HKLM, HKCU and the HKCR view over both.
public sealed class RegistryStore : IDisposable
{
    public Hive LocalMachine { get; }
    public Hive CurrentUser { get; }
    public ClassesRoot ClassesRoot { get; }

    public RegistryStore(Hive localMachine, Hive currentUser)
    {
        LocalMachine = localMachine;
        CurrentUser = currentUser;
        ClassesRoot = new ClassesRoot(currentUser, localMachine);
    }

    // $XDG_CONFIG_HOME/hive/User.fhive and /etc/hive/System.fhive, opened for sharing between processes.
    // The user hive is created (empty, mode 0600) if it does not exist yet. The system hive is only ever read:
    // applications have no business writing it, and a missing one simply reads as empty. Creating and filling
    // system hives is the job of the mkhive tool, run by an installer with the rights it needs.
    public static RegistryStore OpenDefault(string? userHivePath = null, string? systemHivePath = null, bool watch = true)
    {
        userHivePath ??= Path.Combine(DefaultConfigHome(), "hive", "User.fhive");
        systemHivePath ??= "/etc/hive/System.fhive";
        return new RegistryStore(
            Hive.OpenShared("HKLM", systemHivePath, watch, readOnly: true),
            Hive.OpenShared("HKCU", userHivePath, watch));
    }

    private static string DefaultConfigHome()
    {
        var configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrEmpty(configured) && Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }

    // Opens both hive files; a missing file gives an empty hive that will be created on Save.
    public static RegistryStore Load(string systemHivePath, string userHivePath)
        => new(Hive.Load("HKLM", systemHivePath), Hive.Load("HKCU", userHivePath));

    public void Dispose()
    {
        LocalMachine.Dispose();
        CurrentUser.Dispose();
    }

    // Writes the hives that changed. A system hive the user cannot write to is skipped.
    public void Save()
    {
        foreach (var hive in new[] { LocalMachine, CurrentUser })
        {
            if (!hive.IsDirty || hive.FilePath is null)
                continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(hive.FilePath))!);
                hive.Save();
            }
            catch (UnauthorizedAccessException) when (hive == LocalMachine)
            {
            }
        }
    }
}
