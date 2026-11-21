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
        if (page.Parent is { } parent)   // "Personalization › Taskbar"
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children =
                {
                    new TextBlock { Text = parent, FontSize = 24, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "\u203A", FontSize = 20, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = page.Title, FontSize = 24, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                },
            });
        else
            panel.Children.Add(new TextBlock { Text = page.Title, FontSize = 24, FontWeight = FontWeight.SemiBold });
        if (page.Sections is { } sections)
        {
            foreach (var section in sections)
                panel.Children.Add(Section(section));
            Content = new ScrollViewer { Content = panel };
            hive.Changed += OnHiveChanged;
            Refresh();
            return;
        }
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

    // The sections' bodies by title, so a host or test can see which are open.
    public IReadOnlyDictionary<string, Control> SectionBodies => _bodies;
    private readonly Dictionary<string, Control> _bodies = new();

    private readonly List<(string Key, Action Rebuild)> _dynamicSections = new();

    // One row of a section: name and help (with a small picture when the setting has one) on the left, the control on the right;
    // a check box carries its own label.
    private Control SectionRow(SettingDefinition setting, Control control, IBrush secondary)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(40, 0, 16, 0), MinHeight = 56 };
        if (control is CheckBox box)
        {
            box.Content = setting.Label;
            row.Children.Add(box);
        }
        else
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            if (setting.IconName is { Length: > 0 } icon)
                line.Children.Add(new ShellIcon { IconNames = [icon, "application-x-executable"], IconSize = 20, VerticalAlignment = VerticalAlignment.Center });
            var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = setting.Label } } };
            if (setting.Help is { } help)
                texts.Children.Add(new TextBlock { Text = help, FontSize = 12, Foreground = secondary });
            line.Children.Add(texts);
            row.Children.Add(line);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
        }
        return new Border { Child = row, Padding = new Thickness(0, 6), BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.Gray), BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    // A Windows 11 section: a card with the title and a line of help, a chevron that folds it, and a row per setting
    // (name and help on the left, the control on the right).
    private Control Section(SettingsSection section)
    {
        var secondary = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray);
        var surface = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.Transparent);
        var body = new StackPanel { IsVisible = section.Expanded };
        var dynamicRows = new List<(SettingBinding binding, Control control)>();
        void Fill()
        {
            foreach (var row in dynamicRows) _rows.Remove(row);
            dynamicRows.Clear();
            body.Children.Clear();
            foreach (var setting in section.Dynamic is { } dynamic ? dynamic(_hive) : section.Settings)
            {
                var binding = new SettingBinding(_hive, setting);
                var control = MakeControl(binding);
                _rows.Add((binding, control));
                if (section.Dynamic is not null) dynamicRows.Add((binding, control));
                body.Children.Add(SectionRow(setting, control, secondary));
                Show(binding, control, binding.Read());
            }
        }
        Fill();
        if (section.Dynamic is not null && section.DynamicKey is { } key)
            _dynamicSections.Add((key, () => { _updating = true; try { Fill(); } finally { _updating = false; } }));
        _bodies[section.Title] = body;

        var chevron = new TextBlock { Text = section.Expanded ? "\u2303" : "\u2304", VerticalAlignment = VerticalAlignment.Center };
        var header = new Button
        {
            Tag = "section:" + section.Title, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(16, 12),
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel { Spacing = 2, Children = { new TextBlock { Text = section.Title }, new TextBlock { Text = section.Help, FontSize = 12, Foreground = secondary } } },
                    chevron,
                },
            },
        };
        Grid.SetColumn(chevron, 1);
        header.Click += (_, _) => { body.IsVisible = !body.IsVisible; chevron.Text = body.IsVisible ? "\u2303" : "\u2304"; };
        return new Border { Background = surface, CornerRadius = new CornerRadius(4), Child = new StackPanel { Children = { header, body } } };
    }

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
        if (_dynamicSections.Any(d => change.IsUnder(d.Key)))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { foreach (var d in _dynamicSections) d.Rebuild(); Refresh(); });
        else if (_page.Settings.Any(s => change.IsUnder(s.KeyPath)))
            Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    private Control MakeControl(SettingBinding binding)
    {
        var d = binding.Definition;
        switch (d.Kind)
        {
            case SettingKind.Toggle when d.AsCheckBox:
                var check = new CheckBox();
                check.IsCheckedChanged += (_, _) => Apply(binding, check.IsChecked == true);
                return check;
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
                var number = new NumericUpDown { Minimum = d.Min, Maximum = Math.Min(d.Max, 1_000_000_000), Increment = d.Step, MinWidth = 160, FormatString = "0" };
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
            case CheckBox check: check.IsChecked = value is true; break;
            case NumericUpDown number: number.Value = value is long l ? l : null; break;
            case ComboBox combo:
                var options = _choices.TryGetValue(binding.Definition.Id, out var s) ? s : binding.Definition.Choices;
                combo.SelectedIndex = options.ToList().FindIndex(c => c.Value == value as string);
                break;
            case TextBox text: text.Text = value as string ?? ""; break;
        }
    }
}
