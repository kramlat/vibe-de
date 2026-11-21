using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace Vde.Shell.Controls;

// Personalization > Fonts, laid out like Windows 11's page: a box to drop font files on (with an "Install Locally" check
// box in place of the store link: on installs for this user, off for every user), then every installed font as a tile that
// shows a sample in that font, with a search box and a language filter.
public sealed class FontsPageView : UserControl
{
    public const string PageId = "fonts";
    private const int FirstBatch = 60;

    private static readonly string[] Samples =
    [
        "Splendid fireworks erupted over the sky.", "The sound of ocean waves calms my soul.", "The aroma of baking bread fills the air.",
        "A campfire crackles as the children gather.", "The fanfare of birds announces the dawn.", "Your presence might be invaluable.",
        "Melodic rain bounces off the roof top.", "Over the horizon comes the break of dawn.", "The starry night brightens our dreams.",
    ];

    private readonly IHive _hive;
    private readonly Func<IReadOnlyList<string>, bool, Task<string>> _install;
    private readonly Func<IReadOnlyList<FontFamilyInfo>> _load;
    private readonly CheckBox _local = new() { Tag = "InstallLocally", Content = "Install Locally" };
    private readonly TextBlock _status = new() { FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _search = new() { PlaceholderText = "Type here to search.", Width = 440, Tag = "search" };
    private readonly ComboBox _language = new() { MinWidth = 180 };
    private readonly WrapPanel _tiles = new();
    private IReadOnlyList<FontFamilyInfo> _all = [];
    private IReadOnlyList<string> _languageTags = [];
    private bool _updating;
    private int _generation;

    // install gets the dropped files and whether to install for this user only, and says what happened; without one the
    // fonts go into this user's own folder.
    public FontsPageView(IHive hive, Func<IReadOnlyList<string>, bool, Task<string>>? install = null, Func<IReadOnlyList<FontFamilyInfo>>? load = null)
    {
        _hive = hive;
        _install = install ?? InstallHereAsync;
        _load = load ?? FontCatalog.Load;

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(24) };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 14),
            Children =
            {
                new TextBlock { Text = "Personalization", FontSize = 24, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "›", FontSize = 20, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "Fonts", FontSize = 24, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        panel.Children.Add(new TextBlock { Text = "Add fonts", FontSize = 18, FontWeight = FontWeight.SemiBold });

        var secondary = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray);
        var zone = new Border
        {
            Tag = "dropzone", Width = 480, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16),
            BorderBrush = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray), BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = "Drag and drop to install" },
                    new TextBlock { Text = "Drag font files from File Explorer or your desktop and drop them here to install fonts on your device.", FontSize = 12, Foreground = secondary, TextWrapping = TextWrapping.Wrap },
                },
            },
        };
        DragDrop.SetAllowDrop(zone, true);
        zone.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        zone.AddHandler(DragDrop.DropEvent, async (_, e) => { e.Handled = true; await InstallAsync(DroppedPaths(e)); });
        panel.Children.Add(zone);

        // the check box that stands where Windows has its store link
        _local.IsCheckedChanged += (_, _) => { if (!_updating) FontInstaller.SetInstallLocally(_hive, _local.IsChecked == true); };
        panel.Children.Add(_local);
        panel.Children.Add(new TextBlock { Text = "Installs the fonts for you only. Clear it to install them for everyone who uses this computer (asks for your password).", FontSize = 12, Foreground = secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 0) });
        panel.Children.Add(_status);

        panel.Children.Add(new TextBlock { Text = "Available fonts", FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 18, 0, 0) });
        _search.HorizontalAlignment = HorizontalAlignment.Left;
        _search.TextChanged += (_, _) => Rebuild();
        panel.Children.Add(_search);
        var filter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 8) };
        filter.Children.Add(new TextBlock { Text = "Filter by:", VerticalAlignment = VerticalAlignment.Center, Foreground = secondary });
        filter.Children.Add(_language);
        panel.Children.Add(filter);
        panel.Children.Add(_tiles);

        Content = new ScrollViewer { Content = panel };
        _language.SelectionChanged += (_, _) => { if (!_updating) Rebuild(); };
        _hive.Changed += OnHiveChanged;
        Reload();
    }

    public CheckBox InstallLocallyBox => _local;
    public ComboBox LanguageBox => _language;
    public string SearchText { get => _search.Text ?? ""; set { _search.Text = value; Rebuild(); } }
    public string Status => _status.IsVisible ? _status.Text ?? "" : "";

    // The families shown under the current search and language.
    public IReadOnlyList<FontFamilyInfo> Shown { get; private set; } = [];
    public IReadOnlyList<Button> Tiles => _tiles.Children.OfType<Button>().ToList();

    // Reads the installed fonts again (after installing, or when the page opens).
    public void Reload()
    {
        _all = _load();
        _languageTags = _all.SelectMany(f => f.Languages).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(tag => (Tag: tag, Name: FontCatalog.LanguageName(tag)))
            .Where(l => !string.Equals(l.Tag, l.Name, StringComparison.OrdinalIgnoreCase))   // tags the system can name
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).Select(l => l.Tag).ToList();
        _updating = true;
        try
        {
            var chosen = _language.SelectedIndex > 0 && _language.SelectedIndex <= _languageTags.Count ? _languageTags[_language.SelectedIndex - 1] : null;
            _language.ItemsSource = new[] { "All languages" }.Concat(_languageTags.Select(FontCatalog.LanguageName)).ToList();
            _language.SelectedIndex = chosen is null ? 0 : Math.Max(0, _languageTags.ToList().IndexOf(chosen) + 1);
            _local.IsChecked = FontInstaller.InstallLocally(_hive);
        }
        finally { _updating = false; }
        Rebuild();
    }

    // Installs the files (as if they had been dropped); says what happened under the drop box.
    public async Task InstallAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        string message;
        try { message = await _install(paths, _local.IsChecked == true); }
        catch (OperationCanceledException) { message = "Installing was cancelled."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ElevationDeniedException)
        { message = "Could not install: " + e.Message; }
        _status.Text = message;
        _status.IsVisible = true;
        Reload();
    }

    private Task<string> InstallHereAsync(IReadOnlyList<string> paths, bool local)
    {
        if (!local) return Task.FromResult("Installing for all users needs the shell's password prompt; this window has none.");
        var dir = FontInstaller.LocalDirectory();
        var result = FontInstaller.InstallInto(paths, dir);
        if (result.Installed.Count > 0) FontInstaller.RefreshCache(dir);
        return Task.FromResult(FontInstallText(result));
    }

    public static string FontInstallText(FontInstallResult result)
        => result.Installed.Count == 0 ? "None of those files is a font (.ttf, .otf, .ttc or .otc)."
         : $"Installed {result.Installed.Count} font file{(result.Installed.Count == 1 ? "" : "s")}." + (result.Skipped.Count > 0 ? $" {result.Skipped.Count} skipped: not a font." : "");

    private static IReadOnlyList<string> DroppedPaths(DragEventArgs e)
    {
        var formats = new Dictionary<string, byte[]>();
        foreach (var item in e.DataTransfer.Items.Select(i => (IDataTransferItem)i))
            foreach (var format in new[] { FileTransfer.UriList, FileTransfer.GnomeCopiedFiles })
                if (item.Contains(DataFormat.CreateBytesPlatformFormat(format)) && item.TryGetValue(DataFormat.CreateBytesPlatformFormat(format)) is { } bytes)
                    formats[format] = bytes;
        return FileTransfer.FromFormats(formats)?.Paths ?? [];
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _hive.Changed -= OnHiveChanged;
    }

    private void OnHiveChanged(object? sender, RegistryChange change)
    {
        if (change.IsUnder(FontInstaller.SettingsKey))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { _updating = true; try { _local.IsChecked = FontInstaller.InstallLocally(_hive); } finally { _updating = false; } });
    }

    // The tiles for the families that match; the first screenful at once, the rest in the background so the page opens at once.
    private void Rebuild()
    {
        var text = SearchText.Trim();
        var tag = _language.SelectedIndex > 0 && _language.SelectedIndex <= _languageTags.Count ? _languageTags[_language.SelectedIndex - 1] : null;
        Shown = _all.Where(f => (text.Length == 0 || f.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                             && (tag is null || f.Languages.Contains(tag))).ToList();
        _tiles.Children.Clear();
        var generation = ++_generation;
        var list = Shown;
        void Add(int from, int to) { for (var i = from; i < Math.Min(to, list.Count); i++) _tiles.Children.Add(Tile(list[i], i)); }
        Add(0, FirstBatch);
        for (var start = FirstBatch; start < list.Count; start += FirstBatch)
        {
            var s = start;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (generation == _generation) Add(s, s + FirstBatch); }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private Button Tile(FontFamilyInfo family, int index)
    {
        FontFamily face;
        try { face = new FontFamily(family.Name); }
        catch (ArgumentException) { face = FontFamily.Default; }
        var accent = ThemeTokens.Find(this, ThemeToken.Accent, Brushes.DodgerBlue);
        var secondary = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray);
        var button = new Button
        {
            Tag = family.Name, Width = 178, Height = 190, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto,Auto"),
                Children =
                {
                    new TextBlock { Text = Samples[index % Samples.Length], FontFamily = face, FontSize = 20, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 110 },
                    new TextBlock { Text = family.Name, Foreground = accent, FontSize = 12, TextWrapping = TextWrapping.Wrap, [Grid.RowProperty] = 1 },
                    new TextBlock { Text = family.FacesText, Foreground = secondary, FontSize = 12, [Grid.RowProperty] = 2 },
                },
            },
        };
        ToolTip.SetTip(button, family.Name);
        return button;
    }
}
