using System.Text;

namespace Vde.Terminal;

// The screen and the escape-sequence interpreter: bytes a program wrote go in (Feed), a grid of cells comes out, and
// what the terminal must answer (cursor position reports, device attributes) comes back through Respond.
// Understands what xterm-compatible programs use: SGR with 256 and true colour, cursor movement, scroll regions,
// insert/delete, the alternate screen, DEC line-drawing, bracketed paste, mouse modes, titles (OSC 0/2), working
// directory (OSC 7) and hyperlinks (OSC 8). Not thread-safe: lock SyncRoot while feeding and while reading cells.
public sealed class Emulator
{
    public const int MaxScrollback = 10_000;

    private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, Dcs, IgnoredString, StringEscape }

    private struct Saved { public int X, Y; public TermColor Fg, Bg; public CellAttrs Attrs; public bool G0Graphics, G1Graphics, Shift, Origin, Wrap; }

    private readonly List<Line> _scrollback = new();
    private readonly List<string> _links = new() { "" };
    private Line[] _main, _alt, _screen;
    private bool[] _tabs;
    private State _state = State.Ground;
    private int _x, _y, _top, _bottom;
    private bool _wrapPending;
    private TermColor _fg, _bg;
    private CellAttrs _attrs;
    private ushort _link;
    private bool _g0Graphics, _g1Graphics, _shifted;
    private Saved _savedMain, _savedAlt;
    private int _lastPrinted;

    // parser
    private readonly int[] _params = new int[32];
    private readonly bool[] _colon = new bool[32];
    private int _paramCount;
    private bool _paramStarted;
    private char _marker;                 // '?', '>', '<', '=' right after CSI
    private readonly StringBuilder _intermediates = new();
    private readonly StringBuilder _string = new();
    private int _utf8Need, _utf8Cp;

    public Emulator(int columns = 80, int rows = 24)
    {
        Cols = Math.Max(columns, 1);
        Rows = Math.Max(rows, 1);
        _main = NewScreen();
        _alt = NewScreen();
        _screen = _main;
        _tabs = DefaultTabs(Cols);
        _top = 0;
        _bottom = Rows - 1;
    }

    public object SyncRoot { get; } = new();
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public TerminalModes Modes { get; } = new();
    public string Title { get; private set; } = "";
    public string? WorkingDirectory { get; private set; }   // from OSC 7
    public int CursorX => _x;
    public int CursorY => _y;
    public bool CursorPendingWrap => _wrapPending;
    public int ScrollbackCount => _scrollback.Count;
    public IReadOnlyList<string> Links => _links;
    // Bumped whenever the picture changed, so a view can skip frames in which nothing happened.
    public long Version { get; private set; }

    public event Action<byte[]>? Respond;   // bytes to send back to the program
    public event Action<string>? TitleChanged;
    public event Action? Bell;
    public event Action<string>? WorkingDirectoryChanged;

    // ---------------- reading the screen ----------------

    public Line GetLine(int row) => _screen[row];

    // A row counted from the top of the scrollback: 0 .. ScrollbackCount-1 is history, then the screen.
    public Line GetHistoryLine(int absoluteRow)
        => absoluteRow < _scrollback.Count ? _scrollback[absoluteRow] : _screen[absoluteRow - _scrollback.Count];

    public string ScreenText()
        => string.Join('\n', Enumerable.Range(0, Rows).Select(r => _screen[r].Text()));

    // ---------------- feeding ----------------

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (_utf8Need > 0)
            {
                if ((b & 0xC0) == 0x80)
                {
                    _utf8Cp = _utf8Cp << 6 | (b & 0x3F);
                    if (--_utf8Need == 0) Process(_utf8Cp > 0x10FFFF || (_utf8Cp >= 0xD800 && _utf8Cp <= 0xDFFF) ? 0xFFFD : _utf8Cp);
                    continue;
                }
                _utf8Need = 0;
                Process(0xFFFD);   // a broken sequence: show it as such and treat this byte normally
            }
            if (b < 0x80) Process(b);
            else if ((b & 0xE0) == 0xC0) { _utf8Need = 1; _utf8Cp = b & 0x1F; }
            else if ((b & 0xF0) == 0xE0) { _utf8Need = 2; _utf8Cp = b & 0x0F; }
            else if ((b & 0xF8) == 0xF0) { _utf8Need = 3; _utf8Cp = b & 0x07; }
            else Process(0xFFFD);
        }
        Version++;
    }

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    private void Process(int cp)
    {
        // These work in every state.
        if (cp == 0x18 || cp == 0x1A) { _state = State.Ground; return; }
        if (cp == 0x1B)
        {
            if (_state is State.Osc or State.Dcs or State.IgnoredString) { _stringState = _state; _state = State.StringEscape; return; }
            _state = State.Escape;
            _intermediates.Clear();
            return;
        }

        switch (_state)
        {
            case State.Ground:
                if (cp < 0x20 || cp == 0x7F) Execute(cp);
                else Print(cp);
                break;
            case State.Escape:
                if (cp < 0x20) { Execute(cp); break; }
                switch ((char)cp)
                {
                    case '[': _state = State.Csi; ResetParams(); break;
                    case ']': _state = State.Osc; _string.Clear(); break;
                    case 'P': _state = State.Dcs; _string.Clear(); break;
                    case '_': case '^': case 'X': _state = State.IgnoredString; break;
                    default:
                        if (cp >= 0x20 && cp <= 0x2F) { _intermediates.Append((char)cp); _state = State.EscapeIntermediate; }
                        else { EscDispatch((char)cp); _state = State.Ground; }
                        break;
                }
                break;
            case State.EscapeIntermediate:
                if (cp >= 0x20 && cp <= 0x2F) _intermediates.Append((char)cp);
                else if (cp < 0x20) Execute(cp);
                else { EscDispatch((char)cp); _state = State.Ground; }
                break;
            case State.Csi:
                if (cp < 0x20) { Execute(cp); break; }
                if (cp >= 0x30 && cp <= 0x39) { AddDigit(cp - '0'); break; }
                if (cp == ';') { NextParam(false); break; }
                if (cp == ':') { NextParam(true); break; }
                if (cp >= 0x3C && cp <= 0x3F && _paramCount == 0 && !_paramStarted && _intermediates.Length == 0) { _marker = (char)cp; break; }
                if (cp >= 0x20 && cp <= 0x2F) { _intermediates.Append((char)cp); break; }
                if (cp >= 0x40 && cp <= 0x7E) { if (_paramStarted || _paramCount > 0) NextParam(false, final: true); CsiDispatch((char)cp); _state = State.Ground; break; }
                _state = State.Ground;   // garbage: give up on the sequence
                break;
            case State.Osc:
                if (cp == 0x07) { OscDispatch(); _state = State.Ground; }
                else if (cp >= 0x20) { if (_string.Length < 4096) _string.Append(char.ConvertFromUtf32(cp)); }
                break;
            case State.Dcs:
            case State.IgnoredString:
                if (cp == 0x07 && _state == State.IgnoredString) _state = State.Ground;
                break;
            case State.StringEscape:
                if (cp == '\\')   // ST ends the string
                {
                    _state = State.Ground;
                    if (_stringState == State.Osc) OscDispatch();
                }
                else
                {
                    _state = State.Ground;
                    Process(0x1B);      // not a string terminator: a new escape sequence starts
                    Process(cp);
                }
                break;
        }
    }

    private State _stringState = State.Ground;

    // ---------------- C0 controls ----------------

    private void Execute(int c)
    {
        switch (c)
        {
            case 0x07: Bell?.Invoke(); break;
            case 0x08: if (_x > 0) _x--; _wrapPending = false; break;
            case 0x09: TabForward(1); break;
            case 0x0A: case 0x0B: case 0x0C: LineFeed(); if (Modes.NewLineMode) CarriageReturn(); break;
            case 0x0D: CarriageReturn(); break;
            case 0x0E: _shifted = true; break;
            case 0x0F: _shifted = false; break;
        }
    }

    private void CarriageReturn() { _x = 0; _wrapPending = false; }

    private void LineFeed()
    {
        _wrapPending = false;
        if (_y == _bottom) ScrollUp(1);
        else if (_y < Rows - 1) _y++;
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (_y == _top) ScrollDown(1);
        else if (_y > 0) _y--;
    }

    private void TabForward(int n)
    {
        _wrapPending = false;
        for (; n > 0; n--)
        {
            do _x++; while (_x < Cols - 1 && !_tabs[_x]);
            if (_x >= Cols - 1) { _x = Cols - 1; break; }
        }
    }

    private void TabBackward(int n)
    {
        _wrapPending = false;
        for (; n > 0; n--)
        {
            do _x--; while (_x > 0 && !_tabs[_x]);
            if (_x <= 0) { _x = 0; break; }
        }
    }

    // ---------------- printing ----------------

    private static readonly char[] Graphics = "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·".ToCharArray();   // 0x60 .. 0x7E

    private void Print(int cp)
    {
        if ((_shifted ? _g1Graphics : _g0Graphics) && cp >= 0x60 && cp <= 0x7E)
            cp = Graphics[cp - 0x60];
        var width = CharWidth.Of(cp);
        if (width == 0)
        {
            AttachCombining(cp);
            return;
        }
        _lastPrinted = cp;
        if (_wrapPending)
        {
            if (Modes.AutoWrap)
            {
                _screen[_y].Wrapped = true;
                CarriageReturn();
                LineFeed();
            }
            else _wrapPending = false;
        }
        if (width == 2 && _x == Cols - 1)
        {
            if (!Modes.AutoWrap) width = 1;
            else
            {
                ClearWideAt(_y, _x);
                _screen[_y].Cells[_x] = BlankCell();
                _screen[_y].Wrapped = true;
                CarriageReturn();
                LineFeed();
            }
        }
        var line = _screen[_y];
        if (Modes.InsertMode) InsertCells(width);
        ClearWideAt(_y, _x);
        if (width == 2) ClearWideAt(_y, _x + 1);
        line.Cells[_x] = new Cell { Rune = cp, Fg = _fg, Bg = _bg, Attrs = _attrs, Link = _link, Width = (byte)width };
        if (width == 2 && _x + 1 < Cols)
            line.Cells[_x + 1] = new Cell { Fg = _fg, Bg = _bg, Attrs = _attrs, Link = _link, Width = 0 };
        _x += width;
        if (_x >= Cols) { _x = Cols - 1; _wrapPending = true; }
    }

    private void AttachCombining(int cp)
    {
        var x = _wrapPending ? _x : _x - 1;
        if (x < 0) return;
        var line = _screen[_y];
        if (line.Cells[x].Width == 0 && x > 0) x--;   // the second half of a wide character: the mark belongs to the first
        if (line.Cells[x].IsEmpty) return;
        line.Cells[x].Combining += char.ConvertFromUtf32(cp);
    }

    // Overwriting one half of a wide character blanks the other half.
    private void ClearWideAt(int y, int x)
    {
        if (x < 0 || x >= Cols) return;
        var cells = _screen[y].Cells;
        if (cells[x].Width == 2 && x + 1 < Cols) cells[x + 1] = BlankCell();
        else if (cells[x].Width == 0 && x > 0 && cells[x - 1].Width == 2) cells[x - 1] = BlankCell();
    }

    private Cell BlankCell() => new() { Bg = _bg, Width = 1 };

    private void InsertCells(int n)
    {
        var cells = _screen[_y].Cells;
        n = Math.Min(n, Cols - _x);
        Array.Copy(cells, _x, cells, _x + n, Cols - _x - n);
        for (var i = 0; i < n; i++) cells[_x + i] = BlankCell();
    }

    // ---------------- scrolling and erasing ----------------

    private Line BlankLine()
    {
        var line = new Line(Cols);
        if (_bg != TermColor.Default)
            for (var i = 0; i < Cols; i++) line.Cells[i] = BlankCell();
        return line;
    }

    private void ScrollUp(int n)
    {
        n = Math.Min(n, _bottom - _top + 1);
        for (var i = 0; i < n; i++)
        {
            if (_top == 0 && _screen == _main)
            {
                _scrollback.Add(_screen[0]);
                if (_scrollback.Count > MaxScrollback) _scrollback.RemoveAt(0);
            }
            Array.Copy(_screen, _top + 1, _screen, _top, _bottom - _top);
            _screen[_bottom] = BlankLine();
        }
    }

    private void ScrollDown(int n)
    {
        n = Math.Min(n, _bottom - _top + 1);
        for (var i = 0; i < n; i++)
        {
            Array.Copy(_screen, _top, _screen, _top + 1, _bottom - _top);
            _screen[_top] = BlankLine();
        }
    }

    private void EraseCells(int y, int from, int to)
    {
        from = Math.Max(from, 0);
        to = Math.Min(to, Cols - 1);
        var cells = _screen[y].Cells;
        if (from > to) return;
        if (from > 0 && cells[from].Width == 0) cells[from - 1] = BlankCell();
        if (to + 1 < Cols && cells[to].Width == 2) cells[to + 1] = BlankCell();
        for (var i = from; i <= to; i++) cells[i] = BlankCell();
        if (to == Cols - 1) _screen[y].Wrapped = false;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseCells(_y, _x, Cols - 1);
                for (var r = _y + 1; r < Rows; r++) EraseCells(r, 0, Cols - 1);
                break;
            case 1:
                for (var r = 0; r < _y; r++) EraseCells(r, 0, Cols - 1);
                EraseCells(_y, 0, _x);
                break;
            case 2:
                for (var r = 0; r < Rows; r++) EraseCells(r, 0, Cols - 1);
                break;
            case 3: _scrollback.Clear(); break;
        }
    }

    private void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0: EraseCells(_y, _x, Cols - 1); break;
            case 1: EraseCells(_y, 0, _x); break;
            case 2: EraseCells(_y, 0, Cols - 1); break;
        }
    }

    private void InsertLines(int n)
    {
        if (_y < _top || _y > _bottom) return;
        n = Math.Min(n, _bottom - _y + 1);
        for (var i = 0; i < n; i++)
        {
            Array.Copy(_screen, _y, _screen, _y + 1, _bottom - _y);
            _screen[_y] = BlankLine();
        }
        _x = 0; _wrapPending = false;
    }

    private void DeleteLines(int n)
    {
        if (_y < _top || _y > _bottom) return;
        n = Math.Min(n, _bottom - _y + 1);
        for (var i = 0; i < n; i++)
        {
            Array.Copy(_screen, _y + 1, _screen, _y, _bottom - _y);
            _screen[_bottom] = BlankLine();
        }
        _x = 0; _wrapPending = false;
    }

    private void DeleteChars(int n)
    {
        var cells = _screen[_y].Cells;
        n = Math.Min(n, Cols - _x);
        ClearWideAt(_y, _x);
        Array.Copy(cells, _x + n, cells, _x, Cols - _x - n);
        for (var i = Cols - n; i < Cols; i++) cells[i] = BlankCell();
        _wrapPending = false;
    }

    // ---------------- cursor ----------------

    private void MoveTo(int row, int col)   // 0-based; row is relative to the margins in origin mode
    {
        if (Modes.OriginMode)
        {
            _y = Math.Clamp(row + _top, _top, _bottom);
        }
        else _y = Math.Clamp(row, 0, Rows - 1);
        _x = Math.Clamp(col, 0, Cols - 1);
        _wrapPending = false;
    }

    private void Save(ref Saved s)
        => s = new Saved { X = _x, Y = _y, Fg = _fg, Bg = _bg, Attrs = _attrs, G0Graphics = _g0Graphics, G1Graphics = _g1Graphics, Shift = _shifted, Origin = Modes.OriginMode, Wrap = _wrapPending };

    private void Restore(in Saved s)
    {
        _x = Math.Min(s.X, Cols - 1); _y = Math.Min(s.Y, Rows - 1);
        _fg = s.Fg; _bg = s.Bg; _attrs = s.Attrs; _g0Graphics = s.G0Graphics; _g1Graphics = s.G1Graphics; _shifted = s.Shift;
        Modes.OriginMode = s.Origin; _wrapPending = s.Wrap;
    }

    // ---------------- ESC sequences ----------------

    private void EscDispatch(char c)
    {
        if (_intermediates.Length > 0)
        {
            var i = _intermediates[0];
            if (i == '(' ) _g0Graphics = c == '0';
            else if (i == ')') _g1Graphics = c == '0';
            else if (i == '#' && c == '8')   // DECALN: the alignment test fills the screen with E
                for (var r = 0; r < Rows; r++)
                    for (var col = 0; col < Cols; col++) _screen[r].Cells[col] = new Cell { Rune = 'E', Width = 1 };
            return;
        }
        switch (c)
        {
            case '7': Save(ref (_screen == _main ? ref _savedMain : ref _savedAlt)); break;
            case '8': Restore(_screen == _main ? _savedMain : _savedAlt); break;
            case 'D': LineFeed(); break;
            case 'E': CarriageReturn(); LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'H': _tabs[Math.Min(_x, Cols - 1)] = true; break;
            case '=': Modes.ApplicationKeypad = true; break;
            case '>': Modes.ApplicationKeypad = false; break;
            case 'c': FullReset(); break;
        }
    }

    public void FullReset()
    {
        var keepTitle = Title;
        Modes.ApplicationCursorKeys = Modes.ApplicationKeypad = Modes.InsertMode = Modes.NewLineMode = Modes.ReverseVideo = Modes.OriginMode = false;
        Modes.AutoWrap = true; Modes.CursorVisible = true; Modes.BracketedPaste = Modes.FocusEvents = false;
        Modes.Mouse = MouseTracking.Off; Modes.MouseEncoding = MouseEncoding.X10; Modes.Cursor = CursorShape.Block;
        if (Modes.AlternateScreen) SwitchScreen(false);
        _fg = _bg = default; _attrs = 0; _link = 0; _g0Graphics = _g1Graphics = _shifted = false;
        _top = 0; _bottom = Rows - 1;
        _tabs = DefaultTabs(Cols);
        _x = _y = 0; _wrapPending = false;
        EraseDisplay(2);
        Title = keepTitle;
    }

    // ---------------- CSI ----------------

    private void ResetParams()
    {
        _paramCount = 0; _paramStarted = false; _marker = '\0'; _intermediates.Clear();
        Array.Clear(_params); Array.Clear(_colon);
    }

    private void AddDigit(int d)
    {
        if (_paramCount >= _params.Length) return;
        _paramStarted = true;
        _params[_paramCount] = Math.Min(_params[_paramCount] * 10 + d, 65535);
    }

    private void NextParam(bool colon, bool final = false)
    {
        if (_paramCount < _params.Length) _paramCount++;
        _paramStarted = false;
        if (!final && _paramCount < _params.Length) _colon[_paramCount] = colon;   // the separator that led to this parameter
    }

    private int P(int i, int def = 0) => i < _paramCount && _params[i] != 0 ? _params[i] : def;
    private int Pn(int i) => P(i, 1);

    private void CsiDispatch(char c)
    {
        var inter = _intermediates.ToString();
        if (inter.Length > 0)
        {
            if (inter == " " && c == 'q') SetCursorStyle(P(0));
            else if (inter == "!" && c == 'p') SoftReset();
            return;
        }
        if (_marker == '?')
        {
            if (c == 'h' || c == 'l') for (var i = 0; i < Math.Max(_paramCount, 1); i++) SetPrivateMode(_params[i], c == 'h');
            else if (c == 'n') PrivateReport(P(0));
            return;
        }
        if (_marker == '>')
        {
            if (c == 'c') Reply("\e[>0;10;0c");   // secondary device attributes: a VT100-class terminal, version 10
            return;
        }
        if (_marker != '\0') return;

        switch (c)
        {
            case 'A': MoveTo(_y - Pn(0) - (Modes.OriginMode ? _top : 0), _x); ClampUp(); break;
            case 'B': case 'e': MoveTo(_y + Pn(0) - (Modes.OriginMode ? _top : 0), _x); break;
            case 'C': case 'a': _x = Math.Min(_x + Pn(0), Cols - 1); _wrapPending = false; break;
            case 'D': _x = Math.Max(_x - Pn(0), 0); _wrapPending = false; break;
            case 'E': MoveTo(_y + Pn(0) - (Modes.OriginMode ? _top : 0), 0); break;
            case 'F': MoveTo(_y - Pn(0) - (Modes.OriginMode ? _top : 0), 0); ClampUp(); break;
            case 'G': case '`': MoveTo(_y - (Modes.OriginMode ? _top : 0), Pn(0) - 1); break;
            case 'H': case 'f': MoveTo(Pn(0) - 1, Pn(1) - 1); break;
            case 'd': MoveTo(Pn(0) - 1, _x); break;
            case 'I': TabForward(Pn(0)); break;
            case 'Z': TabBackward(Pn(0)); break;
            case 'J': EraseDisplay(P(0)); break;
            case 'K': EraseLine(P(0)); break;
            case 'L': InsertLines(Pn(0)); break;
            case 'M': DeleteLines(Pn(0)); break;
            case '@': InsertCells(Math.Min(Pn(0), Cols - _x)); break;
            case 'P': DeleteChars(Pn(0)); break;
            case 'X': EraseCells(_y, _x, _x + Pn(0) - 1); break;
            case 'S': ScrollUp(Pn(0)); break;
            case 'T': ScrollDown(Pn(0)); break;
            case 'b': for (var i = Pn(0); i > 0 && _lastPrinted != 0; i--) Print(_lastPrinted); break;
            case 'g': if (P(0) == 3) Array.Clear(_tabs); else if (P(0) == 0) _tabs[Math.Min(_x, Cols - 1)] = false; break;
            case 'm': Sgr(); break;
            case 'r':
                var top = Pn(0) - 1; var bottom = P(1, Rows) - 1;
                if (top < bottom && bottom < Rows) { _top = top; _bottom = bottom; MoveTo(0, 0); }
                break;
            case 's': Save(ref (_screen == _main ? ref _savedMain : ref _savedAlt)); break;
            case 'u': Restore(_screen == _main ? _savedMain : _savedAlt); break;
            case 'h': case 'l':
                for (var i = 0; i < Math.Max(_paramCount, 1); i++)
                    if (_params[i] == 4) Modes.InsertMode = c == 'h';
                    else if (_params[i] == 20) Modes.NewLineMode = c == 'h';
                break;
            case 'n':
                if (P(0) == 5) Reply("\e[0n");
                else if (P(0) == 6) Reply($"\e[{(Modes.OriginMode ? _y - _top : _y) + 1};{_x + 1}R");
                break;
            case 'c': if (P(0) == 0) Reply("\e[?62;22c"); break;   // VT220 with ANSI colour
            case 't':
                if (P(0) == 18) Reply($"\e[8;{Rows};{Cols}t");
                break;
        }
    }

    private void ClampUp() { if (Modes.OriginMode) _y = Math.Max(_y, _top); }

    private void Reply(string text) => Respond?.Invoke(Encoding.UTF8.GetBytes(text));

    private void PrivateReport(int n)
    {
        if (n == 6) Reply($"\e[?{(Modes.OriginMode ? _y - _top : _y) + 1};{_x + 1}R");
    }

    private void SoftReset()
    {
        Modes.InsertMode = Modes.OriginMode = false; Modes.AutoWrap = true; Modes.CursorVisible = true; Modes.ApplicationCursorKeys = false;
        _top = 0; _bottom = Rows - 1; _fg = _bg = default; _attrs = 0; _g0Graphics = _g1Graphics = _shifted = false;
    }

    private void SetCursorStyle(int n)
    {
        Modes.Cursor = n switch { 3 or 4 => CursorShape.Underline, 5 or 6 => CursorShape.Bar, _ => CursorShape.Block };
        Modes.CursorBlink = n is 0 or 1 or 3 or 5;
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: Modes.ApplicationCursorKeys = on; break;
            case 5: Modes.ReverseVideo = on; break;
            case 6: Modes.OriginMode = on; MoveTo(0, 0); break;
            case 7: Modes.AutoWrap = on; break;
            case 12: Modes.CursorBlink = on; break;
            case 25: Modes.CursorVisible = on; break;
            case 47: case 1047: SwitchScreen(on); break;
            case 1048: if (on) Save(ref _savedMain); else Restore(_savedMain); break;
            case 1049:
                if (on) { Save(ref _savedMain); SwitchScreen(true); EraseDisplay(2); }
                else { SwitchScreen(false); Restore(_savedMain); }
                break;
            case 1000: Modes.Mouse = on ? MouseTracking.Click : MouseTracking.Off; break;
            case 1002: Modes.Mouse = on ? MouseTracking.Drag : MouseTracking.Off; break;
            case 1003: Modes.Mouse = on ? MouseTracking.Any : MouseTracking.Off; break;
            case 1004: Modes.FocusEvents = on; break;
            case 1005: Modes.MouseEncoding = on ? MouseEncoding.Utf8 : MouseEncoding.X10; break;
            case 1006: Modes.MouseEncoding = on ? MouseEncoding.Sgr : MouseEncoding.X10; break;
            case 1015: Modes.MouseEncoding = on ? MouseEncoding.Urxvt : MouseEncoding.X10; break;
            case 2004: Modes.BracketedPaste = on; break;
        }
    }

    private void SwitchScreen(bool alternate)
    {
        if (alternate == Modes.AlternateScreen) return;
        Modes.AlternateScreen = alternate;
        _screen = alternate ? _alt : _main;
        _top = 0; _bottom = Rows - 1;
        if (alternate) foreach (var line in _alt) Array.Clear(line.Cells);
        _wrapPending = false;
    }

    private void Sgr()
    {
        if (_paramCount == 0) { _fg = _bg = default; _attrs = 0; return; }
        for (var i = 0; i < _paramCount; i++)
        {
            var p = _params[i];
            switch (p)
            {
                case 0: _fg = _bg = default; _attrs = 0; break;
                case 1: _attrs |= CellAttrs.Bold; break;
                case 2: _attrs |= CellAttrs.Faint; break;
                case 3: _attrs |= CellAttrs.Italic; break;
                case 4: _attrs |= CellAttrs.Underline; if (i + 1 < _paramCount && _colon[i + 1]) { if (_params[i + 1] == 0) _attrs &= ~CellAttrs.Underline; i++; } break;
                case 5: case 6: _attrs |= CellAttrs.Blink; break;
                case 7: _attrs |= CellAttrs.Inverse; break;
                case 8: _attrs |= CellAttrs.Invisible; break;
                case 9: _attrs |= CellAttrs.Strike; break;
                case 21: _attrs |= CellAttrs.DoubleUnderline; break;
                case 22: _attrs &= ~(CellAttrs.Bold | CellAttrs.Faint); break;
                case 23: _attrs &= ~CellAttrs.Italic; break;
                case 24: _attrs &= ~(CellAttrs.Underline | CellAttrs.DoubleUnderline); break;
                case 25: _attrs &= ~CellAttrs.Blink; break;
                case 27: _attrs &= ~CellAttrs.Inverse; break;
                case 28: _attrs &= ~CellAttrs.Invisible; break;
                case 29: _attrs &= ~CellAttrs.Strike; break;
                case 53: _attrs |= CellAttrs.Overline; break;
                case 55: _attrs &= ~CellAttrs.Overline; break;
                case >= 30 and <= 37: _fg = TermColor.Index(p - 30); break;
                case 39: _fg = default; break;
                case >= 40 and <= 47: _bg = TermColor.Index(p - 40); break;
                case 49: _bg = default; break;
                case >= 90 and <= 97: _fg = TermColor.Index(p - 90 + 8); break;
                case >= 100 and <= 107: _bg = TermColor.Index(p - 100 + 8); break;
                case 38:
                case 48:
                    if (ExtendedColor(ref i) is { } color) { if (p == 38) _fg = color; else _bg = color; }
                    break;
            }
        }
    }

    // 38;5;n  38;2;r;g;b  and the colon forms 38:5:n  38:2::r:g:b  38:2:r:g:b
    private TermColor? ExtendedColor(ref int i)
    {
        if (i + 1 >= _paramCount) return null;
        var kind = _params[i + 1];
        if (kind == 5 && i + 2 < _paramCount) { var c = TermColor.Index(Math.Clamp(_params[i + 2], 0, 255)); i += 2; return c; }
        if (kind == 2)
        {
            var colon = i + 2 < _paramCount && _colon[i + 2];
            var start = i + 2;
            if (colon && _paramCount - start >= 4) start++;   // a colour-space id sits before r:g:b
            if (start + 2 < _paramCount)
            {
                var c = TermColor.Rgb((byte)Math.Min(_params[start], 255), (byte)Math.Min(_params[start + 1], 255), (byte)Math.Min(_params[start + 2], 255));
                i = start + 2;
                return c;
            }
        }
        return null;
    }

    // ---------------- OSC ----------------

    private void OscDispatch()
    {
        var text = _string.ToString();
        var semi = text.IndexOf(';');
        var code = semi < 0 ? text : text[..semi];
        var arg = semi < 0 ? "" : text[(semi + 1)..];
        switch (code)
        {
            case "0": case "2": Title = arg; TitleChanged?.Invoke(arg); break;
            case "133": ShellIntegration(arg); break;
            case "7":
                if (arg.StartsWith("file://", StringComparison.Ordinal) && arg.IndexOf('/', 7) is > 0 and var slash)   // file://host/path
                {
                    WorkingDirectory = Uri.UnescapeDataString(arg[slash..]);
                    WorkingDirectoryChanged?.Invoke(WorkingDirectory);
                }
                break;
            case "8":   // OSC 8 ; params ; URI  -  an empty URI ends the link
                var uriStart = arg.IndexOf(';');
                var target = uriStart < 0 ? "" : arg[(uriStart + 1)..];
                if (target.Length == 0) _link = 0;
                else
                {
                    var index = _links.IndexOf(target);
                    if (index < 0 && _links.Count < ushort.MaxValue) { _links.Add(target); index = _links.Count - 1; }
                    _link = (ushort)Math.Max(index, 0);
                }
                break;
        }
    }

    // OSC 133 (semantic prompts): A = a prompt starts here, B = it ends and input begins, D;n = the command ended with n.
    // The marks stay on the lines they were set on, through scrolling and into the history.
    private Line? _promptLine;
    private int? _pendingExit;

    public bool ShellIntegrationActive { get; private set; }
    public event Action? PromptStarted;

    private void ShellIntegration(string arg)
    {
        ShellIntegrationActive = true;
        switch (arg.Length > 0 ? arg[0] : '\0')
        {
            case 'A':
                _screen[_y].Mark = LineMark.PromptStart;
                _screen[_y].ExitCode = _pendingExit;
                _promptLine = _screen[_y];
                _pendingExit = null;
                PromptStarted?.Invoke();
                break;
            case 'D':
                _pendingExit = arg.Length > 2 && arg[1] == ';' && int.TryParse(arg.AsSpan(2), out var code) ? code : null;
                break;
        }
    }

    // The line number (counted from the top of the history) of the previous or next prompt before/after `fromAbsolute`,
    // or null. For "jump to previous prompt".
    public int? FindPrompt(int fromAbsolute, bool backwards)
    {
        var total = _scrollback.Count + Rows;
        if (backwards)
        {
            for (var i = Math.Min(fromAbsolute - 1, total - 1); i >= 0; i--)
                if (GetHistoryLine(i).Mark == LineMark.PromptStart) return i;
        }
        else
            for (var i = Math.Max(fromAbsolute + 1, 0); i < total; i++)
                if (GetHistoryLine(i).Mark == LineMark.PromptStart) return i;
        return null;
    }

    // ---------------- size ----------------

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(columns, 1);
        rows = Math.Max(rows, 1);
        if (columns == Cols && rows == Rows) return;

        // the main screen: lose lines from the top (into history) when shrinking below the cursor, and take history back when growing
        var main = _main.ToList();
        if (rows < Rows)
        {
            var drop = Rows - rows;
            var fromTop = _screen == _main ? Math.Min(drop, Math.Max(0, _y - rows + 1)) : 0;
            for (var i = 0; i < fromTop; i++) { _scrollback.Add(main[0]); main.RemoveAt(0); }
            if (_screen == _main) _y -= fromTop;
            main.RemoveRange(rows, main.Count - rows);
        }
        else
        {
            var grow = rows - Rows;
            var pulled = 0;
            while (pulled < grow && _scrollback.Count > 0 && _screen == _main)
            {
                main.Insert(0, _scrollback[^1]);
                _scrollback.RemoveAt(_scrollback.Count - 1);
                pulled++;
            }
            if (_screen == _main) _y += pulled;
            while (main.Count < rows) main.Add(new Line(columns));
        }
        var alt = _alt.ToList();
        while (alt.Count > rows) alt.RemoveAt(alt.Count - 1);
        while (alt.Count < rows) alt.Add(new Line(columns));
        var wasAlt = _screen == _alt;
        _main = main.ToArray();
        _alt = alt.ToArray();
        Cols = columns;
        Rows = rows;
        foreach (var line in _main.Concat(_alt).Concat(_scrollback)) line.Resize(columns);
        _screen = wasAlt ? _alt : _main;
        _tabs = DefaultTabs(columns);
        _top = 0; _bottom = rows - 1;
        _x = Math.Min(_x, columns - 1); _y = Math.Clamp(_y, 0, rows - 1);
        _wrapPending = false;
        Version++;
    }

    private Line[] NewScreen()
    {
        var lines = new Line[Rows];
        for (var i = 0; i < Rows; i++) lines[i] = new Line(Cols);
        return lines;
    }

    private static bool[] DefaultTabs(int columns)
    {
        var tabs = new bool[columns];
        for (var i = 8; i < columns; i += 8) tabs[i] = true;
        return tabs;
    }
}
