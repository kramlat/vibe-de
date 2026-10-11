using Vibe.Registry;

namespace Vde.Shell;

public sealed record FileTypeInfo(
    string MimeType,
    string Description,                  // "PNG image"
    IReadOnlyList<string> IconNames);    // freedesktop icon names (or an absolute path), most specific first

// What kind of thing a file is, and how to show it: its MIME type, a friendly name and icon names.
// The registry can override each per extension or per MIME type under HKCR\CLSID\<.ext or mime/type>:
//   (Default)      friendly type name
//   DefaultIcon    freedesktop icon name, or an absolute path to an image
//   Content Type   MIME type to treat files with this extension as
// Anything the registry does not say comes from the freedesktop MIME database.
public sealed class FileTypes
{
    public const string RegistryRoot = "CLSID";

    private readonly MimeDatabase _mime;
    private readonly ClassesRoot? _classes;
    private readonly IReadOnlyList<string> _locales;

    public FileTypes(MimeDatabase mime, ClassesRoot? classes = null, IReadOnlyList<string>? locales = null)
    {
        _mime = mime;
        _classes = classes;
        _locales = locales ?? [];
    }

    public string GetMimeType(string path)
        => Override(Extension(path), "Content Type") is { } forced ? _mime.Canonical(forced) : _mime.GuessForFile(path);

    public FileTypeInfo Describe(string path)
    {
        var ext = Extension(path);
        var mime = GetMimeType(path);

        var description = Override(ext, "") ?? Override(mime, "") ?? _mime.Describe(mime, _locales);

        var icons = new List<string>();
        if (Override(ext, "DefaultIcon") is { } extIcon)
            icons.Add(extIcon);
        if (Override(mime, "DefaultIcon") is { } mimeIcon && !icons.Contains(mimeIcon))
            icons.Add(mimeIcon);
        icons.AddRange(_mime.IconNames(mime).Where(n => !icons.Contains(n)));

        return new FileTypeInfo(mime, description, icons);
    }

    // ".PNG" -> ".png"; directories and extension-less names have none.
    private static string? Extension(string path)
    {
        if (Directory.Exists(path))
            return null;
        var ext = Path.GetExtension(path);
        return ext.Length > 1 ? ext.ToLowerInvariant() : null;
    }

    private string? Override(string? className, string valueName)
    {
        if (_classes is null || className is null)
            return null;
        var value = _classes.GetValue(RegistryRoot + "\\" + className, valueName);
        return value is { Kind: RegistryValueKind.String or RegistryValueKind.ExpandString } && value.Data is string { Length: > 0 } s ? s : null;
    }
}
