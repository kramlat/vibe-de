using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Controls;

namespace Vde.Shell.ComDlg;

// The "Open with" chooser. Result is the chosen application, or null when cancelled.
public sealed class OpenWithWindow : Window
{
    private readonly OpenWithModel _model;
    private readonly TextBox _search = new() { PlaceholderText = "Search applications" };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly CheckBox _always;
    private readonly Button _ok;
    private readonly List<Button> _rows = new();

    public OpenWithWindow(OpenWithModel model, string description)
    {
        _model = model;
        Title = "Open with";
        Width = 420; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _always = new CheckBox { Content = $"Always use this app to open {description}" };
        _always.IsCheckedChanged += (_, _) => _model.Always = _always.IsChecked == true;
        _ok = DialogHost.Button("Open", () => Accept(), isDefault: true);
        _ok.IsEnabled = false;
        var cancel = DialogHost.Button("Cancel", () => { Result = null; Close(); }, isCancel: true);
        _search.TextChanged += (_, _) => Search(_search.Text ?? "");

        var footer = new StackPanel { Spacing = 10, Margin = new Thickness(0, 10, 0, 0), Children = { _always, DialogHost.ButtonRow(_ok, cancel) } };
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(_search, Dock.Top);
        root.Children.Add(footer);
        root.Children.Add(_search);
        root.Children.Add(new ScrollViewer { Content = _list, Margin = new Thickness(0, 10, 0, 0) });
        Content = root;
        Rebuild();
    }

    public DesktopEntry? Result { get; private set; }
    public OpenWithModel Model => _model;
    public IReadOnlyList<string> Names => _rows.Select(r => (string)r.Tag!).ToList();
    public bool OpenEnabled => _ok.IsEnabled;

    public void Search(string text)
    {
        _model.Filter = text;
        Rebuild();
    }

    public void Choose(DesktopEntry app)
    {
        _model.Selected = app;
        _ok.IsEnabled = true;
        foreach (var row in _rows)
            row.Opacity = (string)row.Tag! == app.Name ? 1 : 0.85;
    }

    public void SetAlways(bool on) => _always.IsChecked = on;

    public void Accept()
    {
        if (!_model.CanOpen) return;
        Result = _model.Commit();
        Close();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _rows.Clear();
        var recommended = _model.Recommended;
        var others = _model.Others;
        if (recommended.Count > 0)
            _list.Children.Add(Heading("Recommended"));
        foreach (var app in recommended) AddRow(app);
        if (others.Count > 0)
            _list.Children.Add(Heading("Other applications"));
        foreach (var app in others) AddRow(app);
        if (recommended.Count == 0 && others.Count == 0)
            _list.Children.Add(new TextBlock { Text = "No application matches.", Opacity = 0.7, Margin = new Thickness(4) });
    }

    private void AddRow(DesktopEntry app)
    {
        var button = new Button
        {
            Tag = app.Name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Avalonia.Media.Brushes.Transparent,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new ShellIcon { IconName = app.Icon ?? "application-x-executable", IconSize = 24 }, new TextBlock { Text = app.Name, VerticalAlignment = VerticalAlignment.Center } } },
        };
        button.Click += (_, _) => Choose(app);
        button.DoubleTapped += (_, _) => { Choose(app); Accept(); };
        _rows.Add(button);
        _list.Children.Add(button);
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(4, 8, 0, 2) };
}
