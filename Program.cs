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
    /// Raccourci pour capturer uniquement la fenêtre au premier plan ("" = désactivé).
    public string WindowHotkey { get; set; } = "Ctrl+F11";
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HDRSnap");
    public bool CaptureCursor { get; set; } = false;
    public bool PlaySound { get; set; } = true;
    /// Copie aussi la capture dans le presse-papiers (PNG + bitmap). Mode tray uniquement.
    public bool CopyToClipboard { get; set; } = true;
    public bool SubfolderPerApp { get; set; } = true;
    /// Marge au-dessus du blanc SDR allouée aux hautes lumières dans le PNG
    /// (cible du tone mapping = TonemapHeadroom x blanc SDR, en nits).
    public float TonemapHeadroom { get; set; } = 1.25f;

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HDRSnap");
    public static string FilePath => Path.Combine(Dir, "config.json");

    /// Avertissement du dernier Load (null si RAS). Journalisé par Program.Main :
    /// Config ne journalise pas elle-même pour rester testable.
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
                LoadWarning = $"config vide ({path}) -> valeurs par défaut, fichier conservé";
            }
            catch (Exception ex)
            {
                LoadWarning = $"config illisible ({path} : {ex.Message}) -> valeurs par défaut, fichier conservé";
            }
            return new Config();   // ne JAMAIS écraser le fichier de l'utilisateur
        }

        var cfg = new Config();
        if (!cfg.Save(path))
            LoadWarning = $"config par défaut non écrite ({path} : {LastSaveError})";
        cfg.LoadedFromFile = true;   // ce fichier vient d'être écrit par nous : rien à préserver
        return cfg;
    }

    /// Raison de la dernière écriture échouée (null si la dernière a réussi).
    public static string LastSaveError { get; private set; }

    /// Vrai seulement si cette instance vient d'un fichier réellement relu.
    /// Champ (pas propriété) : System.Text.Json ne sérialise que les propriétés publiques.
    internal bool LoadedFromFile;

    public bool Save() => Save(FilePath);

    /// Écrit la config complète (indentée). false si l'écriture échoue ; l'appelant journalise.
    /// Écriture atomique (fichier temporaire puis remplacement) : une coupure de courant ou
    /// un disque plein ne laisse pas un config.json tronqué.
    public bool Save(string path)
    {
        LastSaveError = null;
        try
        {
            // GetDirectoryName rend "" pour un nom nu et null pour une racine : dans les
            // deux cas il n'y a pas de dossier à créer, et CreateDirectory lèverait.
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Load conserve exprès un fichier qu'il n'a pas su lire ; l'écraser ici ferait
            // perdre en silence les réglages que l'utilisateur y avait mis. On le sauve d'abord.
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

    /// Valeurs nulles/vides (ex. "Hotkey": null dans le JSON) -> défauts.
    Config Sanitized()
    {
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "F11";
        if (string.IsNullOrWhiteSpace(OutputDir)) OutputDir = new Config().OutputDir;
        WindowHotkey ??= "";   // null = désactivé, pas un plantage
        return this;
    }

    /// "Ctrl+Shift+F11" -> (mods, vk). Bits de mods : Alt=1, Ctrl=2, Shift=4, Win=8
    /// (mêmes valeurs que MOD_* de RegisterHotKey, comparées par TrayApp.FindHotkey).
    /// false + message si aucune touche, plusieurs touches, ou nom de touche inconnu
    /// (noms = membres de System.Windows.Forms.Keys, insensibles à la casse).
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
                    // Enum.TryParse accepte aussi la forme numérique : « 1 » donnerait
                    // Keys.LButton (vk 1), un hotkey accepté mais que le hook clavier
                    // ne verra jamais. On exige un nom de membre (« D1 », pas « 1 »).
                    if (char.IsDigit(part[0]) || part[0] == '-' || part[0] == '+')
                    { error = $"touche numérique « {part} » (utilise le nom du membre Keys, ex. D1)"; return false; }
                    if (!Enum.TryParse<Keys>(part, true, out var k) || !Enum.IsDefined(typeof(Keys), k))
                    { error = $"touche inconnue « {part} »"; return false; }
                    // Un code de touche virtuelle tient sur 1..254 ; au-delà (Keys.Modifiers,
                    // 0xFFFF0000) aucune frappe ne peut correspondre.
                    if ((uint)k == 0 || (uint)k > 0xFE)
                    { error = $"touche « {part} » hors plage des codes clavier (1-254)"; return false; }
                    if (key != null)
                    { error = $"plusieurs touches ({key} et {k})"; return false; }
                    key = k;
                    break;
            }
        }
        if (key == null) { error = "aucune touche (modificateurs seuls ?)"; return false; }
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
        public uint refreshRateNumerator, refreshRateDenominator; // DISPLAYCONFIG_RATIONAL : 2 x UINT32, aligné 4 (pas ulong : forcerait un padding de 8)
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
    /// L'API DisplayConfig renvoyait ERROR_INVALID_PARAMETER tant que
    /// DISPLAYCONFIG_PATH_TARGET_INFO faisait 56 octets au lieu de 48 (refreshRate déclaré
    /// en ulong) : la faute était ici, pas dans la build de Windows. Tailles verrouillées
    /// par InteropLayoutTests. Replis conservés : registre (MonitorDataStore), puis 200 nits.
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
                Program.Log($"blanc SDR (registre) : plusieurs moniteurs avec des valeurs différentes, max retenu ({best:0} nits)");
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
                    Program.Log($"blanc SDR : GET_SDR_WHITE_LEVEL a échoué (erreur {rc}) pour {mi.szDevice} -> repli registre");
                    continue;
                }
                // Même garde que le repli registre : une valeur hors plage (0 sur une cible
                // clonée inactive) donnerait un blanc nul et une image NaN en aval.
                if (sdr.SDRWhiteLevel < 1000 || sdr.SDRWhiteLevel > 10000)
                {
                    Program.Log($"blanc SDR : valeur API hors plage ({sdr.SDRWhiteLevel}) pour {mi.szDevice}, cible ignorée");
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

    /// Debug (--forcetonemap) : passe par le tone mapper D2D même si la frame reste en plage SDR.
    public static bool ForceToneMap;

    /// Avertissement non bloquant du dernier tir (null si RAS), lu par le tray pour la bulle.
    public static string LastWarning;

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

    /// hwnd : fenêtre à capturer en mode Window ; IntPtr.Zero = celle au premier plan.
    public static string CaptureToFile(Config cfg, CaptureMode mode = CaptureMode.Monitor, IntPtr hwnd = default)
    {
        try { return CaptureToFileCore(cfg, mode, hwnd); }
        catch (Exception ex) when (IsDeviceError(ex))
        {
            // device perdu (TDR / mise à jour pilote) : reset et 2e tentative
            Program.Log($"capture KO ({ex.GetType().Name} : {ex.Message}) -> reset device, 2e tentative");
            ResetDevice();
            return CaptureToFileCore(cfg, mode, hwnd);
        }
        catch (TimeoutException) when (mode == CaptureMode.Monitor)
        {
            // Après un TDR, WGC cesse souvent de livrer des frames sans lever d'erreur COM.
            // Le device est gardé en statique : sans reset il resterait mort pour TOUTES
            // les captures suivantes. On le jette, mais sans relancer la capture (pas de
            // seconde attente de 3 s) : la frappe suivante repartira sur un device neuf.
            // Réservé à l'écran : une fenêtre non capturable (WDA_EXCLUDEFROMCAPTURE, fenêtre
            // élevée) ne livre jamais de frame sans que le device soit en cause, et détruire
            // le device partagé pénaliserait toutes les captures suivantes.
            Program.Log("capture KO (timeout) -> reset device, pas de 2e tentative");
            ResetDevice();
            throw;
        }
    }

    /// Seules les erreurs D3D/DXGI/WinRT justifient un reset du device + retry.
    /// Timeout de frame, E/S disque, chemin invalide : on échoue tout de suite.
    internal static bool IsDeviceError(Exception ex) =>
        ex is SharpGen.Runtime.SharpGenException || ex is COMException;

    static string CaptureToFileCore(Config cfg, CaptureMode mode, IntPtr hwnd = default)
    {
        LastWarning = null;
        var (fgName, fgExe) = ForegroundApp();
        string appName = cfg.SubfolderPerApp ? fgName : null;

        IntPtr hwndTarget = IntPtr.Zero, hmon;
        if (mode == CaptureMode.Window)
        {
            // hwnd fourni par l'appelant (menu du tray) sinon la fenêtre au premier plan (hotkey).
            hwndTarget = hwnd != IntPtr.Zero ? hwnd : Native.GetForegroundWindow();
            if (hwndTarget == IntPtr.Zero) throw new InvalidOperationException("Aucune fenêtre au premier plan.");
            hmon = Native.MonitorFromWindow(hwndTarget, 2 /* MONITOR_DEFAULTTONEAREST */);
        }
        else
        {
            Native.GetCursorPos(out var pt);
            hmon = Native.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        }

        bool hdr = IsMonitorHdr(hmon);
        var (sdrNits, sdrSrc) = hdr ? Native.GetSdrWhite(hmon) : (200f, "n/a");
        string what = mode == CaptureMode.Window ? "capture fenêtre" : "capture écran";
        Program.Log(hdr ? $"{what} HDR (blanc SDR = {sdrNits:0} nits, source : {sdrSrc})" : $"{what} SDR");

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
                        // heuristique valable pour une capture écran seulement
                        if (mode == CaptureMode.Monitor)
                            WarnIfLooksLikeDesktop(fgName, fgExe, maxRaw * 80f, absMax * 80f, sdrNits);
                        bgra = ToneMapScRgbToBgra(map.DataPointer, (int)map.RowPitch, w, h, sdrNits, 1f); // écrêtage, pas de Reinhard
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
                            ResetD2D();   // le device D2D peut être coincé : EnsureD2D le recréera
                            Program.Log($"tone map D2D KO ({ex.Message}) -> fallback CPU (blanc = p99,99 = {maxRaw * scale:0.00} x blanc SDR ; <= 1,005 => simple écrêtage)");
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

    /// Luminance de la frame en unités scRGB (1.0 = 80 nits), NaN/Inf ignorés.
    /// p9999 = percentile 99,99 (MaxCLL robuste, comme le sample Microsoft) — un pixel
    /// parasite isolé ne doit pas dicter la courbe de tone mapping de toute l'image.
    internal static unsafe (float p9999, float max) LuminanceStats(IntPtr data, int rowPitch, int w, int h)
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
        try
        {
            _d2d.BeginDraw();
            _d2d.Clear(new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
            _d2d.DrawImage(toneMap.Output);
            _d2d.EndDraw().CheckError();
        }
        finally { _d2d.Target = null; }   // sinon la cible pointe sur un bitmap disposé

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

    // Chemin CPU : scRGB FP16 (linéaire, 1.0 = 80 nits) -> BGRA8 sRGB.
    // whiteY = blanc du Reinhard étendu, en unités de blanc SDR (1.0 = sdrNits).
    // whiteY <= 1.005 => aucune compression, simple écrêtage à 1.0 (chemin « plage SDR »).
    // L'appelant fournit le p99,99 (pas le max absolu) pour qu'un pixel isolé ne dicte
    // pas la courbe de toute l'image.
    internal static unsafe byte[] ToneMapScRgbToBgra(IntPtr data, int rowPitch, int w, int h, float sdrNits, float whiteY)
    {
        float scale = 80f / Math.Max(sdrNits, 40f); // blanc SDR -> 1.0
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

    /// NaN -> 0, négatif -> 0, +Inf -> max d'un Half (65504, écrêté en blanc à l'encodage).
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
                    // Reinhard étendu sur toute la plage : L' = L(1 + L/whiteY²)/(1 + L)
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

    /// Nom (assaini pour servir de nom de dossier) et chemin de l'exe au premier plan.
    /// name == null pour le bureau ou HDRSnap lui-meme : pas de sous-dossier dans ce cas.
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

    /// QueryFullProcessImageName plutot que MainModule : marche aussi sur les jeux
    /// proteges (Denuvo, anti-triche) ou l'acces aux modules est refuse.
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

    /// Les Fullscreen Optimizations convertissent le plein ecran exclusif en flip compose
    /// par le DWM : c'est ce qui rend la capture WGC possible en jeu. La case "desactiver
    /// les optimisations du mode plein ecran" ecrit ce flag de compat, par exe.
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

    /// Heuristique : en plein ecran *vraiment* exclusif, WGC rend le bureau compose
    /// derriere le jeu, ecrete pile au blanc SDR. Si un jeu est au premier plan et que
    /// rien ne depasse ce plafond, la capture est probablement le bureau, pas le jeu.
    /// Faux positif possible sur une scene sombre entierement sous le blanc SDR : on
    /// avertit sans bloquer, le PNG est enregistre normalement.
    static void WarnIfLooksLikeDesktop(string fgName, string fgExe, float p9999Nits, float maxNits, float sdrNits)
    {
        if (fgName == null) return;                        // bureau au premier plan : normal
        float tol = sdrNits * 0.005f;
        if (Math.Abs(maxNits - sdrNits) > tol) return;     // ca depasse le blanc SDR : vraie frame de jeu
        if (Math.Abs(p9999Nits - sdrNits) > tol) return;   // pas ecrete a plat

        string msg = $"{fgName} est au premier plan mais la frame est ecretee pile au blanc SDR "
                   + $"({sdrNits:0} nits) : c'est probablement le bureau et pas le jeu.";
        msg += FsoDisabled(fgExe)
            ? " Les optimisations du mode plein ecran sont DESACTIVEES pour cet exe :"
              + " decoche 'Desactiver les optimisations du mode plein ecran' dans ses proprietes."
            : " Passe le jeu en fenetre sans bordure.";
        Program.Log("ATTENTION " + msg);
        LastWarning = msg;
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

    // Hotkey via hook clavier bas niveau plutot que RegisterHotKey : un jeu qui
    // s'enregistre en raw input avec RIDEV_NOHOTKEYS (Pragmata) desactive tous les
    // hotkeys applicatifs tant qu'il a le focus - RegisterHotKey reste « enregistree »
    // mais WM_HOTKEY n'arrive jamais. Le hook, lui, est appele avant cette etape.
    Native.LowLevelKeyboardProc _hookProc;
    IntPtr _hook = IntPtr.Zero;
    internal readonly record struct HotkeyBinding(uint Mods, uint Vk, string Label, CaptureMode Mode);
    HotkeyBinding[] _hotkeys = Array.Empty<HotkeyBinding>();   // [0] = écran (toujours), [1] = fenêtre (si activée)
    uint _heldVk;   // vk de la hotkey enfoncée (anti auto-répétition), 0 sinon
    IntPtr _fgBeforeMenu;   // fenêtre au premier plan juste avant l'ouverture du menu du tray
    // Windows retire un hook LL sans prévenir s'il dépasse LowLevelHooksTimeout
    // (pause GC, thread UI occupé pendant un appui). Aucune API ne permet de le
    // savoir : on le réinstalle périodiquement, sur le thread UI.
    System.Windows.Forms.Timer _rehook;
    bool _rearmFailed;   // évite de journaliser la même panne de réarmement chaque minute

    public TrayApp(Config cfg)
    {
        _cfg = cfg;
        ShowInTaskbar = false;

        var (table, hkWarnings) = BuildHotkeys(_cfg);
        _hotkeys = table;
        foreach (var w in hkWarnings) Program.Log("ERR " + w);

        var menu = new ContextMenuStrip();
        menu.Items.Add($"Capturer l'écran ({_hotkeys[0].Label})", null, (_, _) => Shoot(CaptureMode.Monitor));
        if (_hotkeys.Length > 1)
            // _fgBeforeMenu : à l'ouverture du menu, WinForms met AU PREMIER PLAN une fenêtre
            // cachée de HDRSnap (sinon le menu ne se referme pas), donc GetForegroundWindow au
            // moment du clic rendrait notre propre fenêtre. On réutilise celle notée au clic.
            menu.Items.Add($"Capturer la fenêtre ({_hotkeys[1].Label})", null, (_, _) => Shoot(CaptureMode.Window, _fgBeforeMenu));
        menu.Items.Add("Ouvrir le dossier", null, (_, _) => OpenFolder());
        menu.Items.Add("Choisir le dossier de sortie…", null, (_, _) => ChooseFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => { _tray.Visible = false; Application.Exit(); });

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = $"HDRSnap — {_hotkeys[0].Label}",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // Noté au clic : à cet instant la fenêtre de l'utilisateur est encore au premier plan.
        _tray.MouseDown += (_, _) => _fgBeforeMenu = Native.GetForegroundWindow();
        _tray.DoubleClick += (_, _) => OpenFolder();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_lastFile != null && File.Exists(_lastFile))
                Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
        };

        // Avant InstallHook : le shell n'affiche qu'un ballon à la fois par icône, et
        // l'échec du hook (aucun hotkey ne marche) prime sur le repli F11.
        if (hkWarnings.Count > 0)
            _tray.ShowBalloonTip(6000, "HDRSnap — hotkey", string.Join("\n", hkWarnings), ToolTipIcon.Warning);

        InstallHook();

        _rehook = new System.Windows.Forms.Timer { Interval = 60_000 };
        _rehook.Tick += (_, _) => ReinstallHook();
        _rehook.Start();
    }

    void InstallHook()
    {
        _ = Handle;             // force la creation du handle (la fenetre reste invisible)
        _hookProc ??= HookProc; // reference gardee, sinon le GC collecte le delegue
        _hook = Native.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
        {
            Program.Log($"ERR hook clavier impossible (err {Marshal.GetLastWin32Error()})");
            _tray.ShowBalloonTip(4000, "HDRSnap", "Impossible d'installer le hook clavier.", ToolTipIcon.Error);
        }
        else Program.Log($"hotkeys actives : {string.Join(", ", _hotkeys.Select(h => $"{h.Label} ({h.Mode})"))} (hook clavier bas niveau)");
    }

    void ReinstallHook()
    {
        // Installer AVANT de retirer : un échec passager de SetWindowsHookEx ne doit pas
        // détruire un hook qui fonctionnait. Les deux coexistent un instant, sans risque
        // de double capture (même délégué, et _heldVk bloque le second appel).
        IntPtr fresh = Native.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, IntPtr.Zero, 0);
        if (fresh == IntPtr.Zero)
        {
            if (!_rearmFailed)   // une ligne par panne, pas une par minute
            {
                _rearmFailed = true;
                Program.Log($"ERR réarmement hook clavier impossible (err {Marshal.GetLastWin32Error()}), ancien hook conservé");
            }
            return;
        }

        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = fresh;
        // Si le hook est tombé alors que la touche était enfoncée, le KEYUP s'est perdu
        // et _heldVk resterait positionné : la frappe suivante serait avalée sans capturer.
        _heldVk = 0;
        if (_rearmFailed)
        {
            _rearmFailed = false;
            Program.Log("réarmement hook clavier de nouveau OK");
        }
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            uint vk = (uint)Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode, 1er champ
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
                        // on rend la main tout de suite : Windows désactive un hook trop lent
                        BeginInvoke(new Action(() => Shoot(mode)));
                    }
                    return (IntPtr)1;   // touche avalée, le jeu ne la voit pas
                }
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// Table des hotkeys depuis la config. Hotkey invalide -> repli F11 ;
    /// WindowHotkey invalide ou identique à Hotkey -> capture fenêtre désactivée.
    internal static (HotkeyBinding[] table, List<string> warnings) BuildHotkeys(Config cfg)
    {
        var warnings = new List<string>();
        var list = new List<HotkeyBinding>();

        if (Config.TryParseHotkey(cfg.Hotkey, out uint mods, out uint vk, out string err))
            list.Add(new HotkeyBinding(mods, vk, cfg.Hotkey, CaptureMode.Monitor));
        else
        {
            warnings.Add($"hotkey « {cfg.Hotkey} » invalide ({err}) -> repli sur F11");
            list.Add(new HotkeyBinding(0, (uint)Keys.F11, "F11", CaptureMode.Monitor));
        }

        if (!string.IsNullOrWhiteSpace(cfg.WindowHotkey))
        {
            if (!Config.TryParseHotkey(cfg.WindowHotkey, out uint wm, out uint wv, out string werr))
                warnings.Add($"WindowHotkey « {cfg.WindowHotkey} » invalide ({werr}) -> capture fenêtre désactivée");
            else if (wm == list[0].Mods && wv == list[0].Vk)
                warnings.Add($"WindowHotkey identique à Hotkey ({cfg.WindowHotkey}) -> capture fenêtre désactivée");
            else
                list.Add(new HotkeyBinding(wm, wv, cfg.WindowHotkey, CaptureMode.Window));
        }
        return (list.ToArray(), warnings);
    }

    /// Bits des modificateurs enfoncés, mêmes valeurs que Config.TryParseHotkey (Alt=1, Ctrl=2, Shift=4, Win=8).
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

    /// Index de la hotkey dont la touche ET les modificateurs correspondent exactement, -1 sinon.
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
        // hors du thread UI pour ne pas bloquer la pompe de messages
        Task.Run(() =>
        {
            try
            {
                string path = Capturer.CaptureToFile(_cfg, mode, hwnd);
                _lastFile = path;
                Program.Log($"OK {path}");
                // Le son d'abord : retour immédiat, sans attendre le presse-papiers.
                if (_cfg.PlaySound) System.Media.SystemSounds.Asterisk.Play();
                bool copied = _cfg.CopyToClipboard && CopyToClipboard(path);
                string warn = Capturer.LastWarning;
                if (warn != null)
                    _tray.ShowBalloonTip(8000, "HDRSnap - capture douteuse", warn, ToolTipIcon.Warning);
                else
                    // « copié » seulement si ça l'est vraiment : sinon l'utilisateur collerait
                    // le contenu précédent du presse-papiers en croyant coller sa capture.
                    _tray.ShowBalloonTip(1500, "Screenshot",
                        Path.GetFileName(path) + (copied ? " — copié dans le presse-papiers" : ""), ToolTipIcon.Info);
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

    /// Presse-papiers : bitmap (DIB) + flux PNG, pour que Paint, Discord, navigateurs… collent tous.
    /// La lecture et le décodage se font sur le thread appelant (worker) : le thread UI fait
    /// aussi tourner HookProc, et Windows retire un hook clavier qui dépasse
    /// LowLevelHooksTimeout (~300 ms). Décoder un PNG 4K là-dessus tuerait le hotkey.
    /// Seul le dépôt final exige un thread STA, donc lui seul passe par Invoke.
    bool CopyToClipboard(string path)
    {
        try
        {
            byte[] png = File.ReadAllBytes(path);
            using var bmpStream = new MemoryStream(png);   // doit rester ouvert tant que bmp vit
            using var bmp = new Bitmap(bmpStream);
            using var pngStream = new MemoryStream(png);
            var data = new DataObject();
            data.SetImage(bmp);
            data.SetData("PNG", false, pngStream);
            // copy: true => OleFlushClipboard rend tous les formats avant de revenir,
            // donc disposer bmp/flux juste après est sans danger.
            Invoke(new Action(() => Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100)));
            return true;
        }
        catch (Exception ex)
        {
            Program.Log($"presse-papiers KO ({ex.Message})");   // non bloquant : le PNG est déjà sur disque
            return false;
        }
    }

    void ChooseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Dossier où enregistrer les captures",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_cfg.OutputDir) ? _cfg.OutputDir : "",
            // InitialDirectory n'est qu'un défaut, ignoré dès que le picker a mémorisé un
            // dernier dossier : c'est SelectedPath qui impose vraiment le point de départ.
            SelectedPath = Directory.Exists(_cfg.OutputDir) ? _cfg.OutputDir : "",
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        // Une capture déjà lancée lit OutputDir au moment de l'enregistrement : elle peut
        // donc atterrir dans l'ancien ou le nouveau dossier. Sans conséquence, le chemin
        // retenu est journalisé à chaque capture.
        _cfg.OutputDir = dlg.SelectedPath;          // pris en compte à la prochaine capture
        bool saved = _cfg.Save();
        Program.Log(saved
            ? $"dossier de sortie -> {_cfg.OutputDir}"
            : $"ERR config non enregistrée ({Config.LastSaveError}) ; dossier de sortie -> {_cfg.OutputDir} (valable jusqu'au redémarrage)");
        _tray.ShowBalloonTip(3000, "HDRSnap",
            saved ? $"Captures dans : {_cfg.OutputDir}" : "Dossier changé, mais config.json n'a pas pu être écrit.",
            saved ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rehook?.Dispose();
            if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
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
        if (Config.LoadWarning != null) Log("ATTENTION " + Config.LoadWarning);

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
            MessageBox.Show("Windows Graphics Capture non supporté sur ce système.", "HDRSnap");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(cfg));
    }
}
