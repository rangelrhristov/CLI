# CLI

This is the literal host attempt: one native Windows program that launches **actual Windows Terminal windows** running the real interactive `codex` CLI, then reparents those windows into panels inside the host.

It does not recreate a terminal in React, xterm.js, or Electron.

## Run The One-Program Host

```powershell
cd C:\IDE
.\Open-CLI.bat
```

or:

```powershell
npm start
```

The popup asks how many terminals you want, lets you name each one, and opens one host window. Each panel attempts to contain a real Windows Terminal window running `codex`.

The host now keeps the experience mouse-first:

- click a pane to lock typing to that terminal;
- use the tiny `FD CLI` top bar buttons to add, rename, restart, stop, or cycle layouts;
- each pane has a small host-drawn name/status strip while the terminal remains real Windows Terminal;
- the strip changes to `YOU typing` while your input is being sent and `CODEX working` after Enter;
- after you press Enter in a locked pane, the host watches for sustained process idle and then plays a quiet ping while flashing only that pane's border.

## Important

Windows Terminal does not officially support embedding as a control. This host uses Win32 `SetParent` window reparenting. If Windows Terminal refuses to embed on this machine, that is an OS/app limitation rather than xterm styling.

Completion pings are intentionally heuristic. The host does not read the terminal screen because that previously caused freezes; it watches process activity instead.

## Codex Message Noise

`AGENTS.md` in this folder tells Codex sessions launched from `C:\IDE` to keep output compact and clearly mark Codex final responses. This reduces clutter inside the small terminal panes without changing Codex features.

Codex's internal TUI message styling is not currently configurable from this host. The host can change the terminal/container behavior, but per-message colors inside Codex would require Codex itself to expose a theme option or be modified.

## Fallback: One Actual Windows Terminal Window

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\CodexTerminalRoom.ps1 -Instances 4 -Names Frontend,Backend,Tests,Review -Workdir C:\IDE
```

That fallback opens actual Windows Terminal directly with Codex panes. It is not inside a custom host.

Preview the fallback command without opening terminals:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\CodexTerminalRoom.ps1 -Instances 4 -Names Frontend,Backend,Tests,Review -Workdir C:\IDE -PrintCommand
```

## Notes

- Requires Windows Terminal (`wt.exe`) and Codex CLI on PATH.
- The terminal UI is Windows Terminal itself, so it keeps the real look, keyboard behavior, scrollback, copy/paste, shell integration, and Codex interaction.
- The older Electron prototype files are not the recommended path for the literal terminal requirement.
