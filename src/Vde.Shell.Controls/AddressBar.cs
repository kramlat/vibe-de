using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Vde.Shell;

namespace Vde.Shell.Controls;

// The path above a folder view: clickable segments, which turn into a text box when you click beside them
// (or press Ctrl+L) so a path can be typed.
public sealed class AddressBar : UserControl
{
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _editor = new() { IsVisible = false };
    private string _path = "/";
    private string _home = "";

    public AddressBar()
    {
        var host = new Panel { Children = { _crumbs, _editor }, MinHeight = 28 };
        Content = host;
        Focusable = true;

        host.PointerPressed += (_, e) =>
        {
            if (!_editor.IsVisible && e.Source == host)
                BeginEdit();
        };
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndEdit(); e.Handled = true; }
        };
        _editor.LostFocus += (_, _) => EndEdit();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.Control) { BeginEdit(); e.Handled = true; }
        };
        Rebuild();
    }

    public string Path
    {
        get => _path;
        set { _path = value; Rebuild(); }
    }

    // Shown as "Home" in the segments, and what "~" means in typed text.
    public string Home
    {
        get => _home;
        set { _home = value; Rebuild(); }
    }

    public bool IsEditing => _editor.IsVisible;
    public string EditText { get => _editor.Text ?? ""; set => _editor.Text = value; }

    // A segment was clicked, or a path was typed and accepted: the host navigates there.
    public event EventHandler<string>? PathRequested;

    // Text was typed that is not a place (e.g. smb://...): the host may still want to handle it.
    public event EventHandler<string>? TextRejected;

    public void BeginEdit()
    {
        _editor.Text = _path;
        _crumbs.IsVisible = false;
        _editor.IsVisible = true;
        _editor.Focus();
        _editor.SelectAll();
    }

    public void Commit()
    {
        var text = _editor.Text ?? "";
        EndEdit();
        var target = AddressPath.Parse(text, _path, _home);
        if (target is null)
            TextRejected?.Invoke(this, text);
        else
            PathRequested?.Invoke(this, target);
    }

    private void EndEdit()
    {
        _editor.IsVisible = false;
        _crumbs.IsVisible = true;
    }

    private void Rebuild()
    {
        _crumbs.Children.Clear();
        foreach (var crumb in AddressPath.Crumbs(_path, _home.Length > 0 ? _home : null))
        {
            var button = new Button { Content = crumb.Name, Padding = new Thickness(6, 2), Tag = crumb.Path, Background = Avalonia.Media.Brushes.Transparent };
            button.Click += (_, _) => PathRequested?.Invoke(this, crumb.Path);
            _crumbs.Children.Add(button);
            if (crumb.Path != _path)
                _crumbs.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
        }
    }
}
