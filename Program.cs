using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace HDRSnap;

// ---------------------------------------------------------------------------
// Config (%APPDATA%\HDRSnap\config.json)
// ---------------------------------------------------------------------------
public class Config
{
    public string Hotkey { get; set; } = "F11";
    /// Hotkey to capture only the foreground window ("" = disabled).
    public string WindowHotkey { get; set; } = "Ctrl+F11";
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HDRSnap");
    public bool CaptureCursor { get; set; } = false;
    public bool PlaySound { get; set; } = true;
    /// Also copies the capture to the clipboard (PNG + bitmap). Tray mode only.
    public bool CopyToClipboard { get; set; } = true;
    public bool SubfolderPerApp { get; set; } = true;
    /// Headroom above SDR white given to highlights in the PNG
    /// (tone mapping target = TonemapHeadroom x SDR white, in nits).
    public float TonemapHeadroom { get; set; } = 1.25f;

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HDRSnap");
    public static string FilePath => Path.Combine(Dir, "config.json");

    /// Warning from the last Load (null if none). Logged by Program.Main:
    /// Config does not log by itself so that it stays testable.
    public static string LoadWarning { get; private set; }

    public static Config Load() => Load(FilePath);

    public static Config Load(string path)
    {
        LoadWarning = null;
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(path));
                if (loaded != null) { loaded.LoadedFromFile = true; return loaded.Sanitized(); }
                LoadWarning = $"empty config ({path}) -> defaults, file kept";
            }
            catch (Exception ex)
            {
                LoadWarning = $"unreadable config ({path} : {ex.Message}) -> defaults, file kept";
            }
            return new Config();   // NEVER overwrite the user's file
        }

        var cfg = new Config();
        if (!cfg.Save(path))
            LoadWarning = $"default config not written ({path} : {LastSaveError})";
        cfg.LoadedFromFile = true;   // we wrote this file ourselves: nothing to preserve
        return cfg;
    }

    /// Why the last write failed (null if the last one succeeded).
    public static string LastSaveError { get; private set; }

    /// True only if this instance came from a file that was really read back.
    /// Field (not property): System.Text.Json only serialises public properties.
    internal bool LoadedFromFile;

    public bool Save() => Save(FilePath);

    /// Writes the whole config (indented). false if the write fails; the caller logs.
    /// Atomic write (temp file then replace): a power cut or a full disk cannot
    /// leave a truncated config.json behind.
    public bool Save(string path)
    {
        LastSaveError = null;
        try
        {
            // GetDirectoryName returns "" for a bare name and null for a root: in both
            // cases there is no directory to create, and CreateDirectory would throw.
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Load deliberately keeps a file it could not read; overwriting it here would
            // silently lose the settings the user had put in it. Back it up first.
            if (!LoadedFromFile && File.Exists(path))
                File.Copy(path, path + ".bak", true);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        }
        catch (Exception ex) { LastSaveError = ex.Message; return false; }
    }

    /// Copies every value across (hot reload: TrayApp keeps the same instance).
    public void CopyFrom(Config o)
    {
        Hotkey = o.Hotkey; WindowHotkey = o.WindowHotkey; OutputDir = o.OutputDir;
        CaptureCursor = o.CaptureCursor; PlaySound = o.PlaySound; CopyToClipboard = o.CopyToClipboard;
        SubfolderPerApp = o.SubfolderPerApp; TonemapHeadroom = o.TonemapHeadroom;
    }

    /// Null/empty values (e.g. "Hotkey": null in the JSON) -> defaults.
    Config Sanitized()
    {
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "F11";
        if (string.IsNullOrWhiteSpace(OutputDir)) OutputDir = new Config().OutputDir;
        WindowHotkey ??= "";   // null = disabled, not a crash
        return this;
    }

    /// "Ctrl+Shift+F11" -> (mods, vk). Modifier bits: Alt=1, Ctrl=2, Shift=4, Win=8
    /// (same values as RegisterHotKey's MOD_*, compared by TrayApp.FindHotkey).
    /// false + message if there is no key, several keys, or an unknown key name
    /// (names = System.Windows.Forms.Keys members, case-insensitive).
    public bool TryParseHotkey(out uint mods, out uint vk, out string error)
        => TryParseHotkey(Hotkey, out mods, out vk, out error);

    public static bool TryParseHotkey(string text, out uint mods, out uint vk, out string error)
    {
        mods = 0; vk = 0; error = null;
        Keys? key = null;
        foreach (var raw in (text ?? "").Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default:
                    // Enum.TryParse also accepts the numeric form: "1" would give
                    // Keys.LButton (vk 1), a hotkey that parses but that the keyboard
                    // hook will never see. Require a member name ("D1", not "1").
                    if (char.IsDigit(part[0]) || part[0] == '-' || part[0] == '+')
                    { error = $"numeric key \"{part}\" (use the Keys member name, e.g. D1)"; return false; }
                    if (!Enum.TryParse<Keys>(part, true, out var k) || !Enum.IsDefined(typeof(Keys), k))
                    { error = $"unknown key \"{part}\""; return false; }
                    // A virtual-key code fits in 1..254; beyond that (Keys.Modifiers,
                    // 0xFFFF0000) no keystroke can ever match.
                    if ((uint)k == 0 || (uint)k > 0xFE)
                    { error = $"key \"{part}\" outside the virtual-key range (1-254)"; return false; }
                    if (key != null)
                    { error = $"several keys ({key} and {k})"; return false; }
                    key = k;
                    break;
            }
        }
        if (key == null) { error = "no key (modifiers only?)"; return false; }
        vk = (uint)key.Value;
        return true;
    }
}

// ---------------------------------------------------------------------------
// Interop
// ---------------------------------------------------------------------------
internal static class Native
{
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr hProc, uint flags, System.Text.StringBuilder buf, ref uint size);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX mi);
    [DllImport("d3d11.dll")] public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    // --- DisplayConfig (monitor SDR white level) ---
    [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr currentTopologyId);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL info);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);

    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public uint refreshRateNumerator, refreshRateDenominator; // DISPLAYCONFIG_RATIONAL: 2 x UINT32, 4-aligned (not ulong: that would force 8 bytes of padding)
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO { public uint infoType, id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SDR_WHITE_LEVEL { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint SDRWhiteLevel; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    /// Monitor SDR white level in nits (the Windows "SDR content brightness" slider).
    /// The DisplayConfig API kept returning ERROR_INVALID_PARAMETER as long as
    /// DISPLAYCONFIG_PATH_TARGET_INFO was 56 bytes instead of 48 (refreshRate declared
    /// as ulong): the fault was here, not in the Windows build. Sizes are pinned by
    /// InteropLayoutTests. Fallbacks kept: registry (MonitorDataStore), then 200 nits.
    public static (float nits, string source) GetSdrWhite(IntPtr hMonitor)
    {
        float? v = SdrWhiteFromDisplayConfig(hMonitor);
        if (v.HasValue) return (v.Value, "API");
        v = SdrWhiteFromRegistry();
        if (v.HasValue) return (v.Value, "registry");
        return (200f, "default");
    }

    static float? SdrWhiteFromRegistry()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore");
            if (root == null) return null;
            float best = 0f, first = 0f;
            bool ambiguous = false;
            foreach (var name in root.GetSubKeyNames())
                using (var k = root.OpenSubKey(name))
                    if (k?.GetValue("SDRWhiteLevel") is int lvl && lvl >= 1000 && lvl <= 10000)
                    {
                        float nits = lvl / 1000f * 80f;
                        if (first == 0f) first = nits; else if (nits != first) ambiguous = true;
                        best = Math.Max(best, nits);
                    }
            if (ambiguous)
                Program.Log($"SDR white (registry): several monitors with different values, max kept ({best:0} nits)");
            return best > 0f ? best : null;
        }
        catch { return null; }
    }

    static float? SdrWhiteFromDisplayConfig(IntPtr hMonitor)
    {
        try
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoW(hMonitor, ref mi)) return null;

            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint nPaths, out uint nModes) != 0) return null;
            var paths = new DISPLAYCONFIG_PATH_INFO[nPaths];
            var modes = new DISPLAYCONFIG_MODE_INFO[nModes];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref nPaths, paths, ref nModes, modes, IntPtr.Zero) != 0) return null;

            for (int i = 0; i < nPaths; i++)
            {
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = 1, // GET_SOURCE_NAME
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref src) != 0 || src.viewGdiDeviceName != mi.szDevice) continue;

                var sdr = new DISPLAYCONFIG_SDR_WHITE_LEVEL
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = 11, // GET_SDR_WHITE_LEVEL
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                        adapterId = paths[i].targetInfo.adapterId,
                        id = paths[i].targetInfo.id,
                    }
                };
                int rc = DisplayConfigGetDeviceInfo(ref sdr);
                if (rc != 0)
                {
                    Program.Log($"SDR white: GET_SDR_WHITE_LEVEL failed (error {rc}) for {mi.szDevice} -> registry fallback");
                    continue;
                }
                // Same guard as the registry fallback: an out-of-range value (0 on an
                // inactive cloned target) would give a null white and NaN pixels downstream.
                if (sdr.SDRWhiteLevel < 1000 || sdr.SDRWhiteLevel > 10000)
                {
                    Program.Log($"SDR white: API value out of range ({sdr.SDRWhiteLevel}) for {mi.szDevice}, target ignored");
                    continue;
                }
                return sdr.SDRWhiteLevel / 1000f * 80f;
            }
        }
        catch { }
        return null;
    }
}

[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow(IntPtr window, ref Guid iid);
    IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
}

[ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface(ref Guid iid);
}

// ---------------------------------------------------------------------------
// Capture
// ---------------------------------------------------------------------------
public enum CaptureMode { Monitor, Window }

internal static class Capturer
{
    static ID3D11Device _device;
    static ID3D11DeviceContext _context;
    static IDirect3DDevice _winrtDevice;
    static ID2D1Factory1 _d2dFactory;
    static ID2D1Device _d2dDevice;
    static ID2D1DeviceContext _d2d;

    /// Debug (--forcetonemap): go through the D2D tone mapper even if the frame stays in SDR range.
    public static bool ForceToneMap;

    static void EnsureDevice()
    {
        if (_device != null) return;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            null, out _device).CheckError();
        _context = _device.ImmediateContext;
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr pInsp));
        _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pInsp);
        Marshal.Release(pInsp);
    }

    static void EnsureD2D()
    {
        if (_d2d != null) return;
        _d2dFactory ??= D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2d = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
    }

    static void ResetD2D()
    {
        try { _d2d?.Dispose(); } catch { }
        try { _d2dDevice?.Dispose(); } catch { }
        _d2d = null; _d2dDevice = null;
    }

    static void ResetDevice()
    {
        ResetD2D();
        try { _winrtDevice?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _d2d = null; _d2dDevice = null; _winrtDevice = null; _context = null; _device = null;
    }

    /// hwnd: window to capture in Window mode; IntPtr.Zero = the foreground one.
    public static string CaptureToFile(Config cfg, CaptureMode mode = CaptureMode.Monitor, IntPtr hwnd = default)
    {
        try { return CaptureToFileCore(cfg, mode, hwnd); }
        catch (Exception ex) when (IsDeviceError(ex))
        {
            // device lost (TDR / driver update): reset and second attempt
            Program.Log($"capture failed ({ex.GetType().Name}: {ex.Message}) -> device reset, second attempt");
            ResetDevice();
            return CaptureToFileCore(cfg, mode, hwnd);
        }
        catch (TimeoutException) when (mode == CaptureMode.Monitor)
        {
            // After a TDR, WGC often stops delivering frames without raising a COM error.
            // The device is held in a static: without a reset it would stay dead for EVERY
            // later capture. Throw it away, but do not restart the capture (no second 3 s
            // wait): the next keystroke will start again on a fresh device.
            // Screen only: a window that cannot be captured (WDA_EXCLUDEFROMCAPTURE, an
            // elevated window) never delivers a frame without the device being at fault,
            // and destroying the shared device would penalise every later capture.
            Program.Log("capture failed (timeout) -> device reset, no second attempt");
            ResetDevice();
            throw;
        }
    }

    /// Only D3D/DXGI/WinRT errors justify resetting the device and retrying.
    /// Frame timeout, disk I/O, invalid path: fail straight away.
    internal static bool IsDeviceError(Exception ex) =>
        ex is SharpGen.Runtime.SharpGenException || ex is COMException;

    static string CaptureToFileCore(Config cfg, CaptureMode mode, IntPtr hwnd = default)
    {
        var (fgName, fgExe) = ForegroundApp();
        string appName = cfg.SubfolderPerApp ? fgName : null;

        IntPtr hwndTarget = IntPtr.Zero, hmon;
        if (mode == CaptureMode.Window)
        {
            // hwnd supplied by the caller (tray menu), otherwise the foreground window (hotkey).
            hwndTarget = hwnd != IntPtr.Zero ? hwnd : Native.GetForegroundWindow();
            if (hwndTarget == IntPtr.Zero) throw new InvalidOperationException("No foreground window.");
            hmon = Native.MonitorFromWindow(hwndTarget, 2 /* MONITOR_DEFAULTTONEAREST */);
        }
        else
        {
            Native.GetCursorPos(out var pt);
            hmon = Native.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        }

        bool hdr = IsMonitorHdr(hmon);
        var (sdrNits, sdrSrc) = hdr ? Native.GetSdrWhite(hmon) : (200f, "n/a");
        string what = mode == CaptureMode.Window ? "window capture" : "screen capture";
        Program.Log(hdr ? $"HDR {what} (SDR white = {sdrNits:0} nits, source: {sdrSrc})" : $"SDR {what}");

        EnsureDevice();
        var item = mode == CaptureMode.Window ? CreateItemForWindow(hwndTarget) : CreateItemForMonitor(hmon);

        var pixFmt = hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, pixFmt, 2, item.Size);
        using var session = pool.CreateCaptureSession(item);

        Direct3D11CaptureFrame frame = null;
        using var gotFrame = new ManualResetEventSlim(false);
        pool.FrameArrived += (s, _) =>
        {
            var f = s.TryGetNextFrame();
            if (f != null && Interlocked.CompareExchange(ref frame, f, null) == null) gotFrame.Set();
            else f?.Dispose();
        };

        try { session.IsCursorCaptureEnabled = cfg.CaptureCursor; } catch { }
        try { session.IsBorderRequired = false; } catch { }
        session.StartCapture();

        if (!gotFrame.Wait(3000))
            throw new TimeoutException("No frame received in 3 s (capture blocked?)");

        using (frame)
        {
            using var srcTex = GetTexture(frame.Surface);
            var desc = srcTex.Description;
            int w = Math.Min((int)desc.Width, frame.ContentSize.Width);
            int h = Math.Min((int)desc.Height, frame.ContentSize.Height);

            var stagingDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };
            using var staging = _device.CreateTexture2D(stagingDesc);
            _context.CopyResource(staging, srcTex);

            var map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                byte[] bgra;
                if (!hdr)
                    bgra = CopyBgra(map.DataPointer, (int)map.RowPitch, w, h);
                else
                {
                    float scale = 80f / Math.Max(sdrNits, 40f); // SDR white -> 1.0
                    var (maxRaw, absMax) = LuminanceStats(map.DataPointer, (int)map.RowPitch, w, h); // scRGB (1.0 = 80 nits)
                    if (maxRaw * scale <= 1.02f && !ForceToneMap)
                    {
                        // nothing significant above SDR white: just rescale
                        Program.Log($"SDR range, no tone mapping (p99.99 = {maxRaw * 80f:0} nits, max = {absMax * 80f:0} nits)");
                        // heuristic only valid for a screen capture
                        if (mode == CaptureMode.Monitor)
                            WarnIfLooksLikeDesktop(fgName, fgExe, maxRaw * 80f, absMax * 80f, sdrNits);
                        bgra = ToneMapScRgbToBgra(map.DataPointer, (int)map.RowPitch, w, h, sdrNits, 1f); // clipping, no Reinhard
                    }
                    else
                    {
                        float maxNits = Math.Min(maxRaw * 80f, 10000f);
                        float targetNits = Math.Min(Math.Clamp(cfg.TonemapHeadroom, 1f, 4f) * sdrNits, maxNits);
                        try
                        {
                            bgra = ToneMapWithD2D(srcTex, w, h, maxNits, targetNits);
                            Program.Log($"D2D tone map: MaxCLL(p99.99) {maxNits:0} nits (abs max {absMax * 80f:0}) -> target {targetNits:0} nits");
                        }
                        catch (Exception ex)
                        {
                            ResetD2D();   // the D2D device may be stuck: EnsureD2D will recreate it
                            Program.Log($"D2D tone map failed ({ex.Message}) -> CPU fallback (white = p99.99 = {maxRaw * scale:0.00} x SDR white; <= 1.005 => plain clipping)");
                            bgra = ToneMapScRgbToBgra(map.DataPointer, (int)map.RowPitch, w, h, sdrNits, maxRaw * scale);
                        }
                    }
                }
                return SavePng(bgra, w, h, cfg, appName);
            }
            finally { _context.Unmap(staging, 0); }
        }
    }

    // --- WinRT / DXGI helpers ---

    static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
        IntPtr abi = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(abi); }
        finally { Marshal.Release(abi); }
    }

    static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
        IntPtr abi = interop.CreateForMonitor(hmon, ref iid);
        try { return GraphicsCaptureItem.FromAbi(abi); }
        finally { Marshal.Release(abi); }
    }

    static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        return new ID3D11Texture2D(access.GetInterface(ref iid));
    }

    static bool IsMonitorHdr(IntPtr hmon)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
                using (adapter)
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                        using (output)
                            if (output.Description.Monitor == hmon)
                            {
                                using var o6 = output.QueryInterface<IDXGIOutput6>();
                                return o6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                            }
        }
        catch { }
        return false;
    }

    // --- Pixels ---

    /// Frame luminance in scRGB units (1.0 = 80 nits), NaN/Inf ignored.
    /// p9999 = 99.99th percentile (robust MaxCLL, as in the Microsoft sample) - one stray
    /// pixel must not dictate the tone mapping curve for the whole image.
    internal static unsafe (float p9999, float max) LuminanceStats(IntPtr data, int rowPitch, int w, int h)
    {
        const int bins = 4096;
        const float range = 32f; // 0..32 scRGB = 0..2560 nits, beyond that -> last bin
        int stripes = Math.Min(Math.Max(1, Environment.ProcessorCount), h);
        var hists = new int[stripes][];
        var maxes = new float[stripes];
        int rowsPer = (h + stripes - 1) / stripes;
        Parallel.For(0, stripes, s =>
        {
            var hist = new int[bins];
            float localMax = 0f;
            int y0 = s * rowsPer, y1 = Math.Min(h, y0 + rowsPer);
            for (int y = y0; y < y1; y++)
            {
                Half* px = (Half*)((byte*)data + (long)y * rowPitch);
                for (int x = 0; x < w; x++)
                {
                    float r = Math.Max(0f, (float)px[x * 4 + 0]);
                    float g = Math.Max(0f, (float)px[x * 4 + 1]);
                    float b = Math.Max(0f, (float)px[x * 4 + 2]);
                    float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    if (!float.IsFinite(lum)) continue;
                    if (lum > localMax) localMax = lum;
                    int bin = (int)(lum * (bins / range));
                    hist[bin < bins ? bin : bins - 1]++;
                }
            }
            hists[s] = hist;
            maxes[s] = localMax;
        });

        var total = new long[bins];
        long n = 0;
        for (int s = 0; s < stripes; s++)
            if (hists[s] != null)
                for (int i = 0; i < bins; i++) { total[i] += hists[s][i]; n += hists[s][i]; }

        long ignore = n / 10000; // the brightest 0.01 %
        long acc = 0;
        int top = bins - 1;
        for (; top > 0; top--) { acc += total[top]; if (acc > ignore) break; }

        float max = maxes.Max();
        return (Math.Min((top + 1) * (range / bins), max), max);
    }

    /// Tone mapping through the Direct2D "HDR Tone Map" effect (the one Photos/Snipping Tool use):
    /// scRGB [0..maxNits] -> [0..targetNits] on the GPU, then white = targetNits at sRGB encoding.
    static unsafe byte[] ToneMapWithD2D(ID3D11Texture2D srcTex, int w, int h, float maxNits, float targetNits)
    {
        EnsureD2D();

        var texDesc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        };
        using var srcCopy = _device.CreateTexture2D(texDesc);
        _context.CopySubresourceRegion(srcCopy, 0, 0, 0, 0, srcTex, 0,
            new Vortice.Mathematics.Box(0, 0, 0, w, h, 1));

        texDesc.BindFlags = BindFlags.RenderTarget;
        using var dstTex = _device.CreateTexture2D(texDesc);

        var fmt = new Vortice.DCommon.PixelFormat(Format.R16G16B16A16_Float, Vortice.DCommon.AlphaMode.Ignore);
        using var srcSurf = srcCopy.QueryInterface<IDXGISurface>();
        using var dstSurf = dstTex.QueryInterface<IDXGISurface>();
        using var srcBmp = _d2d.CreateBitmapFromDxgiSurface(srcSurf, new BitmapProperties1(fmt));
        using var dstBmp = _d2d.CreateBitmapFromDxgiSurface(dstSurf,
            new BitmapProperties1(fmt, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));

        using var toneMap = new Vortice.Direct2D1.Effects.HdrToneMap(_d2d)
        {
            InputMaxLuminance = maxNits,
            OutputMaxLuminance = targetNits,
            DisplayMode = HDRToneMapDisplayMode.Sdr,
        };
        toneMap.SetInput(0, srcBmp, true);

        _d2d.Target = dstBmp;
        try
        {
            _d2d.BeginDraw();
            _d2d.Clear(new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
            _d2d.DrawImage(toneMap.Output);
            _d2d.EndDraw().CheckError();
        }
        finally { _d2d.Target = null; }   // otherwise the target points at a disposed bitmap

        // CPU readback + sRGB encoding (the tone map's output white = targetNits -> 1.0)
        texDesc.BindFlags = BindFlags.None;
        texDesc.Usage = ResourceUsage.Staging;
        texDesc.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = _device.CreateTexture2D(texDesc);
        _context.CopyResource(staging, dstTex);
        var map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            IntPtr data = map.DataPointer;
            int rowPitch = (int)map.RowPitch;
            float scale = 80f / targetNits;
            var rgb = new float[(long)w * h * 3];
            Parallel.For(0, h, y => ReadHdrRow((byte*)data + (long)y * rowPitch, rgb, y * w * 3, w, scale));
            var bgra = new byte[(long)w * h * 4];
            Parallel.For(0, h, y => EncodeRow(rgb, bgra, y, w, false, 0f));
            return bgra;
        }
        finally { _context.Unmap(staging, 0); }
    }

    // CPU path: scRGB FP16 (linear, 1.0 = 80 nits) -> BGRA8 sRGB.
    // whiteY = extended Reinhard white, in SDR white units (1.0 = sdrNits).
    // whiteY <= 1.005 => no compression, just clipping at 1.0 (the "SDR range" path).
    // The caller passes the 99.99th percentile (not the absolute max) so that a lone
    // pixel does not dictate the curve for the whole image.
    internal static unsafe byte[] ToneMapScRgbToBgra(IntPtr data, int rowPitch, int w, int h, float sdrNits, float whiteY)
    {
        float scale = 80f / Math.Max(sdrNits, 40f); // SDR white -> 1.0
        var rgb = new float[(long)w * h * 3];
        Parallel.For(0, h, y => ReadHdrRow((byte*)data + (long)y * rowPitch, rgb, y * w * 3, w, scale));

        bool tone = whiteY > 1.005f;
        float invWhite2 = tone ? 1f / (whiteY * whiteY) : 0f;

        var bgra = new byte[(long)w * h * 4];
        Parallel.For(0, h, y => EncodeRow(rgb, bgra, y, w, tone, invWhite2));
        return bgra;
    }

    internal static unsafe void ReadHdrRow(byte* row, float[] rgb, int offset, int w, float scale)
    {
        Half* px = (Half*)row;
        for (int x = 0; x < w; x++)
        {
            rgb[offset++] = Sane((float)px[x * 4 + 0]) * scale;
            rgb[offset++] = Sane((float)px[x * 4 + 1]) * scale;
            rgb[offset++] = Sane((float)px[x * 4 + 2]) * scale;
        }
    }

    /// NaN -> 0, negative -> 0, +Inf -> Half's max (65504, clipped to white at encoding).
    internal static float Sane(float v) => float.IsNaN(v) ? 0f : Math.Clamp(v, 0f, 65504f);

    internal static void EncodeRow(float[] rgb, byte[] bgra, int y, int w, bool tone, float invWhite2)
    {
        int src = y * w * 3, dst = y * w * 4;
        for (int x = 0; x < w; x++)
        {
            float r = rgb[src++], g = rgb[src++], b = rgb[src++];
            if (tone)
            {
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                if (lum > 0f)
                {
                    // Extended Reinhard over the whole range: L' = L(1 + L/whiteY^2)/(1 + L)
                    float ratio = (1f + lum * invWhite2) / (1f + lum);
                    r *= ratio; g *= ratio; b *= ratio;
                }
            }
            bgra[dst++] = SrgbByte(b);
            bgra[dst++] = SrgbByte(g);
            bgra[dst++] = SrgbByte(r);
            bgra[dst++] = 255;
        }
    }

    internal static byte SrgbByte(float c)
    {
        if (c <= 0f) return 0;
        if (c >= 1f) return 255;
        float s = c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        return (byte)(s * 255f + 0.5f);
    }

    internal static unsafe byte[] CopyBgra(IntPtr data, int rowPitch, int w, int h)
    {
        var bgra = new byte[(long)w * h * 4];
        Parallel.For(0, h, y =>
        {
            fixed (byte* dst = &bgra[(long)y * w * 4])
            {
                Buffer.MemoryCopy((byte*)data + (long)y * rowPitch, dst, w * 4, w * 4);
                for (int x = 3; x < w * 4; x += 4) dst[x] = 255; // opaque alpha
            }
        });
        return bgra;
    }

    static string SavePng(byte[] bgra, int w, int h, Config cfg, string appName)
    {
        // one subfolder per foreground game/app, root otherwise
        string dir = string.IsNullOrWhiteSpace(appName) ? cfg.OutputDir : Path.Combine(cfg.OutputDir, appName);
        Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        string path = Path.Combine(dir, $"{stamp}.png");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{stamp} ({i}).png");

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(bgra, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        }
        finally { bmp.UnlockBits(bd); }
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    /// Name (sanitised for use as a folder name) and exe path of the foreground process.
    /// name == null for the desktop or for HDRSnap itself: no subfolder in that case.
    static (string name, string exe) ForegroundApp()
    {
        try
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (null, null);
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            if (name is "explorer" or "HDRSnap" or "" or null) return (null, null);
            string exe = ExePath(pid);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return (name, exe);
        }
        catch { return (null, null); }
    }

    /// QueryFullProcessImageName rather than MainModule: it also works on protected
    /// games (Denuvo, anti-cheat) where module access is denied.
    static string ExePath(uint pid)
    {
        IntPtr h = Native.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
    }

    /// Fullscreen optimizations turn exclusive fullscreen into a flip composed by the
    /// DWM: that is what makes WGC capture possible in games at all. The "disable
    /// fullscreen optimizations" checkbox writes this compat flag, per exe.
    static bool FsoDisabled(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        const string sub = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue(exe) is string v &&
                    v.IndexOf("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            catch { }
        }
        return false;
    }

    /// Heuristic: in *truly* exclusive fullscreen, WGC renders the composed desktop
    /// behind the game, clipped exactly at SDR white. If a game is in the foreground
    /// and nothing goes above that ceiling, the capture is probably the desktop.
    /// False positive possible on a dark scene entirely below SDR white: we only log
    /// (no balloon), the PNG is saved normally.
    static void WarnIfLooksLikeDesktop(string fgName, string fgExe, float p9999Nits, float maxNits, float sdrNits)
    {
        if (fgName == null) return;                        // desktop in the foreground: normal
        float tol = sdrNits * 0.005f;
        if (Math.Abs(maxNits - sdrNits) > tol) return;     // above SDR white: a real game frame
        if (Math.Abs(p9999Nits - sdrNits) > tol) return;   // not clipped flat

        string msg = $"{fgName} is in the foreground but the frame is clipped exactly at SDR white "
                   + $"({sdrNits:0} nits): this is probably the desktop, not the game.";
        msg += FsoDisabled(fgExe)
            ? " Fullscreen optimizations are DISABLED for this exe:"
              + " untick 'Disable fullscreen optimizations' in its properties."
            : " Switch the game to borderless windowed.";
        Program.Log("WARNING " + msg);
    }
}

// ---------------------------------------------------------------------------
// Tray + hotkey
// ---------------------------------------------------------------------------
internal class TrayApp : Form
{
    readonly Config _cfg;
    readonly NotifyIcon _tray;
    string _lastFile;
    bool _busy;

    // Hotkey through a low-level keyboard hook rather than RegisterHotKey: a game that
    // registers for raw input with RIDEV_NOHOTKEYS (Pragmata) disables every application
    // hotkey while it has focus - RegisterHotKey stays "registered" but WM_HOTKEY never
    // arrives. The hook runs before that stage.
    Native.LowLevelKeyboardProc _hookProc;
    IntPtr _hook = IntPtr.Zero;
    internal readonly record struct HotkeyBinding(uint Mods, uint Vk, string Label, CaptureMode Mode);
    HotkeyBinding[] _hotkeys = Array.Empty<HotkeyBinding>();   // [0] = screen (always), [1] = window (if enabled)
    uint _heldVk;   // vk of the held hotkey (auto-repeat guard), 0 otherwise
    IntPtr _fgBeforeMenu;   // foreground window just before the tray menu opens
    ToolStripMenuItem _miScreen, _miWindow;
    FileSystemWatcher _watcher;
    System.Windows.Forms.Timer _reloadDebounce;   // editors write in several passes
    int _reloadRetries;   // re-reads already attempted on a locked / incomplete file
    // Windows drops a LL hook without warning if it exceeds LowLevelHooksTimeout
    // (GC pause, UI thread busy during a keypress). No API lets you find out:
    // reinstall it periodically, on the UI thread.
    System.Windows.Forms.Timer _rehook;
    bool _rearmFailed;   // avoids logging the same re-arm failure every minute

    /// Embedded icon rather than ExtractAssociatedIcon: that one only yields 32x32,
    /// while the notification area wants the system size (16 at 100 %, 20 at 125 %).
    static Icon AppIcon()
    {
        // Log the fallback: without it, a missing resource or an unreadable ICO just
        // shows up as "the default Windows icon", with nothing to say so.
        var want = SystemInformation.SmallIconSize;
        try
        {
            using var s = typeof(TrayApp).Assembly.GetManifestResourceStream("HDRSnap.ico");
            if (s != null)
            {
                var icon = new Icon(s, want);
                Program.Log($"tray icon: embedded HDRSnap.ico, asked {want.Width}x{want.Height}, got {icon.Width}x{icon.Height}");
                return icon;
            }
            Program.Log("WARNING tray icon: embedded resource HDRSnap.ico not found, using the default Windows icon");
        }
        catch (Exception ex)
        {
            Program.Log($"WARNING tray icon: {ex.GetType().Name} {ex.Message}, using the default Windows icon");
        }
        return SystemIcons.Application;
    }

    public TrayApp(Config cfg)
    {
        _cfg = cfg;
        ShowInTaskbar = false;

        var (table, hkWarnings) = BuildHotkeys(_cfg);
        _hotkeys = table;
        foreach (var w in hkWarnings) Program.Log("ERR " + w);

        var menu = new ContextMenuStrip();
        _miScreen = new ToolStripMenuItem("", null, (_, _) => Shoot(CaptureMode.Monitor));
        // _fgBeforeMenu: when the menu opens, WinForms brings a hidden HDRSnap window TO
        // THE FOREGROUND (otherwise the menu never closes), so GetForegroundWindow at
        // click time would return our own window. Reuse the one noted on mouse down.
        _miWindow = new ToolStripMenuItem("", null, (_, _) => Shoot(CaptureMode.Window, _fgBeforeMenu));
        menu.Items.Add(_miScreen);
        menu.Items.Add(_miWindow);
        menu.Items.Add("Open folder", null, (_, _) => OpenFolder());
        menu.Items.Add("Choose output folder…", null, (_, _) => ChooseFolder());
        menu.Items.Add("Edit configuration…", null, (_, _) => OpenConfigFile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { _tray.Visible = false; Application.Exit(); });

        _tray = new NotifyIcon
        {
            Icon = AppIcon(),
            Text = $"HDRSnap — {_hotkeys[0].Label}",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // Noted on mouse down: at that instant the user's window is still in the foreground.
        RefreshHotkeyLabels();
        _tray.MouseDown += (_, _) => _fgBeforeMenu = Native.GetForegroundWindow();
        _tray.DoubleClick += (_, _) => OpenFolder();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_lastFile != null && File.Exists(_lastFile))
                Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
        };

        // Before InstallHook: the shell shows only one balloon at a time per icon, and
        // a hook failure (no hotkey works at all) outranks the F11 fallback.
        if (hkWarnings.Count > 0)
            _tray.ShowBalloonTip(6000, "HDRSnap — hotkey", string.Join("\n", hkWarnings), ToolTipIcon.Warning);

        InstallHook();

        _rehook = new System.Windows.Forms.Timer { Interval = 60_000 };
        _rehook.Tick += (_, _) => ReinstallHook();
        _rehook.Start();

        _reloadDebounce = new System.Windows.Forms.Timer { Interval = 500 };
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); ReloadConfig(); };
        try
        {
            Directory.CreateDirectory(Config.Dir);
            _watcher = new FileSystemWatcher(Config.Dir, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += OnConfigFileChanged;
            _watcher.Created += OnConfigFileChanged;
            _watcher.Renamed += OnConfigFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Program.Log($"cannot watch config.json ({ex.Message})"); }
    }

    void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // event on a pool thread -> back to the UI thread, then debounce.
        // The window can be destroyed between the test and the call (quit during a
        // write): without the catch, the exception would kill the process on a pool thread.
        try
        {
            if (IsHandleCreated) BeginInvoke(new Action(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); }));
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    void ReloadConfig()
    {
        // Many editors save atomically (temp file then rename): the file disappears for
        // an instant. Config.Load() would take that chance to WRITE defaults and we would
        // apply them. Do nothing: the rename event will follow.
        if (!File.Exists(Config.FilePath)) return;

        var fresh = Config.Load();
        if (Config.LoadWarning != null)
        {
            // File may still be locked or half-written: retry before complaining.
            if (_reloadRetries++ < 2) { _reloadDebounce.Start(); return; }
            _reloadRetries = 0;
            Program.Log("WARNING " + Config.LoadWarning);
            // What is on disk is no longer what we read: a Save() will have to back it up.
            _cfg.LoadedFromFile = false;
            _tray.ShowBalloonTip(5000, "HDRSnap — config not reloaded", Config.LoadWarning, ToolTipIcon.Warning);
            return;                                  // keep the previous settings
        }
        _reloadRetries = 0;
        // our own Save() calls (ChooseFolder) and duplicate events: nothing to do if identical
        if (JsonSerializer.Serialize(fresh) == JsonSerializer.Serialize(_cfg)) return;

        _cfg.CopyFrom(fresh);
        _cfg.LoadedFromFile = true;   // our values do come from the current file
        var (table, warnings) = BuildHotkeys(_cfg);
        _hotkeys = table;
        foreach (var w in warnings) Program.Log("ERR " + w);
        RefreshHotkeyLabels();
        ReinstallHook();
        Program.Log($"config reloaded (hotkeys {string.Join(", ", _hotkeys.Select(h => h.Label))}, folder {_cfg.OutputDir})");
        _tray.ShowBalloonTip(2500, "HDRSnap",
            warnings.Count > 0 ? string.Join("\n", warnings) : $"Configuration reloaded — {_hotkeys[0].Label}",
            warnings.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.None);
    }

    void InstallHook()
    {
        _ = Handle;             // forces handle creation (the window stays invisible)
        _hookProc ??= HookProc; // keep a reference, otherwise the GC collects the delegate
        _hook = Native.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
        {
            Program.Log($"ERR keyboard hook failed (err {Marshal.GetLastWin32Error()})");
            _tray.ShowBalloonTip(4000, "HDRSnap", "Could not install the keyboard hook.", ToolTipIcon.Error);
        }
        else Program.Log($"active hotkeys: {string.Join(", ", _hotkeys.Select(h => $"{h.Label} ({h.Mode})"))} (low-level keyboard hook)");
    }

    void ReinstallHook()
    {
        // Install BEFORE removing: a transient SetWindowsHookEx failure must not destroy
        // a hook that was working. The two coexist for a moment, with no risk of double
        // capture (same delegate, and _heldVk blocks the second call).
        IntPtr fresh = Native.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, IntPtr.Zero, 0);
        if (fresh == IntPtr.Zero)
        {
            if (!_rearmFailed)   // one line per failure, not one per minute
            {
                _rearmFailed = true;
                Program.Log($"ERR re-arming the keyboard hook failed (err {Marshal.GetLastWin32Error()}), previous hook kept");
            }
            return;
        }

        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = fresh;
        // If the hook died while the key was held, the KEYUP was lost and _heldVk would
        // stay set: the next keystroke would be swallowed without capturing anything.
        _heldVk = 0;
        if (_rearmFailed)
        {
            _rearmFailed = false;
            Program.Log("keyboard hook re-arming OK again");
        }
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            uint vk = (uint)Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode, first field
            if (msg == 0x0101 /* WM_KEYUP */ || msg == 0x0105 /* WM_SYSKEYUP */)
            {
                if (vk == _heldVk) _heldVk = 0;
            }
            else if (msg == 0x0100 /* WM_KEYDOWN */ || msg == 0x0104 /* WM_SYSKEYDOWN */)
            {
                int i = FindHotkey(_hotkeys, vk, CurrentMods());
                if (i >= 0)
                {
                    if (_heldVk != vk)
                    {
                        _heldVk = vk;
                        var mode = _hotkeys[i].Mode;
                        // hand back control at once: Windows disables a hook that is too slow
                        BeginInvoke(new Action(() => Shoot(mode)));
                    }
                    return (IntPtr)1;   // key swallowed, the game never sees it
                }
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// Hotkey table from the config. Invalid Hotkey -> F11 fallback;
    /// WindowHotkey invalid or identical to Hotkey -> window capture disabled.
    internal static (HotkeyBinding[] table, List<string> warnings) BuildHotkeys(Config cfg)
    {
        var warnings = new List<string>();
        var list = new List<HotkeyBinding>();

        if (Config.TryParseHotkey(cfg.Hotkey, out uint mods, out uint vk, out string err))
            list.Add(new HotkeyBinding(mods, vk, cfg.Hotkey, CaptureMode.Monitor));
        else
        {
            warnings.Add($"invalid hotkey \"{cfg.Hotkey}\" ({err}) -> falling back to F11");
            list.Add(new HotkeyBinding(0, (uint)Keys.F11, "F11", CaptureMode.Monitor));
        }

        if (!string.IsNullOrWhiteSpace(cfg.WindowHotkey))
        {
            if (!Config.TryParseHotkey(cfg.WindowHotkey, out uint wm, out uint wv, out string werr))
                warnings.Add($"invalid WindowHotkey \"{cfg.WindowHotkey}\" ({werr}) -> window capture disabled");
            else if (wm == list[0].Mods && wv == list[0].Vk)
                warnings.Add($"WindowHotkey equals Hotkey ({cfg.WindowHotkey}) -> window capture disabled");
            else
                list.Add(new HotkeyBinding(wm, wv, cfg.WindowHotkey, CaptureMode.Window));
        }
        return (list.ToArray(), warnings);
    }

    /// Bits of the held modifiers, same values as Config.TryParseHotkey (Alt=1, Ctrl=2, Shift=4, Win=8).
    static uint CurrentMods()
    {
        static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        uint m = 0;
        if (Down(0x12)) m |= 0x1;                // Alt
        if (Down(0x11)) m |= 0x2;                // Ctrl
        if (Down(0x10)) m |= 0x4;                // Shift
        if (Down(0x5B) || Down(0x5C)) m |= 0x8;  // Win
        return m;
    }

    /// Index of the hotkey whose key AND modifiers match exactly, -1 otherwise.
    internal static int FindHotkey(HotkeyBinding[] table, uint vk, uint modsDown)
    {
        for (int i = 0; i < table.Length; i++)
            if (table[i].Vk == vk && table[i].Mods == modsDown) return i;
        return -1;
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    void Shoot(CaptureMode mode, IntPtr hwnd = default)
    {
        if (_busy) return;
        _busy = true;
        // off the UI thread so the message pump is not blocked
        Task.Run(() =>
        {
            try
            {
                string path = Capturer.CaptureToFile(_cfg, mode, hwnd);
                _lastFile = path;
                Program.Log($"OK {path}");
                // Sound first: immediate feedback, without waiting for the clipboard.
                if (_cfg.PlaySound) System.Media.SystemSounds.Asterisk.Play();
                bool copied = _cfg.CopyToClipboard && CopyToClipboard(path);
                // "copied" only when it really is: otherwise the user would paste the
                // previous clipboard content thinking they were pasting their capture.
                _tray.ShowBalloonTip(1500, "Screenshot",
                    Path.GetFileName(path) + (copied ? " — copied to clipboard" : ""), ToolTipIcon.None);
            }
            catch (Exception ex)
            {
                Program.Log($"ERR {ex}");
                _tray.ShowBalloonTip(4000, "HDRSnap — failed", ex.Message, ToolTipIcon.Error);
            }
            finally { _busy = false; }
        });
    }

    void RefreshHotkeyLabels()
    {
        _miScreen.Text = $"Capture screen ({_hotkeys[0].Label})";
        // Available, not Visible: the Visible getter is false until the menu has been
        // dropped down, so using it as the condition would leave the entry unlabelled.
        bool hasWindowHotkey = _hotkeys.Length > 1;
        _miWindow.Available = hasWindowHotkey;
        if (hasWindowHotkey) _miWindow.Text = $"Capture window ({_hotkeys[1].Label})";
        _tray.Text = $"HDRSnap — {_hotkeys[0].Label}";
    }

    void OpenConfigFile()
    {
        try
        {
            if (!File.Exists(Config.FilePath)) _cfg.Save();
            Process.Start(new ProcessStartInfo(Config.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Program.Log($"opening config failed ({ex.Message}) -> folder");
            Process.Start("explorer.exe", $"/select,\"{Config.FilePath}\"");
        }
    }

    void OpenFolder()
    {
        Directory.CreateDirectory(_cfg.OutputDir);
        Process.Start("explorer.exe", _cfg.OutputDir);
    }

    /// Clipboard: bitmap (DIB) + PNG stream, so Paint, Discord, browsers... all paste fine.
    /// Reading and decoding happen on the calling (worker) thread: the UI thread also runs
    /// HookProc, and Windows drops a keyboard hook that exceeds LowLevelHooksTimeout
    /// (~300 ms). Decoding a 4K PNG there would kill the hotkey.
    /// Only the final hand-off needs an STA thread, so only that goes through Invoke.
    bool CopyToClipboard(string path)
    {
        try
        {
            byte[] png = File.ReadAllBytes(path);
            using var bmpStream = new MemoryStream(png);   // must stay open as long as bmp lives
            using var bmp = new Bitmap(bmpStream);
            using var pngStream = new MemoryStream(png);
            var data = new DataObject();
            data.SetImage(bmp);
            data.SetData("PNG", false, pngStream);
            // copy: true => OleFlushClipboard renders every format before returning,
            // so disposing bmp and the streams right after is safe.
            Invoke(new Action(() => Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100)));
            return true;
        }
        catch (Exception ex)
        {
            Program.Log($"clipboard failed ({ex.Message})");   // non-blocking: the PNG is already on disk
            return false;
        }
    }

    void ChooseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Folder where captures are saved",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_cfg.OutputDir) ? _cfg.OutputDir : "",
            // InitialDirectory is only a default, ignored as soon as the picker has
            // remembered a last folder: SelectedPath is what really sets the starting point.
            SelectedPath = Directory.Exists(_cfg.OutputDir) ? _cfg.OutputDir : "",
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        // A capture already in flight reads OutputDir when it saves: it can therefore land
        // in the old or the new folder. Harmless, and the path actually used is logged on
        // every capture.
        _cfg.OutputDir = dlg.SelectedPath;          // takes effect on the next capture
        bool saved = _cfg.Save();
        Program.Log(saved
            ? $"output folder -> {_cfg.OutputDir}"
            : $"ERR config not saved ({Config.LastSaveError}); output folder -> {_cfg.OutputDir} (until restart)");
        _tray.ShowBalloonTip(3000, "HDRSnap",
            saved ? $"Captures go to: {_cfg.OutputDir}" : "Folder changed, but config.json could not be written.",
            saved ? ToolTipIcon.None : ToolTipIcon.Warning);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rehook?.Dispose();
            _watcher?.Dispose();
            _reloadDebounce?.Dispose();
            if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ---------------------------------------------------------------------------
// Toast identity (AppUserModelID)
// ---------------------------------------------------------------------------

/// Windows 11 renders a legacy balloon tip as a toast, and the toast does NOT use the
/// tray icon: it looks the app up by AppUserModelID. A process without one falls back
/// to the generic "application" icon, however good the tray icon is. Declaring an AUMID
/// is only half of it - the AUMID has to resolve to a Start Menu shortcut carrying the
/// same ID, and it is that shortcut's icon the toast shows.
internal static class AppIdentity
{
    public const string Aumid = "HDRSnap.Tray";

    static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");

    /// System.AppUserModel.ID
    static PROPERTYKEY PkeyAppUserModelId => new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    /// Call before any window or balloon exists: the shell caches the identity of a
    /// process the first time it shows something for it.
    public static void Ensure()
    {
        try
        {
            Native.SetCurrentProcessExplicitAppUserModelID(Aumid);
            string exe = Environment.ProcessPath;
            if (exe == null) return;
            string lnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), "HDRSnap.lnk");
            if (ShortcutPointsAt(lnk, exe)) return;
            CreateShortcut(lnk, exe);
            Program.Log($"start menu shortcut written for toast identity: {lnk}");
        }
        catch (Exception ex)
        {
            // Non-fatal: captures still work, only the toast icon stays generic.
            Program.Log($"WARNING toast identity: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// Rewriting the shortcut on every start would reset a pin the user may have made,
    /// so only touch it when it is missing or aimed somewhere else.
    static bool ShortcutPointsAt(string lnk, string exe)
    {
        if (!File.Exists(lnk)) return false;
        try
        {
            var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellLink));
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new System.Text.StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return string.Equals(sb.ToString(), exe, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void CreateShortcut(string lnk, string exe)
    {
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellLink));
        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe));
        link.SetDescription("HDR screenshots, tone mapped to SDR PNG");

        // The AUMID has to be on the shortcut before it is saved: it is what ties the
        // running process (same string) to this shortcut, and therefore to its icon.
        var store = (IPropertyStore)link;
        var key = PkeyAppUserModelId;
        var pv = new PROPVARIANT { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(Aumid) };
        try
        {
            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        finally { Marshal.FreeCoTaskMem(pv.p); }

        Directory.CreateDirectory(Path.GetDirectoryName(lnk));
        ((IPersistFile)link).Save(lnk, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// 24 bytes on x64: vt + 3 reserved ushorts, then the 16-byte union.
    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT
    {
        public ushort vt;
        ushort r1, r2, r3;
        public IntPtr p;
        IntPtr p2;
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        // Declared in vtable order; the unused slots still have to be here.
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file,
                     int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path,
                             int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }
}

// ---------------------------------------------------------------------------
internal static class Program
{
    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Config.Dir);
            File.AppendAllText(Path.Combine(Config.Dir, "hdrsnap.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        var cfg = Config.Load();
        if (Config.LoadWarning != null) Log("WARNING " + Config.LoadWarning);

        Capturer.ForceToneMap = args.Contains("--forcetonemap");

        if (args.Contains("--shoot"))
        {
            try { Log("OK " + Capturer.CaptureToFile(cfg, args.Contains("--window") ? CaptureMode.Window : CaptureMode.Monitor)); }
            catch (Exception ex) { Log("ERR " + ex); Environment.ExitCode = 1; }
            return;
        }

        using var mutex = new Mutex(true, "HDRSnap_single_instance", out bool isNew);
        if (!isNew) return;

        if (!GraphicsCaptureSession.IsSupported())
        {
            MessageBox.Show("Windows Graphics Capture is not supported on this system.", "HDRSnap");
            return;
        }

        // Before any window exists: the shell pins a process identity to the first thing it shows.
        AppIdentity.Ensure();

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(cfg));
    }
}
