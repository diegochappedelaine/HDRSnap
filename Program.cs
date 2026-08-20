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
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HDRSnap");
    public bool CaptureCursor { get; set; } = false;
    public bool PlaySound { get; set; } = true;
    public bool SubfolderPerApp { get; set; } = true;
    /// Marge au-dessus du blanc SDR allouée aux hautes lumières dans le PNG
    /// (cible du tone mapping = TonemapHeadroom x blanc SDR, en nits).
    public float TonemapHeadroom { get; set; } = 1.25f;

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HDRSnap");
    public static string FilePath => Path.Combine(Dir, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch { /* config corrompue -> défauts */ }
        var cfg = new Config();
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return cfg;
    }

    // "Ctrl+Shift+F11" -> (mods, vk)
    public (uint mods, uint vk) ParseHotkey()
    {
        uint mods = 0x4000; // MOD_NOREPEAT
        Keys key = Keys.F11;
        foreach (var raw in Hotkey.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default: key = Enum.Parse<Keys>(part, true); break;
            }
        }
        return (mods, (uint)key);
    }
}

// ---------------------------------------------------------------------------
// Interop
// ---------------------------------------------------------------------------
internal static class Native
{
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
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

    // --- DisplayConfig (niveau de blanc SDR du moniteur) ---
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
        public ulong refreshRate;
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

    /// Niveau de blanc SDR du moniteur en nits (slider Windows "luminosité SDR").
    /// L'API DisplayConfig échoue (ERROR_INVALID_PARAMETER) sur certaines builds Win11 :
    /// on retombe alors sur le registre (MonitorDataStore), puis sur 200 nits.
    public static (float nits, string source) GetSdrWhite(IntPtr hMonitor)
    {
        float? v = SdrWhiteFromDisplayConfig(hMonitor);
        if (v.HasValue) return (v.Value, "API");
        v = SdrWhiteFromRegistry();
        if (v.HasValue) return (v.Value, "registre");
        return (200f, "défaut");
    }

    static float? SdrWhiteFromRegistry()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore");
            if (root == null) return null;
            float best = 0f;
            foreach (var name in root.GetSubKeyNames())
                using (var k = root.OpenSubKey(name))
                    if (k?.GetValue("SDRWhiteLevel") is int lvl && lvl >= 1000 && lvl <= 10000)
                        best = Math.Max(best, lvl / 1000f * 80f);
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
                if (DisplayConfigGetDeviceInfo(ref sdr) == 0)
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
internal static class Capturer
{
    static ID3D11Device _device;
    static ID3D11DeviceContext _context;
    static IDirect3DDevice _winrtDevice;
    static ID2D1Factory1 _d2dFactory;
    static ID2D1Device _d2dDevice;
    static ID2D1DeviceContext _d2d;

    /// Debug (--forcetonemap) : passe par le tone mapper D2D même si la frame reste en plage SDR.
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

    static void ResetDevice()
    {
        try { _d2d?.Dispose(); } catch { }
        try { _d2dDevice?.Dispose(); } catch { }
        try { _winrtDevice?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _d2d = null; _d2dDevice = null; _winrtDevice = null; _context = null; _device = null;
    }

    public static string CaptureToFile(Config cfg)
    {
        try { return CaptureToFileCore(cfg); }
        catch
        {
            // reset device (TDR / driver update) et 2e tentative
            ResetDevice();
            return CaptureToFileCore(cfg);
        }
    }

    static string CaptureToFileCore(Config cfg)
    {
        string appName = cfg.SubfolderPerApp ? ForegroundAppName() : null;

        Native.GetCursorPos(out var pt);
        IntPtr hmon = Native.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);

        bool hdr = IsMonitorHdr(hmon);
        var (sdrNits, sdrSrc) = hdr ? Native.GetSdrWhite(hmon) : (200f, "n/a");
        Program.Log(hdr ? $"capture HDR (blanc SDR = {sdrNits:0} nits, source : {sdrSrc})" : "capture SDR");

        EnsureDevice();
        var item = CreateItemForMonitor(hmon);

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
            throw new TimeoutException("Aucune frame reçue en 3 s (capture bloquée ?)");

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
                    float scale = 80f / Math.Max(sdrNits, 40f); // blanc SDR -> 1.0
                    var (maxRaw, absMax) = LuminanceStats(map.DataPointer, (int)map.RowPitch, w, h); // scRGB (1.0 = 80 nits)
                    if (maxRaw * scale <= 1.02f && !ForceToneMap)
                    {
                        // rien de significatif au-dessus du blanc SDR : simple remise à l'échelle
                        Program.Log($"plage SDR, pas de tone mapping (p99,99 = {maxRaw * 80f:0} nits, max = {absMax * 80f:0} nits)");
                        bgra = ToneMapScRgbToBgra(map.DataPointer, (int)map.RowPitch, w, h, sdrNits);
                    }
                    else
                    {
                        float maxNits = Math.Min(maxRaw * 80f, 10000f);
                        float targetNits = Math.Min(Math.Clamp(cfg.TonemapHeadroom, 1f, 4f) * sdrNits, maxNits);
                        try
                        {
                            bgra = ToneMapWithD2D(srcTex, w, h, maxNits, targetNits);
                            Program.Log($"tone map D2D : MaxCLL(p99,99) {maxNits:0} nits (max abs {absMax * 80f:0}) -> cible {targetNits:0} nits");
                        }
                        catch (Exception ex)
                        {
                            Program.Log($"tone map D2D KO ({ex.Message}) -> fallback CPU Reinhard");
                            bgra = ToneMapScRgbToBgra(map.DataPointer, (int)map.RowPitch, w, h, sdrNits);
                        }
                    }
                }
                return SavePng(bgra, w, h, cfg, appName);
            }
            finally { _context.Unmap(staging, 0); }
        }
    }

    // --- WinRT / DXGI helpers ---

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

    /// Luminance de la frame en unités scRGB (1.0 = 80 nits), NaN/Inf ignorés.
    /// p9999 = percentile 99,99 (MaxCLL robuste, comme le sample Microsoft) — un pixel
    /// parasite isolé ne doit pas dicter la courbe de tone mapping de toute l'image.
    static unsafe (float p9999, float max) LuminanceStats(IntPtr data, int rowPitch, int w, int h)
    {
        const int bins = 4096;
        const float range = 32f; // 0..32 scRGB = 0..2560 nits, au-delà -> dernier bin
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

        long ignore = n / 10000; // les 0,01 % les plus lumineux
        long acc = 0;
        int top = bins - 1;
        for (; top > 0; top--) { acc += total[top]; if (acc > ignore) break; }

        float max = maxes.Max();
        return (Math.Min((top + 1) * (range / bins), max), max);
    }

    /// Tone mapping par l'effet Direct2D « HDR Tone Map » (celui de Photos/Snipping Tool) :
    /// scRGB [0..maxNits] -> [0..targetNits] sur GPU, puis blanc = targetNits à l'encodage sRGB.
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
        _d2d.BeginDraw();
        _d2d.Clear(new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
        _d2d.DrawImage(toneMap.Output);
        _d2d.EndDraw();
        _d2d.Target = null;

        // lecture CPU + encodage sRGB (le blanc de sortie du tonemap = targetNits -> 1.0)
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

    // Fallback CPU : scRGB FP16 (linéaire, 1.0 = 80 nits) -> BGRA8 sRGB tone-mappé
    // (Reinhard étendu sur TOUTE la plage, blanc = maxY — plus de genou à 0.85)
    static unsafe byte[] ToneMapScRgbToBgra(IntPtr data, int rowPitch, int w, int h, float sdrNits)
    {
        float scale = 80f / Math.Max(sdrNits, 40f); // blanc SDR -> 1.0
        var rgb = new float[(long)w * h * 3];

        // passe 1 : conversion + luminance max
        float maxY = 0f;
        object lk = new();
        Parallel.For(0, h,
            () => 0f,
            (y, _, localMax) => Math.Max(localMax, ReadHdrRow((byte*)data + (long)y * rowPitch, rgb, y * w * 3, w, scale)),
            localMax => { lock (lk) maxY = Math.Max(maxY, localMax); });

        // passe 2 : compression des hautes lumières + encodage sRGB
        bool tone = maxY > 1.005f;
        float invWhite2 = tone ? 1f / (maxY * maxY) : 0f;

        var bgra = new byte[(long)w * h * 4];
        Parallel.For(0, h, y => EncodeRow(rgb, bgra, y, w, tone, invWhite2));
        return bgra;
    }

    static unsafe float ReadHdrRow(byte* row, float[] rgb, int offset, int w, float scale)
    {
        float localMax = 0f;
        Half* px = (Half*)row;
        for (int x = 0; x < w; x++)
        {
            float r = Math.Max(0f, (float)px[x * 4 + 0]) * scale;
            float g = Math.Max(0f, (float)px[x * 4 + 1]) * scale;
            float b = Math.Max(0f, (float)px[x * 4 + 2]) * scale;
            float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            if (y > localMax) localMax = y;
            rgb[offset++] = r; rgb[offset++] = g; rgb[offset++] = b;
        }
        return localMax;
    }

    static void EncodeRow(float[] rgb, byte[] bgra, int y, int w, bool tone, float invWhite2)
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
                    // Reinhard étendu sur toute la plage : L' = L(1 + L/maxY²)/(1 + L)
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

    static byte SrgbByte(float c)
    {
        if (c <= 0f) return 0;
        if (c >= 1f) return 255;
        float s = c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        return (byte)(s * 255f + 0.5f);
    }

    static unsafe byte[] CopyBgra(IntPtr data, int rowPitch, int w, int h)
    {
        var bgra = new byte[(long)w * h * 4];
        Parallel.For(0, h, y =>
        {
            fixed (byte* dst = &bgra[(long)y * w * 4])
            {
                Buffer.MemoryCopy((byte*)data + (long)y * rowPitch, dst, w * 4, w * 4);
                for (int x = 3; x < w * 4; x += 4) dst[x] = 255; // alpha opaque
            }
        });
        return bgra;
    }

    static string SavePng(byte[] bgra, int w, int h, Config cfg, string appName)
    {
        // un sous-dossier par jeu/app au premier plan, sinon racine
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

    static string ForegroundAppName()
    {
        try
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            if (name is "explorer" or "HDRSnap" or "" or null) return null;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
        catch { return null; }
    }
}

// ---------------------------------------------------------------------------
// Tray + hotkey
// ---------------------------------------------------------------------------
internal class TrayApp : Form
{
    const int WM_HOTKEY = 0x0312;
    const int HOTKEY_ID = 0xB00B;

    readonly Config _cfg;
    readonly NotifyIcon _tray;
    string _lastFile;
    bool _busy;

    public TrayApp(Config cfg)
    {
        _cfg = cfg;
        ShowInTaskbar = false;

        var menu = new ContextMenuStrip();
        menu.Items.Add($"Capturer ({cfg.Hotkey})", null, (_, _) => Shoot());
        menu.Items.Add("Ouvrir le dossier", null, (_, _) => OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => { _tray.Visible = false; Application.Exit(); });

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = $"HDRSnap — {cfg.Hotkey}",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenFolder();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_lastFile != null && File.Exists(_lastFile))
                Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
        };

        if (!TryRegisterHotkey())
        {
            _tray.ShowBalloonTip(4000, "HDRSnap",
                $"{cfg.Hotkey} est pris par une autre app — nouvel essai toutes les 5 s.", ToolTipIcon.Warning);
            var retry = new System.Windows.Forms.Timer { Interval = 5000 };
            retry.Tick += (_, _) =>
            {
                if (!TryRegisterHotkey()) return;
                retry.Stop();
                retry.Dispose();
                _tray.ShowBalloonTip(2000, "HDRSnap", $"{_cfg.Hotkey} est maintenant actif.", ToolTipIcon.Info);
            };
            retry.Start();
        }
    }

    bool TryRegisterHotkey()
    {
        var (mods, vk) = _cfg.ParseHotkey();
        bool ok = Native.RegisterHotKey(Handle, HOTKEY_ID, mods, vk);
        Program.Log(ok ? $"hotkey {_cfg.Hotkey} enregistrée" : $"ERR hotkey {_cfg.Hotkey} indisponible (conflit)");
        return ok;
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID) Shoot();
        else base.WndProc(ref m);
    }

    void Shoot()
    {
        if (_busy) return;
        _busy = true;
        // hors du thread UI pour ne pas bloquer la pompe de messages
        Task.Run(() =>
        {
            try
            {
                string path = Capturer.CaptureToFile(_cfg);
                _lastFile = path;
                Program.Log($"OK {path}");
                if (_cfg.PlaySound) System.Media.SystemSounds.Asterisk.Play();
                _tray.ShowBalloonTip(1500, "Screenshot", Path.GetFileName(path), ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Program.Log($"ERR {ex}");
                _tray.ShowBalloonTip(4000, "HDRSnap — échec", ex.Message, ToolTipIcon.Error);
            }
            finally { _busy = false; }
        });
    }

    void OpenFolder()
    {
        Directory.CreateDirectory(_cfg.OutputDir);
        Process.Start("explorer.exe", _cfg.OutputDir);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Native.UnregisterHotKey(Handle, HOTKEY_ID);
            _tray.Dispose();
        }
        base.Dispose(disposing);
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

        Capturer.ForceToneMap = args.Contains("--forcetonemap");

        if (args.Contains("--shoot"))
        {
            try { Log("OK " + Capturer.CaptureToFile(cfg)); }
            catch (Exception ex) { Log("ERR " + ex); Environment.ExitCode = 1; }
            return;
        }

        using var mutex = new Mutex(true, "HDRSnap_single_instance", out bool isNew);
        if (!isNew) return;

        if (!GraphicsCaptureSession.IsSupported())
        {
            MessageBox.Show("Windows Graphics Capture non supporté sur ce système.", "HDRSnap");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(cfg));
    }
}
