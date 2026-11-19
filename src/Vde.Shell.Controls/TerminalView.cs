using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Vde.Shell.Theme;
using Vde.Terminal;
using TermCell = Vde.Terminal.Cell;

namespace Vde.Shell.Controls;

// Shows a TerminalSession: the cells drawn in a monospace font, the cursor, the selection, scrolling through the
// history, and everything typed or clicked sent back to the program (as keys, or as mouse events when the program
// asked for them). The size follows the control: whenever it changes the program is told the new rows and columns.
public sealed class TerminalView : Control
{
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private TerminalSession? _session;
    private Typeface _typeface = new("monospace");
    private double _fontSize = 13, _cellW = 8, _cellH = 16;
    private int _scrollOffset;                 // lines scrolled back from the live screen
    private int _lastScrollback;
    private bool _blinkOn = true, _repaintQueued, _selecting;
    private (int Col, int Row)? _selAnchor, _selEnd;   // absolute rows (0 = top of the history)
    private int _clicks;
    private DateTime _lastClick;
    private TerminalPalette _palette = new();

    public TerminalView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _blink.Tick += (_, _) => { _blinkOn = !_blinkOn; InvalidateVisual(); };
        MeasureCell();
    }

    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null) { _session.Output -= OnOutput; _session.Emulator.Bell -= OnBell; }
            _session = value;
            if (value is not null)
            {
                value.Output += OnOutput;
                value.Emulator.Bell += OnBell;
                _lastScrollback = value.Emulator.ScrollbackCount;
                ApplySize();
            }
            InvalidateVisual();
        }
    }

    public string FontFamilyName
    {
        get => _typeface.FontFamily.Name;
        set { _typeface = new Typeface(string.IsNullOrWhiteSpace(value) ? "monospace" : value); MeasureCell(); ApplySize(); InvalidateVisual(); }
    }

    public double TerminalFontSize
    {
        get => _fontSize;
        set { _fontSize = Math.Clamp(value, 6, 72); MeasureCell(); ApplySize(); InvalidateVisual(); }
    }

    public TerminalPalette Palette { get => _palette; set { _palette = value; InvalidateVisual(); } }
    public bool CopyOnSelect { get; set; }
    public int Columns => Math.Max((int)(Bounds.Width / _cellW), 1);
    public int RowsVisible => Math.Max((int)(Bounds.Height / _cellH), 1);
    public double CellWidth => _cellW;
    public double CellHeight => _cellH;
    public int ScrollOffset => _scrollOffset;

    public event Action? BellRang;
    public event Action<string>? LinkInvoked;
    public event Action? NewTabRequested, CloseRequested;

    // ---- sizes ----

    private void MeasureCell()
    {
        var sample = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White);
        _cellW = Math.Max(sample.Width / 10, 1);
        _cellH = Math.Max(Math.Ceiling(sample.Height), 1);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplySize();
    }

    private void ApplySize()
    {
        if (_session is null || Bounds.Width < 1 || Bounds.Height < 1) return;
        var (c, r) = (Columns, RowsVisible);
        if (c != _session.Emulator.Cols || r != _session.Emulator.Rows) _session.Resize(c, r);
    }

    // ---- output ----

    private void OnBell() => Dispatcher.UIThread.Post(() => BellRang?.Invoke());

    private void OnOutput()
    {
        if (_repaintQueued) return;
        _repaintQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _repaintQueued = false;
            if (_session is null) return;
            int added;
            lock (_session.Emulator.SyncRoot)
            {
                added = _session.Emulator.ScrollbackCount - _lastScrollback;
                _lastScrollback = _session.Emulator.ScrollbackCount;
            }
            if (_scrollOffset > 0 && added > 0) _scrollOffset = Math.Min(_scrollOffset + added, _lastScrollback);   // keep reading where you were
            _blinkOn = true;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    // ---- drawing ----

    public override void Render(DrawingContext context)
    {
        var palette = _palette;
        context.DrawRectangle(new SolidColorBrush(palette.Background), null, new Rect(Bounds.Size));
        if (_session is null) return;
        var em = _session.Emulator;
        lock (em.SyncRoot)
        {
            var reverse = em.Modes.ReverseVideo;
            var top = em.ScrollbackCount - _scrollOffset;   // absolute row at the top of the view
            var rows = Math.Min(em.Rows, RowsVisible);
            var sel = NormalizedSelection();
            for (var r = 0; r < rows; r++)
            {
                var abs = top + r;
                if (abs < 0 || abs >= em.ScrollbackCount + em.Rows) continue;
                DrawLine(context, em, em.GetHistoryLine(abs), abs, r, palette, reverse, sel);
            }
            if (_scrollOffset == 0 && em.Modes.CursorVisible && (IsFocused ? (!em.Modes.CursorBlink || _blinkOn) : true))
                DrawCursor(context, em, palette);
        }
    }

    private void DrawLine(DrawingContext context, Emulator em, Line line, int abs, int row, TerminalPalette palette, bool reverse, ((int Col, int Row) A, (int Col, int Row) B)? sel)
    {
        var y = row * _cellH;
        var cols = Math.Min(line.Cells.Length, Columns + 1);
        // backgrounds first, merged into runs
        var runStart = 0;
        Color RunBg(in TermCell c)
        {
            var inverse = c.Attrs.HasFlag(CellAttrs.Inverse) ^ reverse;
            return palette.Resolve(inverse ? c.Fg : c.Bg, inverse);
        }
        for (var x = 0; x <= cols; x++)
        {
            var end = x == cols || RunBg(line.Cells[x]) != RunBg(line.Cells[runStart]);
            if (!end) continue;
            var color = RunBg(line.Cells[runStart]);
            if (color != palette.Background)
                context.DrawRectangle(new SolidColorBrush(color), null, new Rect(runStart * _cellW, y, (x - runStart) * _cellW + 0.5, _cellH + 0.5));
            runStart = x;
            if (x == cols) break;
        }
        // selection
        if (sel is { } s && abs >= s.A.Row && abs <= s.B.Row)
        {
            var from = abs == s.A.Row ? s.A.Col : 0;
            var to = abs == s.B.Row ? s.B.Col : line.Cells.Length - 1;
            if (to >= from) context.DrawRectangle(new SolidColorBrush(palette.Selection), null, new Rect(from * _cellW, y, (to - from + 1) * _cellW, _cellH));
        }
        // text, in runs of the same colour and style
        for (var x = 0; x < cols;)
        {
            var cell = line.Cells[x];
            if (cell.IsEmpty || cell.Width == 0 || cell.Attrs.HasFlag(CellAttrs.Invisible)) { x++; continue; }
            var start = x;
            var text = new System.Text.StringBuilder();
            var wide = cell.Width == 2;
            while (x < cols)
            {
                var c = line.Cells[x];
                if (c.IsEmpty || c.Width == 0 || !SameStyle(c, cell) || (c.Width == 2) != wide || (wide && x > start)) break;
                text.Append(c.Text);
                x += c.Width == 2 ? 2 : 1;
            }
            var inverse = cell.Attrs.HasFlag(CellAttrs.Inverse) ^ reverse;
            var fg = palette.Resolve(inverse ? cell.Bg : cell.Fg, !inverse);
            if (cell.Attrs.HasFlag(CellAttrs.Faint)) fg = Color.FromArgb(fg.A, (byte)((fg.R + palette.Background.R) / 2), (byte)((fg.G + palette.Background.G) / 2), (byte)((fg.B + palette.Background.B) / 2));
            var brush = new SolidColorBrush(fg);
            var face = new Typeface(_typeface.FontFamily, cell.Attrs.HasFlag(CellAttrs.Italic) ? FontStyle.Italic : FontStyle.Normal,
                cell.Attrs.HasFlag(CellAttrs.Bold) ? FontWeight.Bold : FontWeight.Normal);
            var formatted = new FormattedText(text.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, _fontSize, brush);
            context.DrawText(formatted, new Point(start * _cellW, y + Math.Max(0, (_cellH - formatted.Height) / 2)));
            var width = (x - start) * _cellW;
            var pen = new Pen(brush, 1);
            if (cell.Attrs.HasFlag(CellAttrs.Underline) || cell.Link != 0)
                context.DrawLine(pen, new Point(start * _cellW, y + _cellH - 1.5), new Point(start * _cellW + width, y + _cellH - 1.5));
            if (cell.Attrs.HasFlag(CellAttrs.DoubleUnderline))
            {
                context.DrawLine(pen, new Point(start * _cellW, y + _cellH - 1.5), new Point(start * _cellW + width, y + _cellH - 1.5));
                context.DrawLine(pen, new Point(start * _cellW, y + _cellH - 3.5), new Point(start * _cellW + width, y + _cellH - 3.5));
            }
            if (cell.Attrs.HasFlag(CellAttrs.Strike))
                context.DrawLine(pen, new Point(start * _cellW, y + _cellH / 2), new Point(start * _cellW + width, y + _cellH / 2));
            if (cell.Attrs.HasFlag(CellAttrs.Overline))
                context.DrawLine(pen, new Point(start * _cellW, y + 0.5), new Point(start * _cellW + width, y + 0.5));
        }
        // a failed command is marked at the left edge of its prompt (shell integration)
        if (line.Mark == LineMark.PromptStart && line.ExitCode is > 0)
            context.DrawRectangle(new SolidColorBrush(palette.Ansi[9]), null, new Rect(0, y + 2, 2, _cellH - 4));
    }

    private static bool SameStyle(in TermCell a, in TermCell b)
        => a.Fg == b.Fg && a.Bg == b.Bg && a.Attrs == b.Attrs && (a.Link != 0) == (b.Link != 0);

    private void DrawCursor(DrawingContext context, Emulator em, TerminalPalette palette)
    {
        var x = em.CursorX * _cellW;
        var y = em.CursorY * _cellH;
        var brush = new SolidColorBrush(palette.Cursor);
        if (!IsFocused)
        {
            context.DrawRectangle(null, new Pen(brush, 1), new Rect(x + 0.5, y + 0.5, _cellW - 1, _cellH - 1));
            return;
        }
        switch (em.Modes.Cursor)
        {
            case CursorShape.Bar: context.DrawRectangle(brush, null, new Rect(x, y, 2, _cellH)); break;
            case CursorShape.Underline: context.DrawRectangle(brush, null, new Rect(x, y + _cellH - 2, _cellW, 2)); break;
            default:
                context.DrawRectangle(brush, null, new Rect(x, y, _cellW, _cellH));
                var cell = em.GetLine(em.CursorY).Cells[Math.Min(em.CursorX, em.Cols - 1)];
                if (!cell.IsEmpty)   // the character under a block cursor, in the background colour
                {
                    var text = new FormattedText(cell.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _fontSize, new SolidColorBrush(palette.Background));
                    context.DrawText(text, new Point(x, y + Math.Max(0, (_cellH - text.Height) / 2)));
                }
                break;
        }
    }

    // ---- focus ----

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _blink.Start();
        _blinkOn = true;
        if (_session?.Emulator.Modes.FocusEvents == true) _session.Send("\e[I");
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _blink.Stop();
        if (_session?.Emulator.Modes.FocusEvents == true) _session.Send("\e[O");
        InvalidateVisual();
    }

    // ---- keyboard ----

    private static KeyMods Mods(KeyModifiers m)
        => (m.HasFlag(KeyModifiers.Shift) ? KeyMods.Shift : 0) | (m.HasFlag(KeyModifiers.Alt) ? KeyMods.Alt : 0) | (m.HasFlag(KeyModifiers.Control) ? KeyMods.Ctrl : 0);

    private static TerminalKey Map(Key key) => key switch
    {
        Key.Enter => TerminalKey.Enter, Key.Tab => TerminalKey.Tab, Key.Back => TerminalKey.Backspace, Key.Escape => TerminalKey.Escape,
        Key.Up => TerminalKey.Up, Key.Down => TerminalKey.Down, Key.Left => TerminalKey.Left, Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home, Key.End => TerminalKey.End, Key.PageUp => TerminalKey.PageUp, Key.PageDown => TerminalKey.PageDown,
        Key.Insert => TerminalKey.Insert, Key.Delete => TerminalKey.Delete,
        Key.F1 => TerminalKey.F1, Key.F2 => TerminalKey.F2, Key.F3 => TerminalKey.F3, Key.F4 => TerminalKey.F4, Key.F5 => TerminalKey.F5,
        Key.F6 => TerminalKey.F6, Key.F7 => TerminalKey.F7, Key.F8 => TerminalKey.F8, Key.F9 => TerminalKey.F9, Key.F10 => TerminalKey.F10,
        Key.F11 => TerminalKey.F11, Key.F12 => TerminalKey.F12,
        _ => TerminalKey.None,
    };

    // The character a Ctrl or Alt combination means, for keys that give no text of their own.
    private static string? CharFor(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Space => " ", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemPipe or Key.Oem5 => "\\",
        Key.OemMinus => "_", Key.OemQuestion => "/", Key.OemComma => ",", Key.OemPeriod => ".", Key.OemSemicolon => ";",
        _ => null,
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null) return;
        var mods = Mods(e.KeyModifiers);
        var ctrlShift = (mods & (KeyMods.Ctrl | KeyMods.Shift)) == (KeyMods.Ctrl | KeyMods.Shift);

        // the terminal's own shortcuts: Ctrl+Shift so Ctrl+C and friends still reach the program
        if (ctrlShift)
        {
            switch (e.Key)
            {
                case Key.C: _ = CopyAsync(); e.Handled = true; return;
                case Key.V: _ = PasteAsync(); e.Handled = true; return;
                case Key.T: NewTabRequested?.Invoke(); e.Handled = true; return;
                case Key.W: CloseRequested?.Invoke(); e.Handled = true; return;
                case Key.A: SelectAll(); e.Handled = true; return;
                case Key.Up: JumpToPrompt(backwards: true); e.Handled = true; return;
                case Key.Down: JumpToPrompt(backwards: false); e.Handled = true; return;
                case Key.OemPlus or Key.Add: TerminalFontSize += 1; e.Handled = true; return;
                case Key.OemMinus or Key.Subtract: TerminalFontSize -= 1; e.Handled = true; return;
            }
        }
        if (mods == KeyMods.Shift && e.Key == Key.Insert) { _ = PasteAsync(); e.Handled = true; return; }
        if (mods == KeyMods.Ctrl && e.Key == Key.Insert) { _ = CopyAsync(); e.Handled = true; return; }
        if (mods == KeyMods.Shift && e.Key is Key.PageUp or Key.PageDown)
        {
            Scroll(e.Key == Key.PageUp ? RowsVisible - 1 : -(RowsVisible - 1));
            e.Handled = true;
            return;
        }

        var key = Map(e.Key);
        if (key != TerminalKey.None)
        {
            ClearSelection();
            _scrollOffset = 0;
            Send(KeyEncoder.Encode(key, mods, _session.Emulator.Modes));
            e.Handled = true;
            return;
        }
        // Ctrl and Alt combinations give no text input: translate them here
        if ((mods & (KeyMods.Ctrl | KeyMods.Alt)) != 0 && (e.KeySymbol is { Length: > 0 } || CharFor(e.Key) is not null))
        {
            var text = (mods & KeyMods.Ctrl) != 0 ? CharFor(e.Key) : e.KeySymbol ?? CharFor(e.Key);
            if (text is not null)
            {
                if ((mods & KeyMods.Shift) != 0 && text.Length == 1 && char.IsLetter(text[0]) && (mods & KeyMods.Ctrl) == 0) text = text.ToUpperInvariant();
                _scrollOffset = 0;
                Send(KeyEncoder.EncodeText(text, mods & (KeyMods.Ctrl | KeyMods.Alt)));
                e.Handled = true;
            }
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_session is null || string.IsNullOrEmpty(e.Text)) return;
        ClearSelection();
        _scrollOffset = 0;
        Send(KeyEncoder.EncodeText(e.Text, KeyMods.None));
        e.Handled = true;
    }

    private void Send(byte[]? bytes)
    {
        if (bytes is not null) _session?.Send(bytes);
    }

    // ---- clipboard ----

    public string SelectedText
    {
        get
        {
            if (_session is null || NormalizedSelection() is not { } s) return "";
            var em = _session.Emulator;
            var sb = new System.Text.StringBuilder();
            lock (em.SyncRoot)
                for (var abs = s.A.Row; abs <= s.B.Row && abs < em.ScrollbackCount + em.Rows; abs++)
                {
                    var line = em.GetHistoryLine(abs);
                    sb.Append(line.Text(abs == s.A.Row ? s.A.Col : 0, abs == s.B.Row ? s.B.Col : int.MaxValue));
                    if (abs < s.B.Row && !line.Wrapped) sb.Append('\n');   // a wrapped line continues without a break
                }
            return sb.ToString();
        }
    }

    public async Task CopyAsync()
    {
        var text = SelectedText;
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public async Task PasteAsync()
    {
        if (_session is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        if (await clipboard.TryGetTextAsync() is { Length: > 0 } text)
            Paste(text);
    }

    public void Paste(string text)
    {
        if (_session is null) return;
        _scrollOffset = 0;
        _session.Send(KeyEncoder.EncodePaste(text, _session.Emulator.Modes));
    }

    // ---- scrolling and prompts ----

    public void Scroll(int lines)
    {
        if (_session is null) return;
        var max = _session.Emulator.ScrollbackCount;
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, max);
        InvalidateVisual();
    }

    // Shell integration: the previous or next prompt comes to the top of the view.
    public void JumpToPrompt(bool backwards)
    {
        if (_session is null) return;
        var em = _session.Emulator;
        int? target;
        lock (em.SyncRoot)
        {
            var top = em.ScrollbackCount - _scrollOffset;
            target = em.FindPrompt(top, backwards);
            if (target is { } t) _scrollOffset = Math.Clamp(em.ScrollbackCount - t, 0, em.ScrollbackCount);
            else if (!backwards) _scrollOffset = 0;
        }
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is null) return;
        var em = _session.Emulator;
        var up = e.Delta.Y > 0;
        if (em.Modes.Mouse != MouseTracking.Off && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var (col, row) = GridCell(e.GetPosition(this));
            Send(KeyEncoder.EncodeMouse(up ? KeyEncoder.MouseButton.WheelUp : KeyEncoder.MouseButton.WheelDown, false, false, col, row, Mods(e.KeyModifiers), em.Modes));
        }
        else if (em.Modes.AlternateScreen)   // full-screen programs without a mouse mode: the wheel moves the cursor
            for (var i = 0; i < 3; i++) Send(KeyEncoder.Encode(up ? TerminalKey.Up : TerminalKey.Down, KeyMods.None, em.Modes));
        else Scroll((int)Math.Round(e.Delta.Y * 3));
        e.Handled = true;
    }

    // ---- mouse ----

    private (int Col, int Row) GridCell(Point p)
        => (Math.Clamp((int)(p.X / _cellW), 0, Columns - 1), Math.Clamp((int)(p.Y / _cellH), 0, RowsVisible - 1));

    private (int Col, int Row) Absolute(Point p)
    {
        var (c, r) = GridCell(p);
        return (c, (_session?.Emulator.ScrollbackCount ?? 0) - _scrollOffset + r);
    }

    private bool ProgramWantsMouse(KeyModifiers mods) => _session?.Emulator.Modes.Mouse != MouseTracking.Off && !mods.HasFlag(KeyModifiers.Shift);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_session is null) return;
        var point = e.GetCurrentPoint(this);
        if (ProgramWantsMouse(e.KeyModifiers))
        {
            var (col, row) = GridCell(point.Position);
            var button = point.Properties.IsLeftButtonPressed ? KeyEncoder.MouseButton.Left : point.Properties.IsMiddleButtonPressed ? KeyEncoder.MouseButton.Middle : KeyEncoder.MouseButton.Right;
            Send(KeyEncoder.EncodeMouse(button, false, false, col, row, Mods(e.KeyModifiers), _session.Emulator.Modes));
            e.Pointer.Capture(this);
            return;
        }
        if (point.Properties.IsMiddleButtonPressed) { _ = PasteSelectionOrClipboard(); return; }
        if (point.Properties.IsRightButtonPressed)
        {
            ShowMenu();
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        // Ctrl+click opens a hyperlink
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && LinkAt(point.Position) is { } link) { LinkInvoked?.Invoke(link); return; }

        var now = DateTime.UtcNow;
        _clicks = now - _lastClick < TimeSpan.FromMilliseconds(400) ? _clicks % 3 + 1 : 1;
        _lastClick = now;
        var (c, abs) = Absolute(point.Position);
        if (_clicks == 2) SelectWord(c, abs);
        else if (_clicks == 3) { _selAnchor = (0, abs); _selEnd = (Columns - 1, abs); }
        else { _selAnchor = (c, abs); _selEnd = (c, abs); _selecting = true; e.Pointer.Capture(this); }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_session is null) return;
        var point = e.GetCurrentPoint(this);
        if (_selecting)
        {
            _selEnd = Absolute(point.Position);
            if (point.Position.Y < 0) Scroll(1); else if (point.Position.Y > Bounds.Height) Scroll(-1);   // dragging past the edge scrolls
            InvalidateVisual();
            return;
        }
        if (ProgramWantsMouse(e.KeyModifiers) && _session.Emulator.Modes.Mouse is MouseTracking.Drag or MouseTracking.Any)
        {
            var (col, row) = GridCell(point.Position);
            var pressed = point.Properties.IsLeftButtonPressed ? KeyEncoder.MouseButton.Left : point.Properties.IsMiddleButtonPressed ? KeyEncoder.MouseButton.Middle : point.Properties.IsRightButtonPressed ? KeyEncoder.MouseButton.Right : KeyEncoder.MouseButton.None;
            Send(KeyEncoder.EncodeMouse(pressed, false, true, col, row, Mods(e.KeyModifiers), _session.Emulator.Modes));
            return;
        }
        Cursor = new Cursor(e.KeyModifiers.HasFlag(KeyModifiers.Control) && LinkAt(point.Position) is not null ? StandardCursorType.Hand : StandardCursorType.Ibeam);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_session is null) return;
        e.Pointer.Capture(null);
        if (_selecting)
        {
            _selecting = false;
            if (_selAnchor == _selEnd) ClearSelection();
            else if (CopyOnSelect) _ = CopyAsync();
            return;
        }
        if (ProgramWantsMouse(e.KeyModifiers))
        {
            var (col, row) = GridCell(e.GetPosition(this));
            Send(KeyEncoder.EncodeMouse(KeyEncoder.MouseButton.Left, true, false, col, row, Mods(e.KeyModifiers), _session.Emulator.Modes));
        }
    }

    private async Task PasteSelectionOrClipboard()
    {
        if (SelectedText is { Length: > 0 } text) Paste(text);
        else await PasteAsync();
    }

    private string? LinkAt(Point p)
    {
        if (_session is null) return null;
        var em = _session.Emulator;
        var (c, abs) = Absolute(p);
        lock (em.SyncRoot)
        {
            if (abs < 0 || abs >= em.ScrollbackCount + em.Rows) return null;
            var line = em.GetHistoryLine(abs);
            return c < line.Cells.Length && line.Cells[c].Link != 0 ? em.Links[line.Cells[c].Link] : null;
        }
    }

    private void SelectWord(int col, int abs)
    {
        if (_session is null) return;
        var em = _session.Emulator;
        lock (em.SyncRoot)
        {
            if (abs < 0 || abs >= em.ScrollbackCount + em.Rows) return;
            var line = em.GetHistoryLine(abs);
            static bool Word(TermCell c) => !c.IsEmpty && !char.IsWhiteSpace((char)Math.Min(c.Rune, 0xFFFF)) && "\"'()[]{}<>|;,".IndexOf((char)Math.Min(c.Rune, 0xFFFF)) < 0;
            if (col >= line.Cells.Length || !Word(line.Cells[col])) { _selAnchor = _selEnd = (col, abs); return; }
            var a = col; var b = col;
            while (a > 0 && Word(line.Cells[a - 1])) a--;
            while (b < line.Cells.Length - 1 && Word(line.Cells[b + 1])) b++;
            _selAnchor = (a, abs); _selEnd = (b, abs);
        }
    }

    public void SelectAll()
    {
        if (_session is null) return;
        var em = _session.Emulator;
        _selAnchor = (0, 0);
        _selEnd = (em.Cols - 1, em.ScrollbackCount + em.Rows - 1);
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (_selAnchor is null) return;
        _selAnchor = _selEnd = null;
        InvalidateVisual();
    }

    private ((int Col, int Row) A, (int Col, int Row) B)? NormalizedSelection()
    {
        if (_selAnchor is not { } a || _selEnd is not { } b || a == b) return null;
        return a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col) ? (a, b) : (b, a);
    }

    // For tests and for programs driving the view.
    public void SetSelection((int Col, int Row) from, (int Col, int Row) to) { _selAnchor = from; _selEnd = to; InvalidateVisual(); }

    private void ShowMenu()
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Copy", IsEnabled = SelectedText.Length > 0, InputGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift) };
        copy.Click += async (_, _) => await CopyAsync();
        var paste = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift) };
        paste.Click += async (_, _) => await PasteAsync();
        var all = new MenuItem { Header = "Select all" };
        all.Click += (_, _) => SelectAll();
        var previous = new MenuItem { Header = "Previous prompt", IsEnabled = _session?.Emulator.ShellIntegrationActive == true };
        previous.Click += (_, _) => JumpToPrompt(true);
        menu.Items.Add(copy); menu.Items.Add(paste); menu.Items.Add(all);
        if (_session?.Emulator.ShellIntegrationActive == true) menu.Items.Add(previous);
        menu.Open(this);
    }
}
