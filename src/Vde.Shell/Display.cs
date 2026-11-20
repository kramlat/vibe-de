using Vibe.Registry;

namespace Vde.Shell;

// Everything the Display settings panel records about the monitors: in HKCU\Software\Vde\Display.
// Per-display values are keyed by the display's 1-based number, which is the session's enumeration
// order with the primary first.
public sealed record DisplaySettings
{
    public const string KeyPath = @"Software\Vde\Display";

    // The multiple-displays mode: Extend, Duplicate or OnlyN ("show only on N").
    public string MultipleDisplays { get; init; } = "Extend";

    // Each display's top-left relative to the primary's, in logical units: "x,y;x,y" by display number.
    public IReadOnlyList<DisplaySlot> Slots { get; init; } = [];

    // One display's Scale (a percent, 100..), Orientation, Resolution and Brightness values.
    public IReadOnlyDictionary<int, DisplayConfig> Displays { get; init; } =
        new Dictionary<int, DisplayConfig>();

    public bool NightLight { get; init; }
}

public sealed record DisplaySlot(double X, double Y);

public sealed record DisplayConfig(string Scale = "", string Orientation = "", string Resolution = "", long Brightness = 80);

// Reads and writes those values; a value the display did not choose falls back to the runtime default.
public static class DisplaySettingsStore
{
    public static DisplaySettings Load(IHive hive)
    {
        string? Text(string name) => hive.GetValue(DisplaySettings.KeyPath, name) is { Kind: RegistryValueKind.String, Data: string { Length: > 0 } s } ? s : null;
        long? DWord(string name) => RegistryNumber.Get(hive, DisplaySettings.KeyPath, name);

        var slots = new List<DisplaySlot>();
        if (Text("Layout") is { } layout)
            foreach (var pair in layout.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (pair.Split(',') is [var xs, var ys] && double.TryParse(xs, out var x) && double.TryParse(ys, out var y))
                    slots.Add(new DisplaySlot(x, y));

        var displays = new Dictionary<int, DisplayConfig>();
        if (hive.KeyExists(DisplaySettings.KeyPath))
            foreach (var value in hive.GetValues(DisplaySettings.KeyPath))
                if (value.Name.StartsWith("Display") && value is { Kind: RegistryValueKind.String, Data: string { Length: > 0 } json }
                    && int.TryParse(value.Name["Display".Length..], out var index))
                {
                    var parts = json.Split(',', 4);
                    displays[index] = new DisplayConfig(
                        parts.Length > 0 ? parts[0] : "",
                        parts.Length > 1 ? parts[1] : "",
                        parts.Length > 2 ? parts[2] : "",
                        DWord($"Brightness{index}") is { } b ? b : new DisplayConfig().Brightness);
                }

        return new DisplaySettings
        {
            MultipleDisplays = Text("MultipleDisplays") is { } mode && System.Text.RegularExpressions.Regex.IsMatch(mode, "^(Extend|Duplicate|Only[0-9]+)$") ? mode : "Extend",
            Slots = slots,
            Displays = displays,
            NightLight = RegistryBool.Get(hive, DisplaySettings.KeyPath, "NightLight", false),
        };
    }

    public static void Save(IHive hive, DisplaySettings settings)
    {
        hive.SetValue(DisplaySettings.KeyPath, "Layout", RegistryValueKind.String,
            string.Join(";", settings.Slots.Select(s => $"{s.X:0},{s.Y:0}")));
        hive.SetValue(DisplaySettings.KeyPath, "MultipleDisplays", RegistryValueKind.String, settings.MultipleDisplays);
        RegistryBool.Set(hive, DisplaySettings.KeyPath, "NightLight", settings.NightLight);
        foreach (var (number, config) in settings.Displays)
        {
            hive.SetValue(DisplaySettings.KeyPath, $"Display{number}", RegistryValueKind.String,
                $"{config.Scale},{config.Orientation},{config.Resolution}");
            RegistryNumber.Set(hive, DisplaySettings.KeyPath, $"Brightness{number}", Math.Clamp(config.Brightness, 0, 100));
        }
    }

    // The choices the multiple-displays control offers for a given number of displays.
    public static IReadOnlyList<SettingChoice> ModeChoices(int displays)
    {
        var choices = new List<SettingChoice>
        {
            new("Extend", displays > 1 ? "Extend these displays" : "Extend the display"),
            new("Duplicate", displays > 1 ? "Duplicate these displays" : "Duplicate the display"),
        };
        for (var i = 1; i <= displays; i++)
            choices.Add(new($"Only{i}", $"Show only on {i}"));
        return choices;
    }

    // The standard scale steps; 100% is marked (Recommended) when that is what is running.
    public static IReadOnlyList<SettingChoice> ScaleChoices(double currentScaling)
        => new[] { 100, 125, 150, 175, 200 }
            .Select(p => new SettingChoice(p.ToString(), p == 100 && Math.Abs(currentScaling - 1.0) < 0.01
                ? "100% (Recommended)" : $"{p}%"))
            .ToList();

    public static IReadOnlyList<SettingChoice> OrientationChoices { get; } =
    [
        new("Landscape", "Landscape"),
        new("Portrait", "Portrait"),
        new("LandscapeFlipped", "Landscape (flipped)"),
        new("PortraitFlipped", "Portrait (flipped)"),
    ];

    // The resolutions offered for a display: the running one first, then the common ones that fit within it.
    public static IReadOnlyList<SettingChoice> ResolutionChoices(int currentWidth, int currentHeight)
    {
        var fit = new[] { (640, 480), (800, 600), (1024, 768), (1280, 720), (1280, 1024), (1366, 768),
            (1440, 900), (1600, 900), (1680, 1050), (1920, 1080), (2560, 1440), (3840, 2160) };
        var choices = new List<SettingChoice> { new($"{currentWidth}x{currentHeight}", $"{currentWidth} × {currentHeight}") };
        foreach (var (w, h) in fit)
            if (w <= currentWidth && h <= currentHeight && (w != currentWidth || h != currentHeight))
                choices.Add(new($"{w}x{h}", $"{w} × {h}"));
        return choices;
    }
}
