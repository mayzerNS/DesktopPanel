# DesktopPanel

[Русский](README.ru.md)

Overlay panel for Windows 10/11. Opens on top of any window or full-screen game by a hotkey and closes without
switching away from it.

## Features

- **Desktop** — desktop shortcuts in the panel: groups (folders), custom order, names and images, search across the system
  (Windows Search index), Recycle Bin, undo (Ctrl+Z). Real desktop icons can be hidden.
- **Mini-browser** — WebView2 window on top of all windows. Modes: click-through, interaction, move.
- **Zapret** — download and control of [zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube):
  start/stop, Windows service, strategy test, lists, diagnostics. Off by default.
- **Telegram proxy** — local [tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy).
- **HDR** — on/off and SDR content brightness.
- **Task manager** — processes, CPU, memory, disk, network.
- **Corner mode** — the panel collapses to a small corner with buttons.
- **Hotkeys** — panel, actions (corner, settings, task manager, Recycle Bin, search, Zapret, Telegram proxy, HDR,
  icons) and mini-browser. Mouse buttons (middle, side, wheel) with Ctrl/Alt/Shift/Win. Unassigned by default
  except the panel hotkey.
- Interface languages: English, Russian.

## Requirements

- Windows 10 2004 (build 19041) or later, Windows 11; x64.
- Microsoft Edge WebView2 Runtime (preinstalled on Windows 11) — for the mini-browser.
- Administrator rights: required by Zapret (WinDivert driver, service), HDR and the hosts file.

## Installation

1. Download `DesktopPanel.exe` from [Releases](../../releases). Single file, .NET is not required.
2. Run it. The executable is not code-signed, so SmartScreen may show "Windows protected your PC":
   **More info → Run anyway**.
3. The first-run wizard sets language, Zapret, hotkey and autostart. It can be run again from Settings.

Default hotkey: **Win+C**. Windows reserves it; the program offers to take it over (option
"Take shortcuts over from Windows", low-level keyboard hook used only for the selected hotkeys).

## Data

| Path | Contents |
|---|---|
| `%APPDATA%\DesktopPanel\config.json` | settings |
| `%APPDATA%\DesktopPanel\logs\` | logs |
| `C:\ProgramData\DesktopPanel\zapret\` | Zapret (only if enabled) |

Autostart — Task Scheduler task. Uninstall — Settings → Uninstall → Uninstall the program, or "Installed apps" in Windows.

## Build

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet publish src\DesktopPanel\DesktopPanel.csproj -c Release -o dist
```

Output: `dist\DesktopPanel.exe` (self-contained single file). Or run `build.bat`.

Dependencies (NuGet): Microsoft.Web.WebView2, NAudio.Wasapi, System.Data.OleDb. Licenses: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

[GPL-3.0](LICENSE). Copyright © 2026 mayzerNS.

Copies and modified versions must be distributed under GPL-3.0 with source code and attribution.

Third-party components (.NET, NAudio, WebView2 SDK) — MIT / BSD-style, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
