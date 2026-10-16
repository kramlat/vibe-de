using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.Theme;
using Vde.Terminal;
using Vibe.Registry;

namespace VTerminal;

// The terminal window: tabs, a menu of shell profiles, and one TerminalView per tab. The look follows the shell
// theme; the font and behaviour come from HKCU\Software\Vde\Terminal (the Terminal page in Settings).
public sealed class TerminalWindow : Window
{
    private sealed class Tab
    {
        public required TerminalSession Session { get; init; }
        public required TerminalView View { get; init; }
        public required string Name { get; set; }
        public Button Header { get; set; } = null!;
        public TextBlock Label { get; set; } = null!;
        public string? Title { get; set; }
    }

    private readonly IHive _hive;
    private readonly IReadOnlyList<ShellProfile> _profiles;
    private readonly string? _integrationDir;
    private readonly bool _hold;
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 4, 0, 0) };
    private readonly ContentControl _content = new();
    private readonly List<Tab> _tabs = new();
    private Tab? _current;
    private readonly string _startDirectory;

    public TerminalWindow(IHive hive, IReadOnlyList<ShellProfile> profiles, string startDirectory, bool hold = false)
    {
        _hive = hive;
        _profiles = profiles;
        _integrationDir = ShellProfiles.FindIntegrationDir();
        _hold = hold;
        _startDirectory = startDirectory;
        Title = "Terminal";
        Width = 980; Height = 600;

        var plus = new Button { Content = "+", Padding = new Thickness(10, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        plus.Click += (_, _) => NewTab(DefaultProfile());
        var drop = new Button { Content = "▾", Padding = new Thickness(6, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        drop.Click += (_, _) => ShowProfileMenu(drop);
        var strip = new DockPanel { LastChildFill = false };
        strip.Children.Add(_tabStrip);
        var addButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Children = { plus, drop } };
        strip.Children.Add(addButtons);
        var root = new DockPanel();
        DockPanel.SetDock(strip, Dock.Top);
        root.Children.Add(strip);
        root.Children.Add(_content);
        Content = root;

        KeyDown += OnWindowKey;
        _hive.Changed += (_, c) => { if (c.IsUnder(BuiltInPages.TerminalKey)) Dispatcher.UIThread.Post(ApplySettings); };
        ThemeTokens.Changed += (_, _) => Dispatcher.UIThread.Post(ApplySettings);
        ActualThemeVariantChanged += (_, _) => ApplySettings();
        Closed += (_, _) => { foreach (var t in _tabs.ToList()) t.Session.Dispose(); };
    }

    public int TabCount => _tabs.Count;
    public TerminalView? CurrentView => _current?.View;
    public TerminalSession? CurrentSession => _current?.Session;
    public IReadOnlyList<string> TabNames => _tabs.Select(t => t.Label.Text ?? "").ToList();

    // ---- profiles ----

    public ShellProfile DefaultProfile()
    {
        var wanted = _hive.GetValue(BuiltInPages.TerminalKey, "DefaultProfile") is { Kind: RegistryValueKind.String, Data: string { Length: > 0 } s } ? s : null;
        return _profiles.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)) ?? _profiles[0];
    }

    private void ShowProfileMenu(Control anchor)
    {
        var menu = new ContextMenu();
        foreach (var profile in _profiles)
        {
            var item = new MenuItem { Header = profile.Name };
            var captured = profile;
            item.Click += (_, _) => NewTab(captured);
            menu.Items.Add(item);
        }
        menu.Open(anchor);
    }

    // ---- tabs ----

    public void NewTab(ShellProfile profile, string? workingDirectory = null, IReadOnlyList<string>? command = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var (cmd, vars) = command is null ? ShellProfiles.Launch(profile, _integrationDir) : (command, env ?? new Dictionary<string, string>());
        var dir = workingDirectory ?? _current?.Session.Emulator.WorkingDirectory ?? _startDirectory;
        if (!Directory.Exists(dir)) dir = _startDirectory;
        var view = new TerminalView();
        ApplyView(view);
        var session = TerminalSession.Start(cmd, 100, 30, dir, vars);
        var tab = new Tab { Session = session, View = view, Name = command is null ? profile.Name : Options.Name(cmd) };

        view.Session = session;
        session.Emulator.TitleChanged += title => Dispatcher.UIThread.Post(() => { tab.Title = title; Retitle(tab); });
        session.Emulator.WorkingDirectoryChanged += _ => Dispatcher.UIThread.Post(() => { });
        view.NewTabRequested += () => NewTab(DefaultProfile());
        view.CloseRequested += () => CloseTab(tab);
        view.LinkInvoked += Open;
        view.BellRang += () => { if (_current != tab) tab.Label.FontWeight = FontWeight.Bold; };
        session.Exited.ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (t.Result == 0 && !_hold) CloseTab(tab);
            else session.Emulator.Feed($"\r\n\e[2m[process ended with status {t.Result}]\e[0m\r\n");
        }));

        tab.Label = new TextBlock { Text = tab.Name, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = "×", Padding = new Thickness(5, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(6, 0, 0, 0) };
        close.Click += (_, _) => CloseTab(tab);
        tab.Header = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { tab.Label, close } },
            Padding = new Thickness(10, 4), BorderThickness = new Thickness(0),
        };
        tab.Header.Click += (_, _) => Select(tab);
        _tabs.Add(tab);
        _tabStrip.Children.Add(tab.Header);
        Select(tab);
    }

    private void Select(Tab tab)
    {
        _current = tab;
        _content.Content = tab.View;
        tab.Label.FontWeight = FontWeight.Normal;
        foreach (var t in _tabs)
            t.Header.Background = t == tab ? ThemeTokens.Find(this, ThemeToken.HoverFill, Brushes.DimGray) : Brushes.Transparent;
        Title = tab.Title is { Length: > 0 } title ? title : tab.Name;
        Dispatcher.UIThread.Post(() => tab.View.Focus(), DispatcherPriority.Background);
    }

    private void Retitle(Tab tab)
    {
        tab.Label.Text = tab.Title is { Length: > 0 } t ? t : tab.Name;
        if (_current == tab) Title = tab.Label.Text;
    }

    private void CloseTab(Tab? tab = null)
    {
        tab ??= _current;
        if (tab is null || !_tabs.Remove(tab)) return;
        _tabStrip.Children.Remove(tab.Header);
        tab.Session.Dispose();
        if (_tabs.Count == 0) { Close(); return; }
        if (_current == tab) Select(_tabs[Math.Min(_tabs.IndexOf(_tabs.Last()), _tabs.Count - 1)]);
    }

    public void CloseCurrentTab() => CloseTab(_current);

    private void OnWindowKey(object? sender, KeyEventArgs e)
    {
        var m = e.KeyModifiers;
        if (m == KeyModifiers.Control && e.Key == Key.Tab) { Cycle(1); e.Handled = true; }
        else if (m == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.Tab) { Cycle(-1); e.Handled = true; }
        else if (m == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key is >= Key.D1 and <= Key.D9 && e.Key - Key.D1 < _tabs.Count) { Select(_tabs[e.Key - Key.D1]); e.Handled = true; }
        else if (m == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.N) { NewWindow(); e.Handled = true; }
        else if (m == KeyModifiers.None && e.Key == Key.F11) { WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; e.Handled = true; }
    }

    private void Cycle(int step)
    {
        if (_current is null) return;
        Select(_tabs[(_tabs.IndexOf(_current) + step + _tabs.Count) % _tabs.Count]);
    }

    // A new window in the same folder (another process, so one crashing does not take the rest).
    private void NewWindow()
    {
        try
        {
            var self = Environment.ProcessPath;
            if (self is null) return;
            var info = new System.Diagnostics.ProcessStartInfo(self) { UseShellExecute = false };
            if (_current?.Session.Emulator.WorkingDirectory is { } cwd) { info.ArgumentList.Add("--working-directory"); info.ArgumentList.Add(cwd); }
            System.Diagnostics.Process.Start(info)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static void Open(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file" or "mailto")) return;   // only things that are safe to hand to the desktop
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", uri.AbsoluteUri) { UseShellExecute = false })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ---- look and settings ----

    private void ApplySettings()
    {
        foreach (var t in _tabs) ApplyView(t.View);
        if (_current is not null) foreach (var t in _tabs) t.Header.Background = t == _current ? ThemeTokens.Find(this, ThemeToken.HoverFill, Brushes.DimGray) : Brushes.Transparent;
    }

    private void ApplyView(TerminalView view)
    {
        var family = _hive.GetValue(BuiltInPages.TerminalKey, "FontFamily") is { Kind: RegistryValueKind.String, Data: string { Length: > 0 } f } ? f : "monospace";
        var size = _hive.GetValue(BuiltInPages.TerminalKey, "FontSize") is { Kind: RegistryValueKind.QWord, Data: long n } ? n
            : _hive.GetValue(BuiltInPages.TerminalKey, "FontSize") is { Kind: RegistryValueKind.DWord, Data: uint d } ? d : 13;
        view.FontFamilyName = family;
        view.TerminalFontSize = size;
        view.CopyOnSelect = _hive.GetValue(BuiltInPages.TerminalKey, "CopyOnSelect") is { Kind: RegistryValueKind.DWord, Data: uint c } && c != 0;
        Color Of(ThemeToken token, Color fallback) => ThemeTokens.Find(this, token, new SolidColorBrush(fallback)) is ISolidColorBrush b ? b.Color : fallback;
        view.Palette = TerminalPalette.ForBackground(Of(ThemeToken.Surface, Color.Parse("#1E1E1E")), Of(ThemeToken.TextPrimary, Color.Parse("#CCCCCC")), Of(ThemeToken.Accent, Color.Parse("#0078D4")));
    }
}
