# HDRSnap

A small homemade screenshot tool for Windows, built to replace ShareX on HDR displays — ShareX botches the tone mapping and blows out the highlights.

## What it does

- Global hotkey (`F11` by default) → captures the monitor under the cursor via Windows Graphics Capture. The hotkey goes through a low-level keyboard hook (`WH_KEYBOARD_LL`) rather than `RegisterHotKey`, for the reason in the gotchas below. A second hotkey (`WindowHotkey`, `Ctrl+F11` by default) captures only the foreground window.
- On an HDR display: captures in FP16 scRGB, tone-maps on the GPU via the Direct2D `HDR Tone Map` effect (the same one Photos / Windows Snipping Tool use). The target is computed from the MaxCLL measured on the frame (99.99th percentile) and the monitor's actual SDR white level, with a CPU fallback (extended Reinhard) if the GPU path fails.
- Outputs SDR PNGs, one subfolder per foreground app (`Pictures\HDRSnap\<ProcessName>\yyyy-MM-dd HH-mm-ss.png`).
- Runs from the system tray, single instance (mutex).

## Build

Requires the .NET 9 SDK (Windows).

```powershell
dotnet build -c Release
```

For a self-contained portable executable (single .exe, no dependency on an installed runtime):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish
```

## Installation

1. Copy `publish\HDRSnap.exe` anywhere you like (no installer needed).
2. Run it once to generate the default config at `%APPDATA%\HDRSnap\config.json` (also reachable from the tray menu: *Modifier la configuration…*). Edits are applied on save, no restart needed.
3. For autostart: drop a shortcut to the exe in `shell:startup`.

### Config (`%APPDATA%\HDRSnap\config.json`)

| Key | Default | Description |
|---|---|---|
| `Hotkey` | `F11` | Global hotkey (e.g. `Ctrl+Shift+F11`) |
| `WindowHotkey` | `Ctrl+F11` | Hotkey to capture only the foreground window (empty string disables) |
| `OutputDir` | `Pictures\HDRSnap` | Output folder (can also be changed from the tray menu: *Choisir le dossier de sortie…*) |
| `CaptureCursor` | `false` | Include the cursor in the capture |
| `PlaySound` | `true` | Confirmation sound |
| `CopyToClipboard` | `true` | Also copy the capture to the clipboard (PNG + bitmap); tray mode only |
| `SubfolderPerApp` | `true` | Subfolder named after the foreground process |
| `TonemapHeadroom` | `1.25` | Headroom above SDR white allotted to highlights (× SDR white, in nits) |

Log: `%APPDATA%\HDRSnap\hdrsnap.log`.

Command-line test: `HDRSnap.exe --shoot` (add `--forcetonemap` to force the D2D tone-mapping path even on a frame that stays within SDR range — useful for debugging, or `--window` to capture the foreground window at launch time, which is usually the console).

## Technical notes / known gotchas

- Earlier versions saw `DisplayConfig GET_SDR_WHITE_LEVEL` fail with error 87 and blamed the Windows build. The actual cause was in HDRSnap's own P/Invoke: `DISPLAYCONFIG_PATH_TARGET_INFO.refreshRate` was declared as a 64-bit integer, which padded the struct to 56 bytes instead of the native 48, so `QueryDisplayConfig` results were read at the wrong stride and the target adapter/id passed to the API were garbage. The struct sizes are now pinned by `tests/HDRSnap.Tests/InteropLayoutTests.cs`. The registry fallback (`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore\<id>\SDRWhiteLevel`, in 1/1000 units, e.g. 6000 = 480 nits) is kept, then 200 nits if everything fails. Note the registry fallback picks the max across all monitors and logs a warning when they differ.
- A misdetected SDR white level (silently falling back to 200 nits instead of the monitor's real value) was the main cause of blown-out highlights in an earlier version — the API → registry → default chain, plus computing MaxCLL at the 99.99th percentile (instead of the absolute max, which is sensitive to a single stray pixel), fixes that.
- `RegisterHotKey` is unusable for a capture tool. A game that registers for raw keyboard input with `RIDEV_NOHOTKEYS` (observed on Pragmata) asks Windows to stop processing application-defined hotkeys while it has focus. The registration stays alive — another process calling `RegisterHotKey` on the same key still gets error 1409 — but `WM_HOTKEY` is never posted, so the hotkey silently does nothing in-game and logs nothing at all. A `WH_KEYBOARD_LL` hook runs before that stage and is unaffected. To diagnose the same class of problem: poll `GetAsyncKeyState` from a background process; if the key is seen while the game is in the foreground but the log stays empty, this is it.
- A low-level hook is still blind to a game running elevated (UIPI blocks input from a higher-integrity foreground window), so HDRSnap has to run elevated too in that case.
- Window capture goes through `IGraphicsCaptureItemInterop.CreateForWindow`. It has the same limitation as monitor capture on true exclusive fullscreen (see the fullscreen-optimizations bullet below), and the "looks like the desktop" warning is deliberately not applied to window captures, since that heuristic assumes a full-monitor frame.
- Fullscreen optimizations convert legacy exclusive fullscreen into a DWM-composed flip, which is what makes Windows Graphics Capture work in games at all. When they are turned off for an exe (the `DISABLEDXMAXIMIZEDWINDOWEDMODE` compat flag under `AppCompatFlags\Layers`), the game gets true exclusive fullscreen and the capture returns the desktop behind it, clipped exactly at SDR white. HDRSnap recognises that shape — a game in the foreground and both the 99.99th percentile and the max pinned to SDR white — and warns instead of silently saving a desktop screenshot, pointing at the compat flag when it is set. Capturing true exclusive fullscreen would require hooking `IDXGISwapChain::Present` the way OBS game capture does, which is out of scope here.
