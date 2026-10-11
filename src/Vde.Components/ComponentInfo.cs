namespace Vde.Components;

public sealed record ComponentInfo(
    string Clsid,                 // "{5A0E2D4C-...}", upper case
    string ClassName,             // "Namespace.Type"
    string AssemblyPath,
    string AssemblyName,          // the assembly's full name
    ThreadingModel ThreadingModel,
    string? ProgId = null,
    string? Description = null,
    IReadOnlyList<string>? Categories = null)
{
    // "{...}" in the one spelling the registry uses; null when it is not a GUID.
    public static string? NormalizeGuid(string? text)
        => Guid.TryParse(text, out var guid) ? guid.ToString("B").ToUpperInvariant() : null;
}
