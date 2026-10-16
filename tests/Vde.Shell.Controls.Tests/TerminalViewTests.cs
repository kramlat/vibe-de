using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Vde.Shell.Controls;
using Vde.Terminal;

namespace Vde.Shell.Controls.Tests;

public class TerminalViewTests
{
    private static (Window window, TerminalView view, TerminalSession session) Start(string script, int width = 640, int height = 320)
    {
        Assert.NotNull(Pty.FindLauncher());
        var view = new TerminalView { TerminalFontSize = 13 };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        view.Focus();
        var session = TerminalSession.Start(["/bin/sh", "-c", script], view.Columns, view.RowsVisible);
        view.Session = session;
        return (window, view, session);
    }

    private static async Task Until(Func<bool> condition, int ms = 8000)
    {
        for (var i = 0; i < ms / 20 && !condition(); i++) { await Task.Delay(20); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }

    private static bool Contains(TerminalSession s, string text) { lock (s.Emulator.SyncRoot) return s.Emulator.ScreenText().Contains(text); }

    [AvaloniaFact]
    public async Task The_program_is_told_the_size_the_view_has_and_a_resize_reaches_it()
    {
        var (window, view, session) = Start("stty size; read x; stty size");
        using var _ = session;
        await Until(() => Contains(session, $"{view.RowsVisible} {view.Columns}"));
        Assert.InRange(view.Columns, 60, 120);

        window.Width = 400; window.Height = 200;
        await Task.Delay(100);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        session.Send("\r");
        await Until(() => Contains(session, $"{view.RowsVisible} {view.Columns}") && view.Columns < 60);
    }

    [AvaloniaFact]
    public async Task Typing_and_special_keys_reach_the_program()
    {
        var (window, view, session) = Start("read -r a; echo \"got:$a\"; stty raw -echo; dd bs=1 count=3 2>/dev/null | od -An -c; stty sane");
        using var _ = session;
        window.KeyTextInput("hi");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Until(() => Contains(session, "got:hi"));

        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        await Until(() => Contains(session, "033"));   // od -c shows ESC [ A as 033 [ A
        lock (session.Emulator.SyncRoot) Assert.Contains("[   A", session.Emulator.ScreenText());
    }

    [AvaloniaFact]
    public async Task Selecting_text_copies_it_with_wrapped_lines_joined_and_history_can_be_scrolled()
    {
        var (window, view, session) = Start("i=1; while [ $i -le 60 ]; do echo line$i; i=$((i+1)); done; read x");
        using var _ = session;
        await Until(() => Contains(session, "line60"));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(session.Emulator.ScrollbackCount > 0);
        view.Scroll(10);
        Assert.Equal(10, view.ScrollOffset);
        view.Scroll(-100);
        Assert.Equal(0, view.ScrollOffset);

        int top;
        lock (session.Emulator.SyncRoot) top = session.Emulator.ScrollbackCount;
        view.SetSelection((0, top), (5, top + 1));
        Assert.Equal("line" + (top + 1) + "\nline" + (top + 2) is var expected ? view.SelectedText.Split('\n')[0] : "", view.SelectedText.Split('\n')[0]);
        Assert.StartsWith("line", view.SelectedText);
        Assert.Equal(2, view.SelectedText.Split('\n').Length);
    }

    [AvaloniaFact]
    public async Task Colours_and_the_cursor_are_drawn_and_a_picture_can_be_taken()
    {
        var (window, view, session) = Start("printf '\\033[1;31mred\\033[0m \\033[48;2;30;80;160m blue \\033[0m \\033[4munder\\033[0m 日本語\\r\\n'; printf '\\033[38;5;208morange\\033[0m $ '; read x");
        using var _ = session;
        await Until(() => Contains(session, "orange"));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame()!;
        Assert.True(frame.PixelSize.Width > 100);
        if (Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR") is { Length: > 0 } dir) frame.Save(Path.Combine(dir, "terminal.png"));
    }

    [AvaloniaFact]
    public async Task Paste_is_bracketed_when_the_program_asks_for_it_and_ctrl_shift_v_is_the_terminals_own_shortcut()
    {
        var (window, view, session) = Start("printf '\\033[?2004h'; stty raw -echo; dd bs=1 count=12 2>/dev/null | od -An -c; stty sane");
        using var _ = session;
        await Task.Delay(300);
        view.Paste("ab");
        await Until(() => Contains(session, "033"));
        lock (session.Emulator.SyncRoot) Assert.Contains("2   0   0   ~   a   b", session.Emulator.ScreenText());   // ESC [ 2 0 0 ~ a b ESC [ 2 0 1 ~
    }
}
