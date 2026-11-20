using Vibe.Registry;

namespace Vde.Shell;

public enum LockBackground { Picture, Slideshow, Color }

// What the lock screen shows, in HKCU\Software\Vde\LockScreen. A Picture of null means "the desktop wallpaper".
// StatusApp is the id of the app whose detailed status the lock screen shows (null: none).
public sealed record LockScreenSettings(
    LockBackground Background = LockBackground.Picture,
    string? Picture = null,
    string? SlideshowFolder = null,
    string Color = "#1E1E2E",
    string? StatusApp = null,
    bool ShowOnSignIn = true);

public static class LockScreenSettingsStore
{
    public const string KeyPath = @"Software\Vde\LockScreen";

    public static LockScreenSettings Load(IHive hive)
    {
        string? Text(string name) => hive.GetValue(KeyPath, name) is { Kind: RegistryValueKind.String, Data: string { Length: > 0 } s } ? s : null;
        var defaults = new LockScreenSettings();
        return new LockScreenSettings(
            Enum.TryParse<LockBackground>(Text("Background"), true, out var mode) && Enum.IsDefined(mode) ? mode : defaults.Background,
            Text("Picture"),
            Text("SlideshowFolder"),
            Text("Color") is { } c && System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$") ? c : defaults.Color,
            Text("StatusApp"),
            RegistryBool.Get(hive, KeyPath, "ShowOnSignIn", defaults.ShowOnSignIn));
    }

    public static void Save(IHive hive, LockScreenSettings settings)
    {
        hive.SetValue(KeyPath, "Background", RegistryValueKind.String, settings.Background.ToString());
        hive.SetValue(KeyPath, "Picture", RegistryValueKind.String, settings.Picture ?? "");
        hive.SetValue(KeyPath, "SlideshowFolder", RegistryValueKind.String, settings.SlideshowFolder ?? "");
        hive.SetValue(KeyPath, "Color", RegistryValueKind.String, settings.Color);
        hive.SetValue(KeyPath, "StatusApp", RegistryValueKind.String, settings.StatusApp ?? "");
        RegistryBool.Set(hive, KeyPath, "ShowOnSignIn", settings.ShowOnSignIn);
    }

    // The picture to draw now: the chosen one, else the desktop's wallpaper; for a slideshow the folder's
    // images in name order, one per `index`. Null for a solid colour or when nothing usable exists.
    public static string? PictureToShow(LockScreenSettings settings, DesktopSettings desktop, int index = 0)
    {
        switch (settings.Background)
        {
            case LockBackground.Color:
                return null;
            case LockBackground.Slideshow when settings.SlideshowFolder is { } folder && Directory.Exists(folder):
                var images = Directory.EnumerateFiles(folder)
                    .Where(f => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                return images.Count == 0 ? null : images[Math.Abs(index) % images.Count];
            default:
                var path = settings.Picture ?? desktop.Wallpaper;
                return path is not null && File.Exists(path) ? path : null;
        }
    }
}
