# Technical notes

Windows pitfalls hit while building HDRSnap, and how the code deals with them.

## SDR white level detection

The tone mapper needs the monitor's SDR white level. HDRSnap reads it in this order:

1. The `DisplayConfig` API (`GET_SDR_WHITE_LEVEL`).
2. The registry: `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore\<id>\SDRWhiteLevel`, in 1/1000 units (`6000` = 480 nits). It takes the max across all monitors and logs a warning when they differ.
3. 200 nits.

A wrong value was the main cause of blown-out highlights in early versions: the API call failed, the code silently fell back to 200 nits, and the image was tone-mapped against the wrong white.

The API call failed with error 87 because of a bug in HDRSnap's P/Invoke, not in Windows. `DISPLAYCONFIG_PATH_TARGET_INFO.refreshRate` was declared as a 64-bit integer, which padded the struct to 56 bytes instead of 48. `QueryDisplayConfig` results were read at the wrong stride, so the adapter and target ids passed to the API were garbage. `tests/HDRSnap.Tests/InteropLayoutTests.cs` now pins the struct sizes.

MaxCLL is measured at the 99.99th percentile rather than the absolute max, so a single stray pixel can't skew the tone mapping.

## Hotkeys: why not `RegisterHotKey`

Some games register for raw keyboard input with `RIDEV_NOHOTKEYS` (seen in Pragmata). Windows then stops delivering application hotkeys while the game has focus. The registration stays alive (another process registering the same key still gets error 1409), but `WM_HOTKEY` is never posted. The hotkey does nothing and nothing is logged.

A low-level keyboard hook (`WH_KEYBOARD_LL`) runs before that stage and is unaffected, so HDRSnap uses one.

To diagnose this kind of problem: poll `GetAsyncKeyState` from a background process. If the key shows up while the game is in the foreground but the log stays empty, this is it.

A low-level hook is still blind to a game running as administrator: UIPI blocks input from a higher-integrity foreground window. HDRSnap has to run elevated too in that case.

## Exclusive fullscreen

Windows Graphics Capture only works in games thanks to fullscreen optimizations, which turn legacy exclusive fullscreen into a DWM-composed flip. When they are disabled for an exe (the `DISABLEDXMAXIMIZEDWINDOWEDMODE` compat flag under `AppCompatFlags\Layers`), the game gets true exclusive fullscreen and the capture returns the desktop behind it, clipped exactly at SDR white.

HDRSnap recognises that shape: a game in the foreground, and both the 99.99th percentile and the max pinned to SDR white. It then logs a `WARNING` line in `hdrsnap.log`, naming the compat flag when it is set. The check only logs, because it has false positives on dark scenes.

Capturing true exclusive fullscreen would require hooking `IDXGISwapChain::Present` the way OBS game capture does. That is out of scope.

## Window capture

Window capture goes through `IGraphicsCaptureItemInterop.CreateForWindow`. It has the same exclusive fullscreen limitation as monitor capture. The "looks like the desktop" check is not applied to window captures, since it assumes a full-monitor frame.

## App icons: three separate paths

A Windows app's icon comes from three places, and fixing one does nothing for the others:

- **The exe resource** (`ApplicationIcon`), used by Explorer and the taskbar.
- **The tray icon** (`NotifyIcon.Icon`).
- **The notification.** Windows 11 shows a legacy balloon tip as a toast, and the toast ignores the tray icon: it looks the app up by `AppUserModelID`. Without one it shows the generic application icon. `AppIdentity.Ensure()` sets the ID with `SetCurrentProcessExplicitAppUserModelID` and writes a Start Menu shortcut with the same ID; the toast shows that shortcut's icon.

Balloons use `ToolTipIcon.None`: `ToolTipIcon.Info` makes the shell draw its own blue "i" instead of the app icon.

## Tray icon size and DPI

The tray icon is loaded at `SystemInformation.SmallIconSize`, which follows display scaling: 16 px at 100 %, 20 at 125 %, 24 at 150 %, 28 at 175 %, 32 at 200 %. If the `.ico` lacks the exact size, Windows stretches the nearest one and the icon looks blurry rather than failing. `tools/make-icon.ps1` therefore emits 16/20/24/28/32/40/48/64/256.

`AppIcon()` logs the size it asked for and the size it got, which is the quickest way to spot this again.
