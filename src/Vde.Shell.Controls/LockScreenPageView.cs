using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace Vde.Shell.Controls;

// Personalization > Lock screen, laid out like Windows 11's page: a preview of the lock screen, how to
// personalize it (a picture, a slideshow of a folder, or a solid colour), the app whose status it shows, the
// sign-in screen toggle, and links to the related pages. Saved to HKCU\Software\Vde\LockScreen at once.
public sealed class LockScreenPageView : UserControl
{
    public const string PageId = "lockscreen";

    private static readonly (string Label, LockBackground Value)[] Modes =
        [("Picture", LockBackground.Picture), ("Slideshow", LockBackground.Slideshow), ("Solid color", LockBackground.Color)];

    private readonly IHive _hive;
    private readonly IHive? _defaults;
    private readonly IReadOnlyList<WallpaperInfo> _wallpapers;
    private readonly IReadOnlyList<SettingChoice> _statusApps;
    private readonly Func<Window?, Task<string?>>? _browse;
    private readonly LockScreenView _preview = new(0.28);
    private readonly Border _previewFrame = new() { Width = 320, Height = 180, CornerRadius = new CornerRadius(6), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1) };
    private readonly ComboBox _mode = new() { MinWidth = 180, ItemsSource = Modes.Select(m => m.Label).ToList() };
    private readonly WrapPanel _gallery = new();
    private readonly StackPanel _pictureBody = new() { Spacing = 8 };
    private readonly TextBox _folder = new() { PlaceholderText = "/path/to/pictures", MinWidth = 280 };
    private readonly StackPanel _folderBody = new() { Spacing = 8 };
    private readonly TextBox _color = new() { PlaceholderText = "#RRGGBB", Width = 120 };
    private readonly TextBlock _colorError = new() { FontSize = 12, IsVisible = false, Text = "Use the form #RRGGBB, for example #1E1E2E." };
    private readonly StackPanel _colorBody = new() { Spacing = 4 };
    private readonly ComboBox _status = new() { MinWidth = 180 };
    private readonly ToggleSwitch _signIn = new() { OnContent = "On", OffContent = "Off" };
    private readonly Button _expand = new() { Content = "\u2303", Tag = "expand", Padding = new Thickness(8, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly Border _details = new() { Padding = new Thickness(42, 6, 14, 12) };
    private Control _chooseRow = null!;
    private bool _expanded = true;
    private bool _updating;

    // statusApps are the apps that can show a status ("None" is added); browse picks a picture file.
    public LockScreenPageView(IHive hive, IReadOnlyList<WallpaperInfo> wallpapers, IReadOnlyList<SettingChoice>? statusApps = null,
        Func<Window?, Task<string?>>? browse = null, IHive? systemDefaults = null)
    {
        _hive = hive;
        _defaults = systemDefaults;
        _wallpapers = wallpapers;
        _browse = browse;
        _statusApps = [new SettingChoice("", "None"), .. statusApps ?? []];
        _status.ItemsSource = _statusApps.Select(a => a.Label).ToList();
        _status.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((label, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            if (label is { Length: > 0 } && label != "None")
                row.Children.Add(new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(3), Background = ThemeTokens.Find(this, ThemeToken.Accent, Brushes.DodgerBlue),
                    Child = new TextBlock { Text = label[..1], FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            return row;
        });

        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(24) };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 14),
            Children =
            {
                new TextBlock { Text = "Personalization", FontSize = 24, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "\u203A", FontSize = 20, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "Lock screen", FontSize = 24, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        _previewFrame.Child = _preview;
        _previewFrame.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(_previewFrame);

        var browseButton = new Button { Content = "Browse photos", IsEnabled = browse is not null, Tag = "browse", MinWidth = 130, HorizontalContentAlignment = HorizontalAlignment.Center };
        browseButton.Click += async (_, _) => await BrowseAsync();
        _pictureBody.Children.Add(new TextBlock { Text = "Recent images", Margin = new Thickness(0, 0, 0, 2) });
        _pictureBody.Children.Add(_gallery);
        _chooseRow = Row("Choose a photo", null, browseButton);
        _folder.LostFocus += (_, _) => { if (!_updating) Change(s => s with { SlideshowFolder = string.IsNullOrWhiteSpace(_folder.Text) ? null : _folder.Text!.Trim() }); };
        _folderBody.Children.Add(new TextBlock { Text = "Choose a folder of pictures to cycle through", FontSize = 12, Opacity = 0.7 });
        _folderBody.Children.Add(_folder);
        _color.TextChanged += (_, _) => { if (!_updating && (_color.Text ?? "").Length == 7) SetColor(_color.Text!); };
        _colorBody.Children.Add(_color);
        _colorBody.Children.Add(_colorError);

        _expand.Click += (_, _) => { _expanded = !_expanded; _expand.Content = _expanded ? "\u2303" : "\u2304"; Reload(); };
        var modeControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _mode, _expand } };
        panel.Children.Add(Row("Personalize your lock screen", null, modeControls));
        _details.Child = new StackPanel { Spacing = 8, Children = { _pictureBody, _folderBody, _colorBody } };
        panel.Children.Add(_details);
        panel.Children.Add(_chooseRow);
        panel.Children.Add(Row("Lock screen status", "Choose an app to show detailed status on the lock screen", _status));
        panel.Children.Add(Row("Show the lock screen background picture on the sign-in screen", null, _signIn));

        panel.Children.Add(new TextBlock { Text = "Related settings", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 22, 0, 6) });
        panel.Children.Add(Link("Screen timeout", "power"));
        panel.Children.Add(Link("Screen saver", "screensaver", external: true));
        Content = new ScrollViewer { Content = panel };

        _mode.SelectionChanged += (_, _) => { if (!_updating && _mode.SelectedIndex >= 0) Change(s => s with { Background = Modes[_mode.SelectedIndex].Value }); };
        _status.SelectionChanged += (_, _) => { if (!_updating && _status.SelectedIndex >= 0) Change(s => s with { StatusApp = _statusApps[_status.SelectedIndex].Value is { Length: > 0 } id ? id : null }); };
        _signIn.IsCheckedChanged += (_, _) => { if (!_updating) Change(s => s with { ShowOnSignIn = _signIn.IsChecked == true }); };
        _hive.Changed += OnHiveChanged;
        Reload();
    }

    // Asks the host to open another settings page (the id of the page, e.g. "power").
    public event EventHandler<string>? OpenPage;

    public LockScreenSettings Settings => LockScreenSettingsStore.Load(_hive);
    public LockScreenView Preview => _preview;
    public ComboBox ModeBox => _mode;
    public ComboBox StatusBox => _status;
    public ToggleSwitch SignInToggle => _signIn;
    // The picture tiles in the gallery: for tests and screen readers.
    public IReadOnlyList<Button> Tiles => _gallery.Children.OfType<Button>().ToList();
    public string? ColorError => _colorError.IsVisible ? _colorError.Text : null;
    public bool PictureChoicesVisible => _pictureBody.IsVisible;
    public bool FolderChoiceVisible => _folderBody.IsVisible;
    public bool ColorChoiceVisible => _colorBody.IsVisible;

    public void SelectPicture(string? path) => Change(s => s with { Picture = path });
    public void SetMode(LockBackground mode) => Change(s => s with { Background = mode });

    public bool SetColor(string text)
    {
        var ok = System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$");
        _colorError.IsVisible = !ok;
        if (ok) Change(s => s with { Color = text.ToUpperInvariant() });
        return ok;
    }

    public async Task BrowseAsync()
    {
        if (_browse is null) return;
        if (await _browse(TopLevel.GetTopLevel(this) as Window) is { } path)
            SelectPicture(path);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _hive.Changed -= OnHiveChanged;
    }

    private void OnHiveChanged(object? sender, RegistryChange change)
    {
        if (change.IsUnder(LockScreenSettingsStore.KeyPath) || change.IsUnder(DesktopSettingsStore.KeyPath))
            Avalonia.Threading.Dispatcher.UIThread.Post(Reload);
    }

    private void Change(Func<LockScreenSettings, LockScreenSettings> edit)
    {
        LockScreenSettingsStore.Save(_hive, edit(LockScreenSettingsStore.Load(_hive)));
        Reload();
    }

    private void Reload()
    {
        var s = LockScreenSettingsStore.Load(_hive);
        var desktop = DesktopSettingsStore.Load(_hive, _defaults);
        _updating = true;
        try
        {
            _mode.SelectedIndex = Array.FindIndex(Modes, m => m.Value == s.Background);
            _status.SelectedIndex = Math.Max(0, _statusApps.ToList().FindIndex(a => a.Value == (s.StatusApp ?? "")));
            _signIn.IsChecked = s.ShowOnSignIn;
            _details.IsVisible = _expanded;
            _chooseRow.IsVisible = _expanded && s.Background == LockBackground.Picture;
            _pictureBody.IsVisible = s.Background == LockBackground.Picture;
            _folderBody.IsVisible = s.Background == LockBackground.Slideshow;
            _colorBody.IsVisible = s.Background == LockBackground.Color;
            if (!_folder.IsFocused) _folder.Text = s.SlideshowFolder ?? "";
            if (!_color.IsFocused) _color.Text = s.Color;
        }
        finally { _updating = false; }

        _previewFrame.BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.Gray);
        _preview.Apply(s, desktop, s.StatusApp is { } app ? _statusApps.FirstOrDefault(a => a.Value == app)?.Label : null);
        RebuildGallery(s, desktop);
    }

    private void RebuildGallery(LockScreenSettings current, DesktopSettings desktop)
    {
        _gallery.Children.Clear();
        var chosen = current.Picture ?? desktop.Wallpaper;
        var shown = _wallpapers.ToList();
        if (current.Picture is { } own && shown.All(w => w.Path != own))
            shown.Insert(0, new WallpaperInfo(Path.GetFileNameWithoutExtension(own), own));   // a picture from Browse stays visible
        foreach (var w in shown)
        {
            var picture = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = ThemeTokens.Find(this, ThemeToken.Track, Brushes.DimGray) };
            try { using var stream = File.OpenRead(w.Path); picture.Child = new Image { Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 288), Stretch = Stretch.UniformToFill }; }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException) { }
            var path = w.Path;
            var button = new Button
            {
                Tag = w.Path, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(4), Background = Brushes.Transparent, BorderThickness = new Thickness(2),
                BorderBrush = path == chosen && current.Picture is not null ? ThemeTokens.Find(this, ThemeToken.Accent, Brushes.DodgerBlue) : Brushes.Transparent,
                Content = picture,
            };
            ToolTip.SetTip(button, w.Name);
            button.Click += (_, _) => SelectPicture(path);
            _gallery.Children.Add(button);
        }
    }

    private Control Link(string title, string pageId, bool external = false)
    {
        var button = new Button
        {
            Tag = "link:" + pageId, Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { new TextBlock { Text = title }, new TextBlock { Text = external ? "\u2197" : "\u203A", Opacity = 0.6, [Grid.ColumnProperty] = 1 } } },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 16), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
            Background = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.Transparent), Margin = new Thickness(0, 0, 0, 2),
        };
        button.Click += (_, _) => OpenPage?.Invoke(this, pageId);
        return button;
    }

    private Control Row(string title, string? help, Control control)
    {
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        texts.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (help is not null)
            texts.Children.Add(new TextBlock { Text = help, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(texts);
        control.SetValue(Grid.ColumnProperty, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return new Border { Padding = new Thickness(14, 12), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(4), Background = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.Transparent), Child = grid };
    }
}
