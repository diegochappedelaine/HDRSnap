# HDRSnap — notes for agents

Windows-only tray screenshot tool that tone-maps HDR captures to SDR PNGs.
One app project (`Program.cs`) plus a test project. Read `README.md` first:
its "Technical notes / known gotchas" section records hard-won platform facts
(hotkey hook vs RegisterHotKey, SDR white level detection, fullscreen
optimizations). Do not undo any of them.

## Build / test (Windows + .NET 9 SDK only)

- Build: `dotnet build HDRSnap.csproj -c Release`
- Tests: `dotnet test tests/HDRSnap.Tests -c Release` (always pass the path; no .sln)
- Portable exe: see README "Build"
- Manual smoke test: `bin\Release\net9.0-windows10.0.22621.0\HDRSnap.exe --shoot`
  then read the last line of `%APPDATA%\HDRSnap\hdrsnap.log`. `Program.Log`
  prefixes every line with `yyyy-MM-dd HH:mm:ss `, so success looks like
  `2026-09-19 21:36:02 OK <path>`; on failure the line reads `ERR <exception>`
  and the process exits with code 1.
- CI: `.github/workflows/ci.yml` runs build + tests on `windows-latest`.

## Layout

- `Program.cs` — everything, in sections: `Config` → `Native` (P/Invoke) →
  COM interop interfaces → `Capturer` (WGC capture, luminance stats, D2D and
  CPU tone mapping, PNG save) → `TrayApp` (tray icon, low-level keyboard hook)
  → `Program` (log, `Main`, `--shoot` / `--forcetonemap` flags).
- `tests/HDRSnap.Tests/` — xUnit. Pure pixel functions are `internal` and
  exposed via `InternalsVisibleTo`. Tests build synthetic scRGB FP16 (`Half`)
  buffers; see `PixelMathTests.cs` for the helpers.
- `plans/` — implementation plans written by an advisor session; `plans/README.md`
  is the index. Not shipped.

## Conventions

- Identifiers, UI strings and log messages in English; code comments may stay French.
- Package versions: wildcard major (`3.*`).
- Commit messages in French, `Zone : description` (see `git log`).
- Pixel math: scRGB linear, `1.0 = 80 nits`, 4 channels per pixel, row pitch
  in bytes. Any change to `LuminanceStats`, `ToneMapScRgbToBgra`, `ReadHdrRow`,
  `EncodeRow`, `SrgbByte` or `CopyBgra` needs a test in `PixelMathTests.cs`.
- Errors that the user needs to see go to `Program.Log` **and** a tray balloon.
- Nothing in this repo runs on macOS/Linux; do not "fix" the TFM.
