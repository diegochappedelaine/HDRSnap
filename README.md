# HDRSnap

A small screenshot tool for Windows HDR displays. ShareX botches the tone mapping on HDR and blows out the highlights; HDRSnap tone-maps the capture properly and saves an SDR PNG that looks like what was on screen.

## Features

- `F11` captures the monitor under the cursor, `Ctrl+F11` only the foreground window.
- HDR frames are tone-mapped on the GPU with the Direct2D `HDR Tone Map` effect (the one Photos and Snipping Tool use), targeting the monitor's real SDR white level. A CPU fallback (extended Reinhard) takes over if the GPU path fails.
- Saves an SDR PNG per capture, in one subfolder per app (`Pictures\HDRSnap\<ProcessName>\yyyy-MM-dd HH-mm-ss.png`), and copies it to the clipboard.
- The hotkey still works in games that block regular hotkeys.
- Lives in the system tray. Config changes apply on save, no restart.

## Install

1. Download `HDRSnap.exe` from the [releases](https://github.com/diegochappedelaine/HDRSnap/releases), or [build it](#build). It is portable: no installer, no .NET runtime needed.
2. Run it. It appears in the tray.
3. To start it with Windows, put a shortcut to the exe in `shell:startup`.

## Configuration

`%APPDATA%\HDRSnap\config.json` is created on first run. Open it from the tray menu (*Edit configuration…*); changes apply as soon as the file is saved.

| Key | Default | Description |
|---|---|---|
| `Hotkey` | `F11` | Screen capture hotkey (e.g. `Ctrl+Shift+F11`) |
| `WindowHotkey` | `Ctrl+F11` | Foreground window capture hotkey (empty string disables it) |
| `OutputDir` | `Pictures\HDRSnap` | Output folder (also settable from the tray: *Choose output folder…*) |
| `CaptureCursor` | `false` | Include the cursor in the capture |
| `PlaySound` | `true` | Play a confirmation sound |
| `CopyToClipboard` | `true` | Copy each capture to the clipboard (PNG + bitmap); not with `--shoot` |
| `SubfolderPerApp` | `true` | Save into a subfolder named after the foreground process |
| `TonemapHeadroom` | `1.25` | Headroom above SDR white for highlights (× SDR white) |

The log is at `%APPDATA%\HDRSnap\hdrsnap.log`.

## Limitations

- **True exclusive fullscreen can't be captured.** Windows Graphics Capture returns the desktop behind the game instead. Use borderless windowed, or make sure *Disable fullscreen optimizations* is not ticked in the game exe's properties. HDRSnap logs a `WARNING` when a capture looks like this.
- **Games running as administrator** don't see HDRSnap's hotkey unless HDRSnap runs as administrator too.

## Build

Requires Windows and the .NET 9 SDK.

```powershell
dotnet build -c Release
dotnet test tests/HDRSnap.Tests -c Release
```

Portable single-file exe:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish
```

Capture from the command line, for testing:

- `HDRSnap.exe --shoot` captures once and exits.
- `--window` captures the foreground window instead (at launch, that is usually the console).
- `--forcetonemap` forces the Direct2D tone-mapping path even on a frame within SDR range.

The app icon is generated: `powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 HDRSnap.ico`.

## Technical notes

[docs/technical-notes.md](docs/technical-notes.md) covers the Windows pitfalls hit along the way: why `RegisterHotKey` silently fails in some games, the struct padding bug that broke SDR white detection, and why an app icon has three separate paths.
