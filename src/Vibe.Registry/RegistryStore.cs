namespace Vibe.Registry;

// The registry as applications see it: HKLM, HKCU and the HKCR view over both.
public sealed class RegistryStore
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

    // Opens both hive files; a missing file gives an empty hive that will be created on Save.
    public static RegistryStore Load(string systemHivePath, string userHivePath)
        => new(Hive.Load("HKLM", systemHivePath), Hive.Load("HKCU", userHivePath));

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
