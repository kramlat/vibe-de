using System.Text.RegularExpressions;
using Vibe.Registry;

namespace Vde.Shell;

public sealed record KeyboardDevice(string Name, uint Bus, uint Vendor, uint Product, bool IsApple);

// Finds the keyboards attached, from the kernel's list of input devices. Only real keyboards count: a device
// has to have a keyboard handler, so an Apple trackpad or mouse does not make this an "Apple keyboard" machine.
public static partial class KeyboardDetector
{
    private const uint AppleUsb = 0x05ac, AppleBluetooth = 0x004c;

    [GeneratedRegex(@"\bApple\b", RegexOptions.IgnoreCase)]
    private static partial Regex AppleName();

    public static IReadOnlyList<KeyboardDevice> Detect(string devicesFile = "/proc/bus/input/devices")
        => File.Exists(devicesFile) ? Parse(File.ReadAllText(devicesFile)) : [];

    public static IReadOnlyList<KeyboardDevice> Parse(string text)
    {
        var keyboards = new List<KeyboardDevice>();
        foreach (var block in text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            string name = "";
            uint bus = 0, vendor = 0, product = 0;
            bool keyboard = false;
            foreach (var raw in block.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("I:"))
                {
                    bus = Hex(line, "Bus="); vendor = Hex(line, "Vendor="); product = Hex(line, "Product=");
                }
                else if (line.StartsWith("N: Name="))
                    name = line["N: Name=".Length..].Trim('"');
                else if (line.StartsWith("H: Handlers="))
                    keyboard = line["H: Handlers=".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("kbd");
            }
            if (keyboard)
                keyboards.Add(new KeyboardDevice(name, bus, vendor, product,
                    vendor is AppleUsb or AppleBluetooth || AppleName().IsMatch(name)));
        }
        return keyboards;
    }

    private static uint Hex(string line, string key)
    {
        int at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return 0;
        var rest = line[(at + key.Length)..].Split(' ')[0];
        return uint.TryParse(rest, System.Globalization.NumberStyles.HexNumber, null, out var value) ? value : 0;
    }
}

public enum ShortcutStyle { Pc, Mac }
public enum ShortcutPreference { Auto, Mac, Pc }

// Which style of shortcuts to show and accept: the user's choice (HKCU\Software\Vde\Input\ShortcutStyle:
// Auto, Mac or Pc), where Auto means Mac only while an Apple keyboard is attached and PC otherwise.
public sealed class ShortcutStyleService
{
    public const string KeyPath = @"Software\Vde\Input";

    private readonly IHive _hive;
    private readonly Func<IReadOnlyList<KeyboardDevice>> _detect;
    private ShortcutStyle _style;

    public ShortcutStyleService(IHive hive, Func<IReadOnlyList<KeyboardDevice>>? detect = null)
    {
        _hive = hive;
        _detect = detect ?? (() => KeyboardDetector.Detect());
        _style = Compute();
        hive.Changed += (_, c) => { if (c.IsUnder(KeyPath)) Refresh(); };
    }

    public ShortcutStyle Style => _style;
    public event EventHandler? Changed;

    public ShortcutPreference Preference
        => _hive.GetValue(KeyPath, "ShortcutStyle") is { Kind: RegistryValueKind.String, Data: string s } && Enum.TryParse<ShortcutPreference>(s, true, out var p) && Enum.IsDefined(p)
            ? p : ShortcutPreference.Auto;

    public bool AppleKeyboardPresent => _detect().Any(k => k.IsApple);

    // What a key press means. Normally in the current style; but on a machine with an Apple keyboard, pressing
    // Command (without Ctrl) is Mac input whatever the label style is, so Command+C copies even when the
    // user forced PC labels or is typing on the Apple keyboard of a machine that also has a PC one.
    public ShortcutAction? ActionFor(KeyPress press)
    {
        if (ShortcutMap.Match(press, _style) is { } action)
            return action;
        if (_style == ShortcutStyle.Pc && press.Meta && !press.Ctrl && AppleKeyboardPresent)
            return ShortcutMap.Match(press, ShortcutStyle.Mac);
        return null;
    }

    // Re-reads the preference and the attached keyboards (call it when one is plugged in or removed).
    public void Refresh()
    {
        var style = Compute();
        if (style == _style) return;
        _style = style;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Looks for keyboard changes now and then, since plugging one in has no registry event.
    public IDisposable Poll(TimeSpan every)
    {
        var timer = new Timer(_ => Refresh(), null, every, every);
        return timer;
    }

    private ShortcutStyle Compute() => Preference switch
    {
        ShortcutPreference.Mac => ShortcutStyle.Mac,
        ShortcutPreference.Pc => ShortcutStyle.Pc,
        _ => AppleKeyboardPresent ? ShortcutStyle.Mac : ShortcutStyle.Pc,
    };
}
