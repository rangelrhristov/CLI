# CLI

CLI is a small Windows desktop app for running multiple real Codex CLI sessions in one window.

It does not emulate a terminal. It launches actual Windows Terminal instances, embeds them into a native WinForms host, and gives you a simple control surface for switching, naming, and arranging them.

## Features

- Multiple real Codex CLI sessions in one app window
- Startup prompt for terminal count and working directory
- Click a pane to lock input to that Codex session
- Hidden input router for dictation tools such as Wispr
- Inline pane renaming by double-clicking a pane name
- Grid and horizontal layouts
- Quiet completion ping and flashing pane border
- Minimal black `FD CLI` control bar
- Generated app icon and ready-to-run Windows executable

## Run

```powershell
cd C:\IDE
.\Open-CLI.bat
```

`Open-CLI.bat` rebuilds `CLI.exe` from source and launches it.

You can also run the current built executable directly:

```powershell
.\CLI.exe
```

## Requirements

- Windows
- Windows Terminal
- Codex CLI available on PATH
- .NET Framework C# compiler, usually available at:

```text
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

## Build

```powershell
.\Build-NativeTerminalHost.ps1
```

The build outputs:

```text
CLI.exe
```

## Dictation Support

CLI keeps a tiny host-owned input field focused and forwards text into the selected terminal pane. This avoids dictation tools accidentally triggering Codex CLI paste-image or copy-response shortcuts.

Click a pane, then type or dictate normally. Input is routed into the selected Codex session.

For Wispr Flow on Windows, the default push-to-talk shortcut is `Ctrl+Win`, and Paste Last Transcript is `Shift+Alt+Z`. CLI keeps focus on its input sink while the app is active so those paste-based insertion paths land in the host first, then get forwarded to the selected Codex pane as plain text.

CLI also blocks Codex TUI shortcuts that conflict with dictation paste flows, including `Ctrl+V`, `Ctrl+C`, and `Ctrl+L`, while an embedded terminal is the active foreground target.

If Wispr falls back to copying the transcript to the clipboard, CLI watches for that text immediately after a Wispr-style shortcut and injects it into the selected pane once, instead of letting Codex interpret the paste shortcut as an image or copy command.

## Notes

Windows Terminal does not officially support being embedded as a child control. CLI uses Win32 window reparenting to keep real terminal windows inside one host window.

Completion detection is heuristic. CLI does not scrape the terminal screen; it watches activity and idle timing so it can ping without destabilizing the app.

## License

MIT
