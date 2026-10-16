# vterminal

VDE's terminal: `vterminal` (src/VTerminal), built on `Vde.Terminal` (the emulator, no UI) and `TerminalView`
(Vde.Shell.Controls). It takes its colours from the shell theme and its font and behaviour from
`HKCU\Software\Vde\Terminal` (Settings → Terminal).

    vterminal [-p PROFILE] [--working-directory DIR] [--title TEXT] [--hold] [-e PROGRAM [ARGS...]]

## What it does

- **Tabs**: `Ctrl+Shift+T` new, `Ctrl+Shift+W` close, `Ctrl+Tab` / `Ctrl+Shift+Tab` switch, `Ctrl+Alt+1..9` go to,
  `Ctrl+Shift+N` new window in the same folder, `F11` full screen. The ▾ button lists the shell profiles.
  A new tab opens in the folder the current one is in (the shell reports it, see below).
- **Emulation**: xterm-compatible (`TERM=xterm-256color`, `COLORTERM=truecolor`): 256 and true colour, the
  alternate screen, scroll regions, DEC line drawing, wide and combining characters, bracketed paste, mouse
  reporting (X10, SGR, urxvt), focus events, window titles, hyperlinks (OSC 8: `Ctrl+click` opens them).
- **Keys**: `Ctrl+Shift+C` / `Ctrl+Shift+V` copy and paste (plain `Ctrl+C` stays the program's), `Shift+Insert`,
  middle click pastes, `Shift+PageUp/PageDown` and the wheel scroll the history, double and triple click select a
  word or a line, `Ctrl+Shift+A` selects all, `Ctrl+Shift++/-` change the font size.
- **Starting programs**: the program runs on a pseudo-terminal through `vterm-launch` (native/vterm), a 60-line
  C helper, because a .NET process cannot fork. Build it with `native/vterm/build.sh`; the installer puts it in
  `/usr/lib/vde`.

## PowerShell and bash integration

`vterminal` lists the profiles it finds: your login shell, `pwsh` ("PowerShell"), bash, zsh, fish. For PowerShell
and bash it loads a small script (`data/terminal/vde-integration.ps1` / `.bash`, installed in
`/usr/share/vde/terminal`) after your own profile, and sets `VDE_TERMINAL_INTEGRATION=1`. The script keeps your
prompt and adds invisible escape sequences:

| Sequence | Meaning |
| --- | --- |
| `OSC 133;A` / `133;B` | the prompt starts / ends and your input begins |
| `OSC 133;D;n` | the command before this prompt ended with status n |
| `OSC 7` | the current folder |

With these vterminal can jump between prompts (`Ctrl+Shift+Up` / `Down`), mark the prompts of failed commands with
a red bar, and start new tabs and windows in the folder you are in. PowerShell reports each command's result from
`$?` and `$LASTEXITCODE`; an empty line reports none. To use it in a shell vterminal did not start, put
`. /usr/share/vde/terminal/vde-integration.ps1` in your `$PROFILE` (it does nothing unless
`VDE_TERMINAL_INTEGRATION=1`).

More profiles: `HKCU\Software\Vde\Terminal` has `DefaultProfile`; a profile's `Command` can be given with the
`-e` option for one-off use.

## Not built yet

Split panes, search in the scrollback, a settings page for colour schemes, reflowing wrapped lines on resize,
sixel/graphics, OSC 52 clipboard access, and IME composition.
