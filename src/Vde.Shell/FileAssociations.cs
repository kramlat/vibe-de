using Vde.Freedesktop;
using Vibe.Registry;

namespace Vde.Shell;

// Which application opens which MIME type.
//
// Sources, strongest first:
//   1. the registry, under HKCR\MIME\<mime type> (HKCU overrides HKLM):
//        Default  (String)       desktop id of the chosen application
//        Added    (MultiString)  further applications for the "Open with" list
//        Removed  (MultiString)  applications hidden for this type
//   2. freedesktop mimeapps.list files (desktop-specific first, user before system)
//   3. the applications' own MimeType= lists
// A "Removed" entry in any source hides that application for the type everywhere below it.
public sealed class FileAssociations
{
    public const string RegistryRoot = "MIME";

    private readonly XdgDirectories _xdg;
    private readonly MimeDatabase _mime;
    private readonly DesktopEntryDatabase _apps;
    private readonly IReadOnlyList<string> _desktops;
    private readonly ClassesRoot? _classes;
    private readonly bool _mirror;

    // classes is HKCR. Without it, associations live in mimeapps.list only. mirrorToFreedesktop also writes each choice to the user's mimeapps.list so that
    // xdg-open and other desktops' apps agree with the shell; the registry stays the source of truth.
    public FileAssociations(
        XdgDirectories xdg, MimeDatabase mime, DesktopEntryDatabase apps, IReadOnlyList<string> desktops,
        ClassesRoot? classes = null, bool mirrorToFreedesktop = true)
    {
        _xdg = xdg;
        _mime = mime;
        _apps = apps;
        _desktops = desktops;
        _classes = classes;
        _mirror = mirrorToFreedesktop;
    }

    // The user's own freedesktop list.
    public string UserMimeAppsPath => Path.Combine(_xdg.ConfigHome, "mimeapps.list");

    public DesktopEntry? GetDefault(string mimeType) => GetHandlers(mimeType).FirstOrDefault();

    // Only the application the user chose in the registry (HKCR\MIME\<type>\Default), ignoring every fallback.
    public DesktopEntry? GetRegistryDefault(string mimeType)
    {
        if (_classes is null)
            return null;
        var id = new RegistryList(_classes).Defaults(_mime.Canonical(mimeType)).FirstOrDefault();
        return id is not null && _apps.Find(id) is { } app && IsUsable(app) ? app : null;
    }

    // The "Open with" list: the chosen default first, then other associated applications.
    // Handlers of parent types (a C source file is also plain text) come after those of the exact type.
    public IReadOnlyList<DesktopEntry> GetHandlers(string mimeType)
    {
        var lists = LoadLists();
        var result = new List<DesktopEntry>();

        void Add(string? id)
        {
            if (id is null || _apps.Find(id) is not { } app || result.Contains(app) || !IsUsable(app))
                return;
            result.Add(app);
        }

        foreach (var type in _mime.WithAncestors(mimeType))
        {
            var removed = lists.SelectMany(l => l.Removed(type)).ToHashSet(StringComparer.Ordinal);

            foreach (var list in lists)
                foreach (var id in list.Defaults(type))
                    Add(id);
            foreach (var list in lists)
                foreach (var id in list.Added(type))
                    if (!removed.Contains(id))
                        Add(id);

            foreach (var app in _apps.Entries
                         .Where(a => a.MimeTypes.Any(m => _mime.Canonical(m) == type) && !removed.Contains(a.Id))
                         .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                Add(app.Id);
        }
        return result;
    }

    public void SetDefault(string mimeType, DesktopEntry app)
    {
        mimeType = _mime.Canonical(mimeType);

        if (_classes is not null)
        {
            var hive = _classes;
            var key = KeyFor(mimeType);
            hive.SetValue(key, "Default", RegistryValueKind.String, app.Id);

            // A chosen app is also an association, so it stays in "Open with".
            var added = ReadList(hive, key, "Added");
            if (!added.Contains(app.Id))
                hive.SetValue(key, "Added", RegistryValueKind.MultiString, added.Append(app.Id).ToArray());
            // Choosing an app un-hides it.
            var removed = ReadList(hive, key, "Removed");
            if (removed.Contains(app.Id))
                hive.SetValue(key, "Removed", RegistryValueKind.MultiString, removed.Where(r => r != app.Id).ToArray());
        }

        if (_mirror || _classes is null)
            WriteMimeApps(mimeType, app);
    }

    // Drops the registry choice for a type, so the freedesktop settings and the apps' own lists decide again.
    public void ClearDefault(string mimeType)
    {
        _classes?.DeleteValue(KeyFor(_mime.Canonical(mimeType)), "Default");
    }

    private static string KeyFor(string mimeType) => RegistryRoot + "\\" + mimeType;

    private static string[] ReadList(ClassesRoot hive, string key, string name)
        => hive.GetValue(key, name) is { Kind: RegistryValueKind.MultiString } v ? (string[])v.Data! : [];

    private void WriteMimeApps(string mimeType, DesktopEntry app)
    {
        IniEditor.SetValue(UserMimeAppsPath, "Default Applications", mimeType, app.Id + ";");

        var added = IniFile.Load(UserMimeAppsPath).GetList("Added Associations", mimeType);
        if (!added.Contains(app.Id))
            IniEditor.SetValue(UserMimeAppsPath, "Added Associations", mimeType,
                string.Concat(added.Append(app.Id).Select(i => i + ";")));
    }

    private static bool IsUsable(DesktopEntry app) => app.Type == "Application" && !app.Hidden && app.Exec is not null;

    // One source of associations: a registry hive or a mimeapps.list file.
    private interface IAssociationList
    {
        IReadOnlyList<string> Defaults(string type);
        IReadOnlyList<string> Added(string type);
        IReadOnlyList<string> Removed(string type);
    }

    private sealed class MimeAppsFile(IniFile ini) : IAssociationList
    {
        public IReadOnlyList<string> Defaults(string type) => ini.GetList("Default Applications", type);
        public IReadOnlyList<string> Added(string type) => ini.GetList("Added Associations", type);
        public IReadOnlyList<string> Removed(string type) => ini.GetList("Removed Associations", type);
    }

    private sealed class RegistryList(ClassesRoot hive) : IAssociationList
    {
        public IReadOnlyList<string> Defaults(string type)
            => hive.GetValue(KeyFor(type), "Default") is { Kind: RegistryValueKind.String } v && v.Data is string { Length: > 0 } id ? [id] : [];
        public IReadOnlyList<string> Added(string type) => ReadList(hive, KeyFor(type), "Added");
        public IReadOnlyList<string> Removed(string type) => ReadList(hive, KeyFor(type), "Removed");
    }

    private List<IAssociationList> LoadLists()
    {
        var lists = new List<IAssociationList>();
        if (_classes is not null)
            lists.Add(new RegistryList(_classes));

        var files = new List<string>();
        foreach (var configDir in _xdg.ConfigSearchPath)
        {
            foreach (var desktop in _desktops)
                files.Add(Path.Combine(configDir, desktop.ToLowerInvariant() + "-mimeapps.list"));
            files.Add(Path.Combine(configDir, "mimeapps.list"));
        }
        foreach (var dataDir in _xdg.DataSearchPath)
        {
            foreach (var desktop in _desktops)
                files.Add(Path.Combine(dataDir, "applications", desktop.ToLowerInvariant() + "-mimeapps.list"));
            files.Add(Path.Combine(dataDir, "applications", "mimeapps.list"));
        }

        lists.AddRange(files.Where(File.Exists).Select(f => new MimeAppsFile(IniFile.Load(f))));
        return lists;
    }
}
