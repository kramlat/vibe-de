using Vibe.Registry;

namespace Vde.Shell;

// IsTrusted says whether the shell may run Command for this user (see CommandTrust); an untrusted verb is still
// listed so a menu can show it greyed out, or offer to mark it executable.
public sealed record FileVerb(string Id, string Label, string Command, bool IsTrusted = true, string? UntrustedReason = null);

public sealed record FileTypeInfo(
    string MimeType,
    string Description,                  // "PNG image"
    IReadOnlyList<string> IconNames);    // freedesktop icon names (or an absolute path), most specific first

// What kind of thing a file is, and how to show it: its MIME type, a friendly name and icon names.
// The registry can override each, in either of the two layouts Explorer has always read:
//   HKCR\CLSID\<.ext or mime/type>   (Default) friendly name, DefaultIcon, Content Type
//   HKCR\.ext -> ProgID              Content Type on .ext; (Default) and DefaultIcon on HKCR\<ProgID>
// The CLSID class is checked first, then the ProgID chain, then the MIME type's own class.
// DefaultIcon is a freedesktop icon name or an absolute path; Windows "file,index" references are ignored.
// Anything the registry does not say comes from the freedesktop MIME database.
public sealed class FileTypes
{
    public const string RegistryRoot = "CLSID";

    private readonly MimeDatabase _mime;
    private readonly ClassesRoot? _classes;
    private readonly IReadOnlyList<string> _locales;
    private readonly RegistryIdentity _identity;

    public FileTypes(MimeDatabase mime, ClassesRoot? classes = null, IReadOnlyList<string>? locales = null, RegistryIdentity? identity = null)
    {
        _mime = mime;
        _classes = classes;
        _locales = locales ?? [];
        _identity = identity ?? RegistryIdentity.Current;
    }

    public string GetMimeType(string path)
    {
        var ext = Extension(path);
        var forced = Lookup(ext, "Content Type") ?? (ext is null ? null : Read(ext, "Content Type"));
        return forced is not null ? _mime.Canonical(forced) : _mime.GuessForFile(path);
    }

    public FileTypeInfo Describe(string path)
    {
        var ext = Extension(path);
        var mime = GetMimeType(path);

        var description = Lookup(ext, "") ?? ProgId(ext, "") ?? Lookup(mime, "") ?? _mime.Describe(mime, _locales);

        var icons = new List<string>();
        foreach (var candidate in new[] { Lookup(ext, "DefaultIcon"), ProgId(ext, "DefaultIcon"), Lookup(mime, "DefaultIcon") })
            if (candidate is not null && IsDrawableHere(candidate) && !icons.Contains(candidate))
                icons.Add(candidate);
        icons.AddRange(_mime.IconNames(mime).Where(n => !icons.Contains(n)));

        return new FileTypeInfo(mime, description, icons);
    }

    // The actions the registry defines for a file: shell\<verb> with a name and command, e.g. open, edit, print.
    // Checked in the same order as the other settings; the default verb (shell's own default value, else "open") comes first.
    public IReadOnlyList<FileVerb> GetVerbs(string path)
    {
        var ext = Extension(path);
        var mime = GetMimeType(path);
        var roots = new List<string>();
        if (ext is not null)
            roots.Add(RegistryRoot + "\\" + ext);
        if (ext is not null && Read(ext, "") is { } progId)
            roots.Add(progId);
        roots.Add(RegistryRoot + "\\" + mime);

        var verbs = new List<FileVerb>();
        string? defaultVerb = null;
        foreach (var root in roots)
        {
            var shell = root + "\\shell";
            if (_classes is null || !_classes.KeyExists(shell))
                continue;
            defaultVerb ??= Read(shell, "");

            foreach (var verb in _classes.GetSubKeyNames(shell))
            {
                if (verbs.Any(v => string.Equals(v.Id, verb, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (Read(shell + "\\" + verb + "\\command", "") is not { } command)
                    continue;
                var label = Read(shell + "\\" + verb, "")?.Replace("&", "") ?? char.ToUpperInvariant(verb[0]) + verb[1..];
                var trust = CommandTrust.Evaluate(_classes.GetValuePermissions(shell + "\\" + verb + "\\command", ""), _identity);
                verbs.Add(new FileVerb(verb, label, command, trust.IsTrusted, trust.Reason));
            }
        }

        var first = defaultVerb ?? "open";
        return verbs.OrderBy(v => string.Equals(v.Id, first, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
    }

    // Windows layout: HKCR\.ext (Default = ProgID), then HKCR\<ProgID> holds the name and DefaultIcon.
    private string? ProgId(string? ext, string valueName)
    {
        if (ext is null || Read(ext, "") is not { } progId)
            return null;
        return Read(progId, valueName);
    }

    // A Windows icon reference ("C:\app.exe,0", "shell32.dll,3") cannot be drawn here;
    // freedesktop names and absolute Linux paths can.
    private static bool IsDrawableHere(string icon)
        => icon.StartsWith('/') || (!icon.Contains('\\') && !icon.Contains(','));

    // ".PNG" -> ".png"; directories and extension-less names have none.
    private static string? Extension(string path)
    {
        if (Directory.Exists(path))
            return null;
        var ext = Path.GetExtension(path);
        return ext.Length > 1 ? ext.ToLowerInvariant() : null;
    }

    // HKCR\CLSID\<class>
    private string? Lookup(string? className, string valueName)
        => className is null ? null : Read(RegistryRoot + "\\" + className, valueName);

    // A non-empty string value at an HKCR path. Anything else (missing, wrong type) counts as not set,
    // so a bad registry entry can never break the file list.
    private string? Read(string path, string valueName)
    {
        var value = _classes?.GetValue(path, valueName);
        return value is { Kind: RegistryValueKind.String or RegistryValueKind.ExpandString } && value.Data is string { Length: > 0 } s ? s : null;
    }
}
