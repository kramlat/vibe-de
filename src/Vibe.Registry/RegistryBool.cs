namespace Vibe.Registry;

// Yes/no settings are stored as REG_BOOL. Values written as a REG_DWORD of 0 or 1 (by other tools, or by
// older versions) are still understood when reading.
public static class RegistryBool
{
    public static bool? Get(IHive hive, string key, string name) => hive.GetValue(key, name) switch
    {
        { Kind: RegistryValueKind.Bool, Data: bool b } => b,
        { Kind: RegistryValueKind.DWord, Data: uint d } => d != 0,
        _ => null,
    };

    public static bool Get(IHive hive, string key, string name, bool fallback) => Get(hive, key, name) ?? fallback;

    public static void Set(IHive hive, string key, string name, bool value) => hive.SetValue(key, name, RegistryValueKind.Bool, value);
}

// Whole numbers are stored as REG_INT32 (or REG_INT64 when they need it), not as Windows' unsigned DWORD.
// Values written as DWORD or QWORD by other tools or older versions are still understood when reading.
public static class RegistryNumber
{
    public static long? Get(IHive hive, string key, string name) => hive.GetValue(key, name) switch
    {
        { Kind: RegistryValueKind.Int32, Data: int i } => i,
        { Kind: RegistryValueKind.Int64, Data: long l } => l,
        { Kind: RegistryValueKind.DWord, Data: uint d } => d,
        { Kind: RegistryValueKind.QWord, Data: ulong q } when q <= long.MaxValue => (long)q,
        _ => null,
    };

    public static long Get(IHive hive, string key, string name, long fallback) => Get(hive, key, name) ?? fallback;

    public static void Set(IHive hive, string key, string name, int value) => hive.SetValue(key, name, RegistryValueKind.Int32, value);
    public static void Set(IHive hive, string key, string name, long value)
    {
        if (value is >= int.MinValue and <= int.MaxValue) hive.SetValue(key, name, RegistryValueKind.Int32, (int)value);
        else hive.SetValue(key, name, RegistryValueKind.Int64, value);
    }
}
