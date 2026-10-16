namespace Vde.Terminal;

public enum MouseTracking { Off, Click, Drag, Any }          // 1000, 1002, 1003
public enum MouseEncoding { X10, Utf8, Sgr, Urxvt }          // default, 1005, 1006, 1015
public enum CursorShape { Block, Underline, Bar }

// What programs can switch on and off, read by the view and the key encoder.
public sealed class TerminalModes
{
    public bool ApplicationCursorKeys;   // DECCKM (1): arrows send ESC O A instead of ESC [ A
    public bool ApplicationKeypad;       // DECKPAM
    public bool AutoWrap = true;         // DECAWM (7)
    public bool OriginMode;              // DECOM (6)
    public bool InsertMode;              // IRM (4)
    public bool NewLineMode;             // LNM (20)
    public bool ReverseVideo;            // DECSCNM (5)
    public bool CursorVisible = true;    // DECTCEM (25)
    public bool CursorBlink = true;      // (12)
    public bool BracketedPaste;          // (2004)
    public bool FocusEvents;             // (1004)
    public bool AlternateScreen;         // (47/1047/1049)
    public MouseTracking Mouse;
    public MouseEncoding MouseEncoding;
    public CursorShape Cursor = CursorShape.Block;
}
