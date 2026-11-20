using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vde.Shell;
using Vde.Shell.Controls;

namespace VTaskMgr;

// The task manager: every process with its cpu and memory (click a heading to sort, End task to stop one), and a
// Performance tab with cpu and memory graphs. Reads /proc once a second.
public sealed class TaskManagerWindow : Window
{
    private enum Column { Name, Pid, User, Cpu, Memory }

    private sealed class Row
    {
        public int Pid;
        public ProcessInfo Info = null!;
        public readonly Grid Grid = new() { ColumnDefinitions = Columns(), Margin = new Thickness(6, 2) };
        public readonly TextBlock[] Cells = [new(), new(), new(), new(), new()];
        public Row()
        {
            for (int i = 0; i < Cells.Length; i++)
            {
                Cells[i].TextTrimming = TextTrimming.CharacterEllipsis;
                if (i >= 3) Cells[i].TextAlignment = TextAlignment.Right;
                Grid.SetColumn(Cells[i], i);
                Grid.Children.Add(Cells[i]);
            }
        }
    }

    private readonly ProcessSampler _sampler = new();
    private readonly Dictionary<int, Row> _rows = new();
    private readonly ObservableCollection<Row> _items = new();
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter by name or process id", MinWidth = 220 };
    private readonly Button _end = new() { Content = "End task", IsEnabled = false };
    private readonly SpeedGraphView _cpuGraph = new() { Maximum = 100, Height = 150 };
    private readonly SpeedGraphView _memGraph = new() { Maximum = 100, Height = 150 };
    private readonly TextBlock _cpuText = new() { FontSize = 20 }, _memText = new() { FontSize = 20 }, _summary = new() { Opacity = .75 };
    private readonly List<double> _cpuHistory = new(), _memHistory = new();
    private readonly Button[] _headings = new Button[5];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Column _sort = Column.Cpu;
    private bool _descending = true;
    private const int History = 60;

    public TaskManagerWindow()
    {
        Title = "Task Manager";
        Width = 860; Height = 600;
        MinWidth = 520; MinHeight = 360;

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Processes", Content = BuildProcesses() });
        tabs.Items.Add(new TabItem { Header = "Performance", Content = BuildPerformance() });
        Content = tabs;

        _list.ItemsSource = _items;
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Row>((r, _) => r.Grid);
        _list.SelectionChanged += (_, _) => _end.IsEnabled = _list.SelectedItem is Row;
        _list.KeyDown += (_, e) => { if (e.Key == Key.Delete && _list.SelectedItem is Row) EndSelected(); };
        _filter.TextChanged += (_, _) => Refresh(sample: false);
        _end.Click += (_, _) => EndSelected();

        _timer.Tick += (_, _) => Refresh(sample: true);
        Opened += (_, _) => { Refresh(sample: true); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
        Dispatcher.UIThread.Post(() => Refresh(sample: true));   // a first sample for the baseline; the next one has numbers
    }

    private static ColumnDefinitions Columns() => new("*,70,110,70,100");

    private Control BuildProcesses()
    {
        var head = new Grid { ColumnDefinitions = Columns(), Margin = new Thickness(6, 0) };
        string[] names = ["Name", "PID", "User", "CPU", "Memory"];
        for (int i = 0; i < names.Length; i++)
        {
            var column = (Column)i;
            var b = new Button
            {
                Content = names[i], Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = i >= 3 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            b.Click += (_, _) => { if (_sort == column) _descending = !_descending; else { _sort = column; _descending = column is Column.Cpu or Column.Memory; } Refresh(sample: false); };
            Grid.SetColumn(b, i);
            head.Children.Add(b);
            _headings[i] = b;
        }
        var bar = new DockPanel { Margin = new Thickness(8, 6) };
        DockPanel.SetDock(_end, Dock.Right);
        bar.Children.Add(_end);
        bar.Children.Add(_filter);
        _filter.HorizontalAlignment = HorizontalAlignment.Left;

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(_summary, Dock.Bottom);
        _summary.Margin = new Thickness(10, 4);
        root.Children.Add(bar);
        root.Children.Add(head);
        root.Children.Add(_summary);
        root.Children.Add(_list);
        return root;
    }

    private Control BuildPerformance()
    {
        Control Block(string title, TextBlock value, SpeedGraphView graph) => new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 16), Spacing = 4,
            Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, value, graph },
        };
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children = { Block("CPU", _cpuText, _cpuGraph), Block("Memory", _memText, _memGraph) },
            },
        };
    }

    private void Refresh(bool sample)
    {
        if (sample)
        {
            var (processes, machine) = _sampler.Sample();
            _latest = processes;
            Push(_cpuHistory, machine.CpuPercent);
            double mem = machine.MemoryTotal > 0 ? 100.0 * machine.MemoryUsed / machine.MemoryTotal : 0;
            Push(_memHistory, mem);
            _cpuGraph.Points = Series(_cpuHistory);
            _memGraph.Points = Series(_memHistory);
            _cpuGraph.Progress = 1; _memGraph.Progress = 1;
            _cpuText.Text = $"{machine.CpuPercent:0}%  ·  {machine.Cpus} logical processors";
            _memText.Text = $"{Bytes(machine.MemoryUsed)} of {Bytes(machine.MemoryTotal)}  ({mem:0}%)";
            _cpuGraph.Caption = $"{machine.CpuPercent:0}%";
            _memGraph.Caption = $"{mem:0}%";
            _summary.Text = $"{machine.Processes} processes · {machine.Threads} threads · up {Uptime(machine.Uptime)}";
        }
        ShowRows();
    }

    private IReadOnlyList<ProcessInfo> _latest = [];

    private static void Push(List<double> history, double v)
    {
        history.Add(v);
        if (history.Count > History) history.RemoveAt(0);
    }

    private static IReadOnlyList<(double X, double Y)> Series(List<double> h) =>
        h.Select((v, i) => ((double)(i + History - h.Count) / (History - 1), v)).ToList();

    private void ShowRows()
    {
        int? keep = (_list.SelectedItem as Row)?.Pid;
        string filter = _filter.Text?.Trim() ?? "";
        var wanted = _latest.Where(p => filter.Length == 0 || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || p.Pid.ToString().Contains(filter)).ToList();
        Func<ProcessInfo, IComparable> key = _sort switch
        {
            Column.Pid => p => p.Pid,
            Column.User => p => p.User,
            Column.Cpu => p => p.CpuPercent,
            Column.Memory => p => p.MemoryBytes,
            _ => p => p.Name.ToLowerInvariant(),
        };
        var ordered = (_descending ? wanted.OrderByDescending(key) : wanted.OrderBy(key)).ThenBy(p => p.Pid).ToList();

        var live = new HashSet<int>(ordered.Select(p => p.Pid));
        foreach (var gone in _rows.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _items.Remove(_rows[gone]);
            _rows.Remove(gone);
        }
        for (int i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            if (!_rows.TryGetValue(p.Pid, out var row))
                _rows[p.Pid] = row = new Row { Pid = p.Pid };
            row.Info = p;
            row.Cells[0].Text = p.Name;
            ToolTip.SetTip(row.Grid, p.Command);
            row.Cells[1].Text = p.Pid.ToString();
            row.Cells[2].Text = p.User;
            row.Cells[3].Text = p.CpuPercent < 0.05 ? "0%" : $"{p.CpuPercent:0.#}%";
            row.Cells[4].Text = Bytes(p.MemoryBytes);
            int at = _items.IndexOf(row);
            if (at < 0) _items.Insert(Math.Min(i, _items.Count), row);
            else if (at != i) _items.Move(at, Math.Min(i, _items.Count - 1));
        }
        if (keep is int pid && _rows.TryGetValue(pid, out var selected) && !ReferenceEquals(_list.SelectedItem, selected))
            _list.SelectedItem = selected;
        for (int i = 0; i < _headings.Length; i++)
            _headings[i].Content = new[] { "Name", "PID", "User", "CPU", "Memory" }[i] + ((int)_sort == i ? (_descending ? " ▾" : " ▴") : "");
    }

    private void EndSelected()
    {
        if (_list.SelectedItem is not Row row) return;
        // SIGTERM: the program gets to save and exit; a stubborn one can be ended again (SIGKILL on the second try)
        try
        {
            var signal = row.Info.State == 'Z' ? 0 : _terminated.Contains(row.Pid) ? 9 : 15;
            _terminated.Add(row.Pid);
            if (signal != 0) Kill(row.Pid, signal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private readonly HashSet<int> _terminated = new();

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private static void Kill(int pid, int signal)
    {
        if (kill(pid, signal) != 0)
            throw new UnauthorizedAccessException($"cannot signal {pid}");
    }

    internal static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):0} KB",
        _ => $"{b} B",
    };

    private static string Uptime(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
}
