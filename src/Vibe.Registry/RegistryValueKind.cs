namespace Vibe.Registry;

public enum RegistryValueKind : ushort
{
    // Windows-compatible (numbers match REG_*)
    None = 0,
    String = 1,
    ExpandString = 2,
    Binary = 3,
    DWord = 4,
    DWordBigEndian = 5,
    Link = 6,
    MultiString = 7,
    ResourceList = 8,
    FullResourceDescriptor = 9,
    ResourceRequirementsList = 10,
    QWord = 11,

    // Vibe extensions
    Bool = 100,
    Int32 = 101,
    Int64 = 102,
    Float = 103,
    Double = 104,
    Decimal = 105,
    Date = 106,
    Time = 107,
    DateTime = 108,
    Guid = 109,
    Json = 110,
    Yaml = 111,
    Xml = 112,
}
