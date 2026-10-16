using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Components;
using Vde.Shell;
using Vde.Shell.ComDlg;
using Vde.Shell.Controls;
using Vibe.Registry;

[assembly: VdeInstaller(typeof(VExplore.VExploreInstaller))]

namespace VExplore;

// VExplore's own property sheet handler: the tabs the shell adds to every Properties window, in the same way
// installed software would. It is a registered component (see VExploreInstaller) and is also built into the
// shell, so the tabs are there on a plain build and the registry entry is never loaded a second time.
//
//   Permissions   POSIX owner/group/rwx editor, for files and folders
//   Checksums     SHA-256, SHA-1 and MD5 of a file, with a box to compare against a published value
//   Customize     a folder's icon (kept in a KDE-style .directory file, so Dolphin agrees)
[VdeComponent(PropertyPages.Clsid, ProgId = "VExplore.PropertySheets", Description = "Vibe Explorer property pages",
    ThreadingModel = ThreadingModel.Apartment)]
public sealed class PropertyPages : IPropertyPageProvider
{
    public const string Clsid = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B7001}";

    private readonly PosixPermissionsPages _permissions;

    // elevation is what "Take ownership" uses to ask for the rights it needs; without it the button is not offered.
    public PropertyPages() : this(null) { }   // the registry loads components through the empty constructor
    public PropertyPages(ElevationOptions? elevation) => _permissions = new PosixPermissionsPages(elevation: elevation);

    public IReadOnlyList<PropertyPageInfo> GetPages(PropertyTarget target)
    {
        var pages = new List<PropertyPageInfo>(_permissions.GetPages(target));
        if (target.IsSingle)
        {
            var path = target.Paths[0];
            if (Directory.Exists(path) && new FileInfo(path).LinkTarget is null)
                pages.Add(CustomizePage.Create(path));
            else if (File.Exists(path) && new FileInfo(path).LinkTarget is null)
                pages.Add(ChecksumPage.Create(path));
        }
        return pages;
    }
}

// What RegSvrVDE runs with /i: makes the shell's tabs appear for every file and folder.
public sealed class VExploreInstaller : IVdeInstaller
{
    public static readonly string[] Classes = ["*", "Directory"];

    public void Install(VdeInstallContext context)
    {
        ShellFolderSetup.Register(context.Registry, context.Installing);
        foreach (var cls in Classes)
        {
            var key = $@"{cls}\{PropertyHandlers.HandlersKey}\VExplore";
            if (context.Installing)
                context.Registry.SetValue(key, "", RegistryValueKind.String, PropertyPages.Clsid);
            else
                context.Registry.DeleteKey($@"{cls}\{PropertyHandlers.HandlersKey}\VExplore");
        }
    }
}

public sealed class ChecksumPage
{
    private readonly string _path;
    private readonly TextBox _sha256 = Hash(), _sha1 = Hash(), _md5 = Hash(), _expected = new() { Watermark = "Paste a checksum to compare" };
    private readonly TextBlock _status = new() { Opacity = 0.7 }, _match = new();
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 6 };

    private ChecksumPage(string path)
    {
        _path = path;
        _expected.TextChanged += (_, _) => Compare();
        Content = new StackPanel
        {
            Margin = new Thickness(14), Spacing = 6,
            Children =
            {
                _bar, _status,
                new TextBlock { Text = "SHA-256" }, _sha256, new TextBlock { Text = "SHA-1" }, _sha1, new TextBlock { Text = "MD5" }, _md5,
                new Separator(), new TextBlock { Text = "Compare with:" }, _expected, _match,
            },
        };
        ComputeTask = ComputeAsync();
    }

    public Control Content { get; }
    public Task ComputeTask { get; }
    public string Sha256 => _sha256.Text ?? "";
    public string Sha1 => _sha1.Text ?? "";
    public string Md5 => _md5.Text ?? "";
    public string Expected { get => _expected.Text ?? ""; set { _expected.Text = value; Compare(); } }   // compare now: TextChanged can arrive later than the caller expects
    public string MatchText => _match.Text ?? "";

    public static PropertyPageInfo Create(string path)
    {
        var page = new ChecksumPage(path);
        return new PropertyPageInfo("Checksums", () => page.Content, Order: 30, IsDirty: () => false, Tag: page);
    }

    private static TextBox Hash() => new() { IsReadOnly = true, FontFamily = new FontFamily("monospace"), FontSize = 12 };

    // Reads the file once and feeds all three algorithms; started on the UI thread, so the results land there.
    private async Task ComputeAsync()
    {
        _status.Text = "Calculating…";
        try
        {
            var progress = new Progress<double>(f => _bar.Value = f);
            var (sha256, sha1, md5) = await Task.Run(() => HashFile(_path, progress));
            _sha256.Text = sha256; _sha1.Text = sha1; _md5.Text = md5;
            _status.Text = "";
            _bar.Value = 1;
            Compare();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Text = "This file cannot be read: " + e.Message;
        }
    }

    internal static (string sha256, string sha1, string md5) HashFile(string path, IProgress<double>? progress = null)
    {
        using var s256 = SHA256.Create(); using var s1 = SHA1.Create(); using var m5 = MD5.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        var buffer = new byte[1 << 20];
        long total = Math.Max(1, stream.Length), done = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            s256.TransformBlock(buffer, 0, read, null, 0); s1.TransformBlock(buffer, 0, read, null, 0); m5.TransformBlock(buffer, 0, read, null, 0);
            progress?.Report((double)(done += read) / total);
        }
        s256.TransformFinalBlock([], 0, 0); s1.TransformFinalBlock([], 0, 0); m5.TransformFinalBlock([], 0, 0);
        return (Convert.ToHexString(s256.Hash!).ToLowerInvariant(), Convert.ToHexString(s1.Hash!).ToLowerInvariant(), Convert.ToHexString(m5.Hash!).ToLowerInvariant());
    }

    // The pasted value may have spaces or colons, or capitals, and may match any of the three.
    private void Compare()
    {
        var typed = new string(Expected.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (Expected.Trim().Length == 0 || Sha256.Length == 0) { _match.Text = ""; return; }
        var which = typed == Sha256 ? "SHA-256" : typed == Sha1 ? "SHA-1" : typed == Md5 ? "MD5" : null;
        _match.Text = which is null ? "Does not match." : $"Matches the {which}.";
        _match.Foreground = which is null ? Brushes.IndianRed : Brushes.SeaGreen;
    }
}

public sealed class CustomizePage
{
    private readonly string _folder;
    private readonly TextBox _icon = new() { Watermark = "Icon name, e.g. folder-music", MinWidth = 260 };
    private readonly ShellIcon _preview = new() { IconSize = 48 };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, IsVisible = false };
    private readonly string? _original;

    private CustomizePage(string folder)
    {
        _folder = folder;
        _original = FolderIcon.Read(folder);
        _icon.Text = _original ?? "";
        _icon.TextChanged += (_, _) => _preview.IconNames = _icon.Text is { Length: > 0 } t ? [t, "folder"] : ["folder"];
        _preview.IconNames = _original is { } o ? [o, "folder"] : ["folder"];
        var clear = new Button { Content = "Restore default" };
        clear.Click += (_, _) => _icon.Text = "";
        Content = new StackPanel
        {
            Margin = new Thickness(14), Spacing = 8,
            Children = { new TextBlock { Text = "Folder icon" }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _preview, new StackPanel { Spacing = 6, Children = { _icon, clear } } } }, _error,
                new TextBlock { Text = "Saved in this folder's .directory file, which KDE's Dolphin also reads.", FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap } },
        };
    }

    public Control Content { get; }
    public string Icon { get => _icon.Text ?? ""; set => _icon.Text = value; }
    public string Error => _error.IsVisible ? _error.Text ?? "" : "";
    public bool IsDirty => (Icon.Length == 0 ? null : Icon) != _original;

    public static PropertyPageInfo Create(string folder)
    {
        var page = new CustomizePage(folder);
        return new PropertyPageInfo("Customize", () => page.Content, page.Apply, 40, () => page.IsDirty, page);
    }

    public bool Apply()
    {
        try
        {
            FolderIcon.Write(_folder, Icon.Length == 0 ? null : Icon);
            _error.IsVisible = false;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _error.Text = e.Message;
            _error.IsVisible = true;
            return false;
        }
    }
}
