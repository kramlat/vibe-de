using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace Vde.Shell.Controls;

// Draws a SettingsPage: one row per setting, a control for each kind, written to the registry as it changes.
// A value the setting does not allow is refused and the control goes back to what is stored.
public sealed class SettingsPageView : UserControl
{
    private readonly IHive _hive;
    private readonly SettingsPage _page;
    private readonly Dictionary<string, IReadOnlyList<SettingChoice>> _choices;
    private readonly List<(SettingBinding binding, Control control)> _rows = new();
    private bool _updating;

    // choices supplies options the page cannot know itself (the installed themes), by setting id.
    public SettingsPageView(SettingsPage page, IHive hive, IReadOnlyDictionary<string, IReadOnlyList<SettingChoice>>? choices = null)
    {
        _page = page;
        _hive = hive;
        _choices = choices is null ? new() : new(choices);

        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = page.Title, FontSize = 24, FontWeight = FontWeight.SemiBold });
        foreach (var setting in page.Settings)
        {
            var binding = new SettingBinding(hive, setting);
            var control = MakeControl(binding);
            _rows.Add((binding, control));

            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = setting.Label });
            row.Children.Add(control);
            if (setting.Help is { } help)
                row.Children.Add(new TextBlock { Text = help, FontSize = 12, Foreground = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray) });
            panel.Children.Add(row);
        }
        Content = new ScrollViewer { Content = panel };
        hive.Changed += OnHiveChanged;
        Refresh();
    }

    public string PageId => _page.Id;

    // The control showing a setting, for hosts and tests.
    public Control ControlFor(string settingId) => _rows.Single(r => r.binding.Definition.Id == settingId).control;

    // Re-reads every setting (also done when the registry changes behind our back, e.g. from a script).
    public void Refresh()
    {
        _updating = true;
        try
        {
            foreach (var (binding, control) in _rows)
                Show(binding, control, binding.Read());
        }
        finally { _updating = false; }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _hive.Changed -= OnHiveChanged;
    }

    private void OnHiveChanged(object? sender, RegistryChange change)
    {
        if (_page.Settings.Any(s => change.IsUnder(s.KeyPath)))
            Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    private Control MakeControl(SettingBinding binding)
    {
        var d = binding.Definition;
        switch (d.Kind)
        {
            case SettingKind.Toggle:
                var toggle = new ToggleSwitch();
                toggle.IsCheckedChanged += (_, _) => Apply(binding, toggle.IsChecked == true);
                return toggle;
            case SettingKind.Choice:
                var combo = new ComboBox { MinWidth = 220 };
                combo.ItemsSource = (_choices.TryGetValue(d.Id, out var supplied) ? supplied : d.Choices).Select(c => c.Label).ToList();
                combo.SelectionChanged += (_, _) =>
                {
                    var options = _choices.TryGetValue(d.Id, out var s) ? s : d.Choices;
                    if (combo.SelectedIndex >= 0 && combo.SelectedIndex < options.Count)
                        Apply(binding, options[combo.SelectedIndex].Value);
                };
                return combo;
            case SettingKind.Number:
                var number = new NumericUpDown { Minimum = d.Min, Maximum = Math.Min(d.Max, 1_000_000_000), Increment = 100, MinWidth = 160, FormatString = "0" };
                number.ValueChanged += (_, _) => { if (number.Value is { } v) Apply(binding, (long)v); };
                return number;
            default:
                var text = new TextBox { MinWidth = 220 };
                text.LostFocus += (_, _) => Apply(binding, text.Text ?? "");
                return text;
        }
    }

    private void Apply(SettingBinding binding, object value)
    {
        if (_updating)
            return;
        if (!binding.Write(value))
            Refresh();   // not allowed: show what is really stored
    }

    private void Show(SettingBinding binding, Control control, object? value)
    {
        switch (control)
        {
            case ToggleSwitch toggle: toggle.IsChecked = value is true; break;
            case NumericUpDown number: number.Value = value is long l ? l : null; break;
            case ComboBox combo:
                var options = _choices.TryGetValue(binding.Definition.Id, out var s) ? s : binding.Definition.Choices;
                combo.SelectedIndex = options.ToList().FindIndex(c => c.Value == value as string);
                break;
            case TextBox text: text.Text = value as string ?? ""; break;
        }
    }
}
