# HDRSnap

A small homemade screenshot tool for Windows, built to replace ShareX on HDR displays — ShareX botches the tone mapping and blows out the highlights.

## What it does

- Global hotkey (`F11` by default) → captures the monitor under the cursor via Windows Graphics Capture.
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
2. Run it once to generate the default config at `%APPDATA%\HDRSnap\config.json`.
3. For autostart: drop a shortcut to the exe in `shell:startup`.

### Config (`%APPDATA%\HDRSnap\config.json`)

| Key | Default | Description |
|---|---|---|
| `Hotkey` | `F11` | Global hotkey (e.g. `Ctrl+Shift+F11`) |
| `OutputDir` | `Pictures\HDRSnap` | Output folder |
| `CaptureCursor` | `false` | Include the cursor in the capture |
| `PlaySound` | `true` | Confirmation sound |
| `SubfolderPerApp` | `true` | Subfolder named after the foreground process |
| `TonemapHeadroom` | `1.25` | Headroom above SDR white allotted to highlights (× SDR white, in nits) |

Log: `%APPDATA%\HDRSnap\hdrsnap.log`.

Command-line test: `HDRSnap.exe --shoot` (add `--forcetonemap` to force the D2D tone-mapping path even on a frame that stays within SDR range — useful for debugging).

## Technical notes / known gotchas

- The `DisplayConfig GET_SDR_WHITE_LEVEL` API fails (error 87) on some Windows 11 builds (observed on build 26200). Falls back to the registry: `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore\<id>\SDRWhiteLevel` (value in 1/1000 units, e.g. 6000 = 480 nits), then to 200 nits by default if everything else fails.
- A misdetected SDR white level (silently falling back to 200 nits instead of the monitor's real value) was the main cause of blown-out highlights in an earlier version — the API → registry → default chain, plus computing MaxCLL at the 99.99th percentile (instead of the absolute max, which is sensitive to a single stray pixel), fixes that.
