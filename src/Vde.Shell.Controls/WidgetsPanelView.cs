using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Vde.Shell;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace Vde.Shell.Controls;

// The widgets panel, laid out like Windows 11's: the time and a web search box on top, widget cards in two columns, an
// "Add widgets" button and the headlines under them. The cards are VDE's own (WidgetCatalog): the weather from Open-Meteo, photos
// from this day in your Pictures, the machine's load and the headlines of the RSS feeds you chose.
// A widget the community made, ready to draw: an in-process component (ComponentCategories.Widget) or an app that sends a card.
public sealed record CommunityWidget(string Id, string Title, IReadOnlyList<string> Icons, Func<Control> Create);

public sealed class WidgetsPanelView : UserControl
{
    public const int PanelWidth = 640;

    private readonly WidgetSettings _settings;
    private readonly IHive _hive;
    private readonly WeatherService _weather;
    private readonly Fetch _fetch;
    private readonly Func<DateTime> _now;
    private readonly IReadOnlyList<string> _photoFolders;
    private readonly Func<IReadOnlyList<CommunityWidget>> _community;
    private readonly Action<string> _openUrl;
    private readonly Action<string> _openFile;
    private readonly Action _openSettings;
    private readonly ProcessSampler _sampler = new();
    private readonly TextBlock _clock = new() { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 22, FontWeight = FontWeight.SemiBold };
    private readonly TextBox _search = new() { PlaceholderText = "Search the web", Tag = "search" };
    private readonly StackPanel _left = new() { Spacing = 12 }, _right = new() { Spacing = 12 };
    private readonly StackPanel _stories = new() { Spacing = 8 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TextBlock? _cpu, _memory;
    private ProgressBar? _memoryBar;

    public WidgetsPanelView(IHive hive, Action<string> openUrl, Action<string> openFile, Action openSettings,
        Fetch? fetch = null, Func<DateTime>? now = null, IEnumerable<string>? photoFolders = null, Func<IReadOnlyList<CommunityWidget>>? community = null)
    {
        _community = community ?? (() => []);
        _hive = hive;
        _settings = new WidgetSettings(hive);
        _fetch = fetch ?? Http.GetAsync;
        _weather = new WeatherService(_fetch);
        _now = now ?? (() => DateTime.Now);
        _photoFolders = (photoFolders ?? [new KnownFolders(Vde.Freedesktop.XdgDirectories.Default).Get(KnownFolder.Pictures)]).ToList();
        _openUrl = openUrl; _openFile = openFile; _openSettings = openSettings;

        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && SearchText.Trim().Length > 0)
                _openUrl("https://duckduckgo.com/?q=" + Uri.EscapeDataString(SearchText.Trim()));   // an open search engine, not Bing
        };

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        columns.Children.Add(_left);
        Grid.SetColumn(_right, 1);
        columns.Children.Add(_right);
        var add = new Button { Content = "Add widgets", Tag = "add", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8) };
        add.Click += (_, _) => AddMenu(add);

        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(20, 16) };
        panel.Children.Add(_clock);
        panel.Children.Add(_search);
        panel.Children.Add(columns);
        panel.Children.Add(add);
        panel.Children.Add(Card("Top stories", ["news-subscribe", "internet-news-reader"], _stories, null, wide: true, tag: WidgetCatalog.News));
        Width = PanelWidth;
        Content = new ScrollViewer { Content = panel };
        _timer.Tick += (_, _) => Update();
        _hive.Changed += OnHiveChanged;
        Update();
        Rebuild();
    }

    public string SearchText { get => _search.Text ?? ""; set => _search.Text = value; }
    public string ClockText => _clock.Text ?? "";

    // The cards on show, by widget id, in layout order (the headlines are the full-width card under the others).
    public IReadOnlyList<string> CardIds => _left.Children.Concat(_right.Children).OfType<Border>().Select(b => (string)b.Tag!).ToList();
    public IReadOnlyList<string> Headlines => _stories.Children.OfType<Button>().Select(b => (string)b.Tag!).ToList();
    public string CpuText => _cpu?.Text ?? "";

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        _hive.Changed -= OnHiveChanged;
    }

    private void OnHiveChanged(object? sender, RegistryChange change)
    {
        if (change.IsUnder(WidgetSettings.KeyPath)) Dispatcher.UIThread.Post(Rebuild);
    }

    private void Update()
    {
        _clock.Text = _now().ToString("t");
        if (_cpu is not null)
            try
            {
                var machine = _sampler.Sample().Machine;
                _cpu.Text = $"CPU {machine.CpuPercent:0}%";
                _memory!.Text = $"Memory {machine.MemoryUsed / 1048576} of {machine.MemoryTotal / 1048576} MB";
                _memoryBar!.Value = machine.MemoryTotal == 0 ? 0 : 100.0 * machine.MemoryUsed / machine.MemoryTotal;
            }
            catch (IOException) { }
    }

    // Builds the cards the settings ask for, then fills the ones that need the network or the disk in the background.
    public void Rebuild()
    {
        _left.Children.Clear(); _right.Children.Clear();
        _cpu = null;
        var n = 0;
        foreach (var widget in _settings.Shown.Where(w => w.Id != WidgetCatalog.News))
        {
            var body = new StackPanel { Spacing = 6 };
            var card = Card(widget.Title, widget.IconNames, body, widget.Id, tag: widget.Id);
            (n++ % 2 == 0 ? _left : _right).Children.Add(card);
            switch (widget.Id)
            {
                case WidgetCatalog.Weather: _ = FillWeatherAsync(body); break;
                case WidgetCatalog.Photos: FillPhotos(body); break;
                case WidgetCatalog.System: FillSystem(body); break;
            }
        }
        // the community's widgets, after ours; one that throws while drawing loses its card, nothing else
        foreach (var widget in _community().Where(w => _settings.IsShown(w.Id)))
        {
            Control content;
            try { content = widget.Create(); }
            catch (Exception e) when (e is not OutOfMemoryException) { continue; }
            (n++ % 2 == 0 ? _left : _right).Children.Add(Card(widget.Title, widget.Icons, content, widget.Id, tag: widget.Id));
        }
        _stories.Children.Clear();
        if (_settings.IsShown(WidgetCatalog.News)) _ = FillNewsAsync();
        Update();
    }

    // Loads what the network cards show (public so a test can wait for it).
    public async Task RefreshAsync()
    {
        Rebuild();
        await Task.CompletedTask;
    }

    private async Task FillWeatherAsync(StackPanel body)
    {
        if (_settings.City.Length == 0)
        {
            body.Children.Add(new TextBlock { Text = "Choose your city in the widget settings to see its weather.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            body.Children.Add(Link("Widget settings", _openSettings));
            return;
        }
        body.Children.Add(Muted("Loading the weather…"));
        WeatherReport? report;
        try { report = await _weather.GetAsync(_settings.City, _settings.Fahrenheit); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            body.Children.Clear();
            body.Children.Add(Muted("The weather could not be loaded. Check your connection."));
            return;
        }
        body.Children.Clear();
        if (report is null) { body.Children.Add(Muted($"No place found for \"{_settings.City}\".")); return; }
        body.Children.Add(new TextBlock { Text = report.Place, FontSize = 12, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                new ShellIcon { IconNames = [report.IconName, "weather-clear"], IconSize = 40, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = $"{report.Temperature:0}{report.Unit}", FontSize = 34, VerticalAlignment = VerticalAlignment.Center, Tag = "temperature" },
                new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = report.Condition }, Muted($"Humidity {report.Humidity}%  Wind {report.Wind:0}") } },
            },
        });
        var days = new UniformGrid { Rows = 1, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var day in report.Days)
            days.Children.Add(new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2,
                Children =
                {
                    new TextBlock { Text = day.Date == DateOnly.FromDateTime(_now()) ? "Today" : day.Date.ToString("ddd"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                    new ShellIcon { IconNames = [WeatherCodes.Icon(day.Code)], IconSize = 22 },
                    new TextBlock { Text = $"{day.High:0}°", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = $"{day.Low:0}°", FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center },
                },
            });
        body.Children.Add(days);
    }

    private void FillPhotos(StackPanel body)
    {
        var today = DateOnly.FromDateTime(_now());
        var photos = OnThisDay.Find(_photoFolders, today);
        body.Children.Add(new TextBlock { Text = "On this day", FontSize = 18 });
        body.Children.Add(Muted($"{today:MMM d} · {photos.Count} item{(photos.Count == 1 ? "" : "s")}"));
        if (photos.Count == 0) { body.Children.Add(Muted("Nothing from earlier years in your Pictures folder.")); return; }
        var wrap = new WrapPanel();
        foreach (var path in photos)
        {
            var tile = new Button { Tag = path, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 3), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Width = 90, Height = 68 };
            try { using var stream = File.OpenRead(path); tile.Content = new Image { Source = Bitmap.DecodeToWidth(stream, 180), Stretch = Stretch.UniformToFill }; }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException) { continue; }
            tile.Click += (_, _) => _openFile(path);
            wrap.Children.Add(tile);
        }
        body.Children.Add(wrap);
    }

    private void FillSystem(StackPanel body)
    {
        _cpu = new TextBlock { Text = "CPU", FontSize = 18 };
        _memory = new TextBlock { FontSize = 12 };
        _memoryBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6 };
        body.Children.Add(_cpu);
        body.Children.Add(_memory);
        body.Children.Add(_memoryBar);
    }

    private async Task FillNewsAsync()
    {
        _stories.Children.Clear();
        if (_settings.Feeds.Count == 0)
        {
            _stories.Children.Add(Muted("Add the address of a news feed (RSS or Atom) in the widget settings."));
            _stories.Children.Add(Link("Widget settings", _openSettings));
            return;
        }
        IReadOnlyList<FeedItem> items;
        try { items = await FeedReader.LoadAsync(_settings.Feeds, _fetch); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { items = []; }
        _stories.Children.Clear();
        if (items.Count == 0) { _stories.Children.Add(Muted("No headlines could be loaded.")); return; }
        foreach (var item in items)
        {
            var button = new Button
            {
                Tag = item.Link, Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel { Children = { new TextBlock { Text = item.Title, TextWrapping = TextWrapping.Wrap }, Muted(item.Source) } },
            };
            button.Click += (_, _) => { if (item.Link.Length > 0) _openUrl(item.Link); };
            _stories.Children.Add(button);
        }
    }

    // A card: its title with a "..." menu to hide it, and the content.
    private Border Card(string title, IReadOnlyList<string> icons, Control content, string? widgetId, bool wide = false, string? tag = null)
    {
        var more = new Button { Content = "…", Tag = "more", Padding = new Thickness(6, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right };
        if (widgetId is not null)
        {
            var hide = new MenuItem { Header = "Hide this widget" };
            hide.Click += (_, _) => RegistryBool.Set(_hive, WidgetSettings.ShowKey, widgetId, false);
            more.Flyout = new MenuFlyout { Items = { hide } };
        }
        else more.IsVisible = false;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        header.Children.Add(new ShellIcon { IconNames = icons, IconSize = 16, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        Grid.SetColumn(more, 2);
        header.Children.Add(more);
        var surface = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.Transparent);
        return new Border
        {
            Tag = tag ?? title, CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Background = surface, BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.Gray), BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = 8, Children = { header, content } },
        };
    }

    // "Add widgets": the widgets that are hidden, to bring back; and the settings.
    private void AddMenu(Button anchor)
    {
        var menu = new MenuFlyout();
        foreach (var widget in WidgetCatalog.All.Where(w => !_settings.IsShown(w.Id)))
        {
            var item = new MenuItem { Header = "Add " + widget.Title };
            var id = widget.Id;
            item.Click += (_, _) => RegistryBool.Set(_hive, WidgetSettings.ShowKey, id, true);
            menu.Items.Add(item);
        }
        foreach (var widget in _community().Where(w => !_settings.IsShown(w.Id)))
        {
            var item = new MenuItem { Header = "Add " + widget.Title };
            var id = widget.Id;
            item.Click += (_, _) => RegistryBool.Set(_hive, WidgetSettings.ShowKey, id, true);
            menu.Items.Add(item);
        }
        var settings = new MenuItem { Header = "Widget settings…" };
        settings.Click += (_, _) => _openSettings();
        menu.Items.Add(settings);
        menu.ShowAt(anchor);
    }

    private static TextBlock Muted(string text) => new() { Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    private static Button Link(string text, Action invoke)
    {
        var button = new Button { Content = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Tag = "link" };
        button.Click += (_, _) => invoke();
        return button;
    }
}
