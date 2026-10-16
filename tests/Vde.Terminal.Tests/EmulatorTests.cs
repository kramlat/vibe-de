using System.Text;
using Vde.Terminal;

namespace Vde.Terminal.Tests;

public class EmulatorTests
{
    private static Emulator Term(string text = "", int cols = 20, int rows = 5)
    {
        var e = new Emulator(cols, rows);
        e.Feed(text);
        return e;
    }

    private static string Row(Emulator e, int r) => e.GetLine(r).Text();

    [Fact]
    public void Text_lands_on_the_screen_and_wraps_at_the_right_edge()
    {
        var e = Term("hello\r\nworld");
        Assert.Equal("hello", Row(e, 0));
        Assert.Equal("world", Row(e, 1));

        var w = Term(new string('x', 25), cols: 10);
        Assert.Equal("xxxxxxxxxx", Row(w, 0));
        Assert.Equal("xxxxxxxxxxx".Substring(0, 10), Row(w, 1));
        Assert.Equal("xxxxx", Row(w, 2));
        Assert.True(w.GetLine(0).Wrapped);
        Assert.False(w.GetLine(2).Wrapped);
    }

    [Fact]
    public void The_cursor_moves_and_positions_are_clamped()
    {
        var e = Term("\e[3;5HX\e[2AY\e[100;100HZ");
        Assert.Equal('X', (char)e.GetLine(2).Cells[4].Rune);
        Assert.Equal('Y', (char)e.GetLine(0).Cells[5].Rune);
        Assert.Equal('Z', (char)e.GetLine(4).Cells[19].Rune);
    }

    [Fact]
    public void Erasing_clears_parts_of_a_line_and_of_the_screen()
    {
        var e = Term("abcdef\r\nghijkl\e[1;3H\e[K");
        Assert.Equal("ab", Row(e, 0));
        Assert.Equal("ghijkl", Row(e, 1));
        e.Feed("\e[2;3H\e[1J");
        Assert.Equal("", Row(e, 0));
        Assert.Equal("   jkl", Row(e, 1));   // erased up to and including the cursor
        e.Feed("\e[2J");
        Assert.Equal("", e.ScreenText().Replace("\n", ""));
    }

    [Fact]
    public void Colours_and_attributes_are_recorded_per_cell_including_256_and_true_colour()
    {
        var e = Term("\e[1;31mR\e[0m \e[38;5;200mI\e[48;2;1;2;3mT\e[39;49m\e[4;7mU");
        var c = e.GetLine(0).Cells;
        Assert.Equal(TermColor.Index(1), c[0].Fg);
        Assert.True(c[0].Attrs.HasFlag(CellAttrs.Bold));
        Assert.Equal(TermColor.Default, c[2].Fg == default ? default : TermColor.Default);
        Assert.Equal(TermColor.Index(200), c[2].Fg);
        Assert.Equal(TermColor.Rgb(1, 2, 3), c[3].Bg);
        Assert.Equal(TermColor.Index(200), c[3].Fg);
        Assert.Equal(TermColor.Default, c[4].Fg);
        Assert.True(c[4].Attrs.HasFlag(CellAttrs.Underline | CellAttrs.Inverse));
    }

    [Fact]
    public void Colon_separated_colours_work()
    {
        var e = Term("\e[38:2::10:20:30mA\e[38:5:9mB");
        Assert.Equal(TermColor.Rgb(10, 20, 30), e.GetLine(0).Cells[0].Fg);
        Assert.Equal(TermColor.Index(9), e.GetLine(0).Cells[1].Fg);
    }

    [Fact]
    public void Scrolling_fills_the_history_and_a_scroll_region_keeps_the_rest_still()
    {
        var e = Term("1\r\n2\r\n3\r\n4\r\n5\r\n6", rows: 5);
        Assert.Equal(1, e.ScrollbackCount);
        Assert.Equal("1", e.GetHistoryLine(0).Text());
        Assert.Equal("2", Row(e, 0));
        Assert.Equal("6", Row(e, 4));

        var r = Term("top\r\n\e[2;4r\e[2;1Ha\r\nb\r\nc\r\nd", rows: 5);   // margins on rows 2-4
        Assert.Equal("top", Row(r, 0));
        Assert.Equal("b", Row(r, 1));
        Assert.Equal("c", Row(r, 2));
        Assert.Equal("d", Row(r, 3));
        Assert.Equal(0, r.ScrollbackCount);   // a partial region does not feed the history
    }

    [Fact]
    public void Insert_and_delete_lines_and_characters()
    {
        var e = Term("one\r\ntwo\r\nthree\e[2;1H\e[L");
        Assert.Equal("", Row(e, 1));
        Assert.Equal("two", Row(e, 2));
        e.Feed("\e[2;1H\e[M");
        Assert.Equal("two", Row(e, 1));
        e.Feed("\e[1;2H\e[P");
        Assert.Equal("oe", Row(e, 0));
        e.Feed("\e[1;1H\e[2@");
        Assert.Equal("  oe", Row(e, 0));
    }

    [Fact]
    public void The_alternate_screen_is_separate_and_restores_the_main_one_and_the_cursor()
    {
        var e = Term("main\e[2;1H");
        e.Feed("\e[?1049halt");
        Assert.Equal("alt", Row(e, 1));
        Assert.True(e.Modes.AlternateScreen);
        e.Feed("\e[?1049l");
        Assert.Equal("main", Row(e, 0));
        Assert.Equal("", Row(e, 1));
        Assert.Equal(1, e.CursorY);
    }

    [Fact]
    public void Wide_characters_take_two_cells_and_combining_marks_attach_to_the_previous_one()
    {
        var e = Term("日本é");   // a precomposed é first
        e.Feed("é!");
        var c = e.GetLine(0).Cells;
        Assert.Equal(2, c[0].Width);
        Assert.Equal(0, c[1].Width);
        Assert.Equal(2, c[2].Width);
        Assert.Equal("é", c[5].Text);
        Assert.Equal('!', (char)c[6].Rune);
        Assert.Equal("日本é" + "e\u0301!", Row(e, 0));
    }

    [Fact]
    public void Overwriting_half_of_a_wide_character_blanks_the_other_half()
    {
        var e = Term("日\e[1;1HX");
        Assert.Equal('X', (char)e.GetLine(0).Cells[0].Rune);
        Assert.True(e.GetLine(0).Cells[1].IsEmpty);
    }

    [Fact]
    public void UTF8_split_across_chunks_decodes_and_garbage_becomes_a_replacement_character()
    {
        var e = new Emulator(10, 2);
        var bytes = Encoding.UTF8.GetBytes("é€😀");
        foreach (var b in bytes) e.Feed([b]);
        Assert.Equal("é€😀", Row(e, 0));
        e.Feed([0xC3, (byte)'x']);
        Assert.Contains("�x", Row(e, 0));
    }

    [Fact]
    public void Tabs_go_to_every_eighth_column_and_can_be_set_and_cleared()
    {
        var e = Term("a\tb", cols: 30);
        Assert.Equal('b', (char)e.GetLine(0).Cells[8].Rune);
        e.Feed("\e[3g\r\tc");   // all stops cleared: the tab runs to the last column
        Assert.Equal('c', (char)e.GetLine(0).Cells[29].Rune);
    }

    [Fact]
    public void Reports_go_back_to_the_program()
    {
        var e = Term("\e[3;4H");
        var replies = new List<string>();
        e.Respond += b => replies.Add(Encoding.UTF8.GetString(b));
        e.Feed("\e[6n\e[5n\e[c\e[>c\e[18t");
        Assert.Equal(new[] { "\e[3;4R", "\e[0n", "\e[?62;22c", "\e[>0;10;0c", "\e[8;5;20t" }, replies);
    }

    [Fact]
    public void Titles_working_directory_and_hyperlinks_come_from_OSC_sequences_ended_by_BEL_or_ST()
    {
        var e = Term();
        string? title = null, cwd = null;
        e.TitleChanged += t => title = t;
        e.WorkingDirectoryChanged += d => cwd = d;
        e.Feed("\e]0;my title\a\e]7;file://host/home/user/dir\e\\");
        Assert.Equal("my title", title);
        Assert.Equal("/home/user/dir", cwd);
        e.Feed("\e]8;;https://example.org\e\\link\e]8;;\e\\ plain");
        Assert.Equal("https://example.org", e.Links[e.GetLine(0).Cells[0].Link]);
        Assert.Equal(0, e.GetLine(0).Cells[5].Link);
    }

    [Fact]
    public void Modes_can_be_switched_and_the_cursor_style_set()
    {
        var e = Term("\e[?1h\e[?2004h\e[?1006h\e[?1000h\e[?25l\e[5 q");
        Assert.True(e.Modes.ApplicationCursorKeys);
        Assert.True(e.Modes.BracketedPaste);
        Assert.Equal(MouseEncoding.Sgr, e.Modes.MouseEncoding);
        Assert.Equal(MouseTracking.Click, e.Modes.Mouse);
        Assert.False(e.Modes.CursorVisible);
        Assert.Equal(CursorShape.Bar, e.Modes.Cursor);
        Assert.True(e.Modes.CursorBlink);
        e.Feed("\ec");
        Assert.False(e.Modes.ApplicationCursorKeys);
        Assert.True(e.Modes.CursorVisible);
    }

    [Fact]
    public void DEC_line_drawing_maps_the_special_graphics_set()
    {
        var e = Term("\e(0lqk\e(B lqk");
        Assert.Equal("┌─┐ lqk", Row(e, 0));
    }

    [Fact]
    public void Repeat_and_origin_mode_and_save_restore_cursor()
    {
        var e = Term("x\e[3b");
        Assert.Equal("xxxx", Row(e, 0));
        var o = Term("\e[2;4r\e[?6h\e[1;1HZ", rows: 6);
        Assert.Equal('Z', (char)o.GetLine(1).Cells[0].Rune);   // row 1 is relative to the margin
        var s = Term("\e[2;3H\e7\e[1;1H\e8X");
        Assert.Equal('X', (char)s.GetLine(1).Cells[2].Rune);
    }

    [Fact]
    public void Resizing_keeps_the_text_moves_lines_through_history_and_never_throws()
    {
        var e = Term("a\r\nb\r\nc\r\nd\r\ne", rows: 5);
        e.Resize(10, 3);
        Assert.Equal("c", Row(e, 0));
        Assert.Equal("e", Row(e, 2));
        Assert.Equal(2, e.ScrollbackCount);
        e.Resize(10, 5);   // growing takes the history back
        Assert.Equal("a", Row(e, 0));
        Assert.Equal(0, e.ScrollbackCount);
        foreach (var (c, r) in new[] { (1, 1), (200, 60), (3, 9), (80, 24) }) { e.Resize(c, r); e.Feed("日本語text\r\n\e[5;5H"); }
        Assert.Equal(24, e.Rows);
    }

    [Fact]
    public void Random_bytes_never_crash_the_parser()
    {
        var rng = new Random(42);
        var e = new Emulator(40, 12);
        for (var i = 0; i < 200; i++)
        {
            var data = new byte[256];
            rng.NextBytes(data);
            e.Feed(data);
            if (i % 40 == 0) e.Resize(rng.Next(1, 90), rng.Next(1, 40));
        }
        Assert.InRange(e.CursorX, 0, e.Cols - 1);
        Assert.InRange(e.CursorY, 0, e.Rows - 1);
    }
}

public class KeyEncoderTests
{
    private static string S(TerminalKey k, KeyMods m = KeyMods.None, Action<TerminalModes>? mode = null)
    {
        var modes = new TerminalModes();
        mode?.Invoke(modes);
        return Encoding.UTF8.GetString(KeyEncoder.Encode(k, m, modes)!);
    }

    [Fact]
    public void Arrows_follow_the_cursor_key_mode_and_carry_modifiers()
    {
        Assert.Equal("\e[A", S(TerminalKey.Up));
        Assert.Equal("\eOA", S(TerminalKey.Up, mode: m => m.ApplicationCursorKeys = true));
        Assert.Equal("\e[1;5C", S(TerminalKey.Right, KeyMods.Ctrl));
        Assert.Equal("\e[1;2D", S(TerminalKey.Left, KeyMods.Shift, m => m.ApplicationCursorKeys = true));   // modified keys use the CSI form either way
    }

    [Fact]
    public void Editing_and_function_keys_use_xterm_codes()
    {
        Assert.Equal("\e[3~", S(TerminalKey.Delete));
        Assert.Equal("\e[5;3~", S(TerminalKey.PageUp, KeyMods.Alt));
        Assert.Equal("\eOP", S(TerminalKey.F1));
        Assert.Equal("\e[15~", S(TerminalKey.F5));
        Assert.Equal("\e[24;6~", S(TerminalKey.F12, KeyMods.Ctrl | KeyMods.Shift));
        Assert.Equal("\x7f", S(TerminalKey.Backspace));
        Assert.Equal("\e[Z", S(TerminalKey.Tab, KeyMods.Shift));
        Assert.Equal("\r", S(TerminalKey.Enter));
    }

    [Fact]
    public void Typed_text_with_Ctrl_and_Alt()
    {
        Assert.Equal(new byte[] { 3 }, KeyEncoder.EncodeText("c", KeyMods.Ctrl));
        Assert.Equal(new byte[] { 0x1b, 3 }, KeyEncoder.EncodeText("C", KeyMods.Ctrl | KeyMods.Alt));
        Assert.Equal(new byte[] { 0 }, KeyEncoder.EncodeText(" ", KeyMods.Ctrl));
        Assert.Equal(new byte[] { 0x1b, (byte)'x' }, KeyEncoder.EncodeText("x", KeyMods.Alt));
        Assert.Equal(Encoding.UTF8.GetBytes("é"), KeyEncoder.EncodeText("é", KeyMods.None));
    }

    [Fact]
    public void Paste_is_bracketed_only_when_asked_and_cannot_end_the_bracket_early()
    {
        var modes = new TerminalModes();
        Assert.Equal("a\rb", Encoding.UTF8.GetString(KeyEncoder.EncodePaste("a\nb", modes)));
        modes.BracketedPaste = true;
        Assert.Equal("\e[200~x[201~y\e[201~", Encoding.UTF8.GetString(KeyEncoder.EncodePaste("x\e[201~y", modes)));   // an Escape inside is dropped
    }

    [Fact]
    public void Mouse_events_are_encoded_only_in_the_modes_that_want_them()
    {
        var modes = new TerminalModes { Mouse = MouseTracking.Click, MouseEncoding = MouseEncoding.Sgr };
        Assert.Equal("\e[<0;5;3M", Encoding.ASCII.GetString(KeyEncoder.EncodeMouse(KeyEncoder.MouseButton.Left, false, false, 4, 2, KeyMods.None, modes)!));
        Assert.Equal("\e[<0;5;3m", Encoding.ASCII.GetString(KeyEncoder.EncodeMouse(KeyEncoder.MouseButton.Left, true, false, 4, 2, KeyMods.None, modes)!));
        Assert.Null(KeyEncoder.EncodeMouse(KeyEncoder.MouseButton.Left, false, true, 4, 2, KeyMods.None, modes));   // motion needs drag or any
        Assert.Equal("\e[<64;1;1M", Encoding.ASCII.GetString(KeyEncoder.EncodeMouse(KeyEncoder.MouseButton.WheelUp, false, false, 0, 0, KeyMods.None, modes)!));
        modes.Mouse = MouseTracking.Off;
        Assert.Null(KeyEncoder.EncodeMouse(KeyEncoder.MouseButton.Left, false, false, 0, 0, KeyMods.None, modes));
        modes.Mouse = MouseTracking.Click; modes.MouseEncoding = MouseEncoding.X10;
        Assert.Equal(new byte[] { 0x1b, (byte)'[', (byte)'M', 32, 33, 33 }, KeyEncoder.EncodeMouse(KeyEncoder.MouseButton.Left, false, false, 0, 0, KeyMods.None, modes));
    }
}

public class SessionTests
{
    private static string? Launcher => Pty.FindLauncher();

    [Fact]
    public async Task A_program_runs_on_a_pty_its_output_reaches_the_screen_and_its_exit_status_is_reported()
    {
        Assert.NotNull(Launcher);   // build it: native/vterm/build.sh
        using var s = TerminalSession.Start(["/bin/sh", "-c", "printf 'hello\\r\\n'; exit 3"], 40, 5);
        Assert.Equal(3, await s.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(100);
        lock (s.Emulator.SyncRoot) Assert.Equal("hello", s.Emulator.GetLine(0).Text());
    }

    [Fact]
    public async Task Typed_input_reaches_the_program_the_terminal_is_a_real_tty_and_resizing_is_seen()
    {
        Assert.NotNull(Launcher);
        using var s = TerminalSession.Start(["/bin/sh", "-c", "tty >/dev/null && echo TTY; stty size; read line; echo got:$line; stty size"], 33, 7);
        await Task.Delay(500);
        s.Send("abc\r");
        await Task.Delay(300);
        lock (s.Emulator.SyncRoot)
        {
            var text = s.Emulator.ScreenText();
            Assert.Contains("TTY", text);
            Assert.Contains("7 33", text);
            Assert.Contains("got:abc", text);
        }
    }

    [Fact]
    public async Task The_program_can_query_the_terminal_and_gets_an_answer()
    {
        Assert.NotNull(Launcher);
        // asks for the cursor position and prints what came back
        using var s = TerminalSession.Start(["/bin/sh", "-c", "stty raw -echo; printf '\\033[6n'; dd bs=1 count=6 2>/dev/null | od -c | head -1; stty sane"], 40, 5);
        await s.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        lock (s.Emulator.SyncRoot)
        {
            var text = System.Text.RegularExpressions.Regex.Replace(s.Emulator.ScreenText(), @"\s+", "");
            Assert.Contains("[1;1R", text.Replace("033", "").Replace("\\", ""));   // od -c shows the reply as  033 [ 1 ; 1 R
        }
    }
}

public class IntegrationTests
{
    [Fact]
    public void Prompt_marks_stay_on_their_lines_and_carry_the_previous_commands_exit_code()
    {
        var e = new Emulator(30, 4);
        e.Feed("\e]133;A\a$ \e]133;B\als\r\nfile\r\n\e]133;D;0\a\e]133;A\a$ \e]133;B\afalse\r\n\e]133;D;1\a\e]133;A\a$ ");
        Assert.True(e.ShellIntegrationActive);
        Assert.Equal(LineMark.PromptStart, e.GetHistoryLine(0).Mark);
        Assert.Null(e.GetHistoryLine(0).ExitCode);
        Assert.Equal(0, e.GetHistoryLine(2).ExitCode);
        Assert.Equal(1, e.GetHistoryLine(3).ExitCode);

        e.Feed("\r\n\r\n\r\n\r\n\r\n");   // scroll the first prompt into history: its mark goes with it
        Assert.True(e.ScrollbackCount > 0);
        Assert.Equal(LineMark.PromptStart, e.GetHistoryLine(0).Mark);
        Assert.Equal(2, e.FindPrompt(3, backwards: true));
        Assert.Equal(3, e.FindPrompt(2, backwards: false));
        Assert.Null(e.FindPrompt(0, backwards: true));
    }

    [Fact]
    public void Profiles_are_detected_and_shells_start_with_integration_when_a_script_exists()
    {
        var env = new Dictionary<string, string> { ["SHELL"] = "/bin/zsh", ["PATH"] = "/x" };
        var found = new HashSet<string> { "/bin/zsh", "pwsh", "bash" };
        var profiles = ShellProfiles.Detect(k => env.GetValueOrDefault(k), n => found.Contains(n) ? n : null);
        Assert.Equal(new[] { "zsh", "PowerShell", "bash" }, profiles.Select(p => p.Name));

        var dir = Path.Combine(Path.GetTempPath(), "vde-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "vde-integration.ps1"), "");
            var (command, vars) = ShellProfiles.Launch(profiles.Single(p => p.Kind == ShellKind.PowerShell), dir);
            Assert.Equal("pwsh", command[0]);
            Assert.Contains("-NoExit", command);
            Assert.Equal("1", vars["VDE_TERMINAL_INTEGRATION"]);
            var (bash, bashVars) = ShellProfiles.Launch(profiles.Single(p => p.Kind == ShellKind.Bash), dir);   // no bash script in this folder
            Assert.Equal(new[] { "bash" }, bash);
            Assert.False(bashVars.ContainsKey("VDE_TERMINAL_INTEGRATION"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // Real PowerShell, when it is installed: the integration script makes it report prompts, results and folders.
    [Fact]
    public async Task PowerShell_with_the_integration_script_reports_prompts_exit_codes_and_the_folder()
    {
        var pwsh = ShellProfiles.FindOnPath("pwsh", Environment.GetEnvironmentVariable);
        var dir = ShellProfiles.FindIntegrationDir();
        Assert.NotNull(dir);
        if (pwsh is null) return;   // not installed here
        var (command, env) = ShellProfiles.Launch(new ShellProfile("PowerShell", [pwsh], ShellKind.PowerShell), dir);
        using var s = TerminalSession.Start(command, 100, 20, workingDirectory: "/tmp", environment: env);

        async Task Until(Func<bool> done, string what)
        {
            for (var i = 0; i < 300 && !done(); i++) await Task.Delay(100);
            lock (s.Emulator.SyncRoot) Assert.True(done(), what + "\n" + s.Emulator.ScreenText());
        }
        bool Marks(int n) { lock (s.Emulator.SyncRoot) return Enumerable.Range(0, s.Emulator.ScrollbackCount + s.Emulator.Rows).Count(i => s.Emulator.GetHistoryLine(i).Mark == LineMark.PromptStart) >= n; }

        await Until(() => Marks(1), "the first prompt");
        lock (s.Emulator.SyncRoot) Assert.Equal("/tmp", s.Emulator.WorkingDirectory);

        s.Send("Set-Location /usr\r");
        await Until(() => Marks(2), "the second prompt");
        lock (s.Emulator.SyncRoot) Assert.Equal("/usr", s.Emulator.WorkingDirectory);

        s.Send("Write-Error boom 2>$null; cmd-that-does-not-exist 2>$null\r");
        await Until(() => Marks(3), "the third prompt");
        s.Send("\r");   // an empty line is not a command
        await Until(() => Marks(4), "the fourth prompt");
        lock (s.Emulator.SyncRoot)
        {
            var codes = Enumerable.Range(0, s.Emulator.ScrollbackCount + s.Emulator.Rows).Select(i => s.Emulator.GetHistoryLine(i)).Where(l => l.Mark == LineMark.PromptStart).Select(l => l.ExitCode).ToList();
            Assert.Null(codes[0]);   // before any command
            Assert.Equal(0, codes[1]);
            Assert.True(codes[2] is > 0, "the failed command");   // pwsh reports its own status for a command it cannot find
            Assert.Null(codes[3]);   // the empty line: no result
        }
    }
}
