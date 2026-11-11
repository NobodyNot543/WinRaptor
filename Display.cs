using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinPulse;

/// <summary>Un monitor conectado y lo que admite.</summary>
public sealed class MonitorInfo
{
    public string Device { get; init; } = "";          // \\.\DISPLAY1
    public string Name { get; init; } = "";            // nombre comercial
    public string Gpu { get; init; } = "";
    public bool Primary { get; init; }
    public bool Internal { get; init; }
    public string Connection { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int Hz { get; init; }
    public int NativeWidth { get; init; }
    public int NativeHeight { get; init; }
    public int MaxHz { get; init; }                    // máximo a la resolución actual
    public int Scale { get; init; }
    public bool HdrSupported { get; init; }
    public bool HdrOn { get; init; }

    public bool CanBoost => MaxHz > Hz + 1;
    public bool NonNative => NativeWidth > 0 && (Width != NativeWidth || Height != NativeHeight);
    public string Title => Primary ? $"{Name} · principal" : Name;
    public string Mode => $"{Width} × {Height} · {Hz} Hz";
    public string Details => string.Join(" · ", new[]
    {
        Internal ? "pantalla del portátil" : Connection, $"escalado {Scale} %",
        MaxHz > 0 ? $"admite hasta {MaxHz} Hz" : "", HdrSupported ? (HdrOn ? "HDR activado" : "admite HDR") : "", Gpu
    }.Where(s => s.Length > 0));
    public Visibility BoostVisibility => CanBoost ? Visibility.Visible : Visibility.Collapsed;
    public string BoostText => $"Usar {MaxHz} Hz";
    public Brush HzBrush => CanBoost ? Palette.MediumFg : Palette.BasicFg;
    public string Glyph => Internal ? "\uE7F8" : "\uE7F4";
}

public static class Display
{
    // ---------- Win32: modos de pantalla ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int modeNum, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const int ENUM_CURRENT_SETTINGS = -1, DM_DISPLAYFREQUENCY = 0x400000, DISPLAY_DEVICE_ATTACHED = 0x1, DISPLAY_DEVICE_PRIMARY = 0x4;

    // ---------- Win32: DisplayConfig (nombre del monitor, conexión, HDR) ----------

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] struct RATIONAL { public uint Num, Den; }
    [StructLayout(LayoutKind.Sequential)] struct PATH_TARGET
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling;
        public RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] struct PATH_INFO { public PATH_SOURCE source; public PATH_TARGET target; public uint flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] struct MODE_INFO { [FieldOffset(0)] public uint infoType; }
    [StructLayout(LayoutKind.Sequential)] struct HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SOURCE_NAME
    {
        public HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct TARGET_NAME
    {
        public HEADER header; public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }
    [StructLayout(LayoutKind.Sequential)] struct COLOR_INFO { public HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PATH_INFO[] paths, ref uint numMode, [Out] MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO info);

    static string Tech(uint t) => t switch
    {
        0 => "VGA", 4 => "DVI", 5 => "HDMI", 6 => "LVDS", 10 => "DisplayPort", 11 => "DisplayPort interno", 15 => "USB-C / Thunderbolt",
        0x80000000 => "interna", 16 => "Miracast", _ => "conexión " + t
    };

    /// <summary>Datos de DisplayConfig por nombre GDI (\\.\DISPLAYn).</summary>
    static Dictionary<string, (string Name, uint Tech, bool HdrSup, bool HdrOn)> ConfigInfo()
    {
        var map = new Dictionary<string, (string, uint, bool, bool)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(2, out var np, out var nm) != 0) return map;
            var paths = new PATH_INFO[np]; var modes = new MODE_INFO[nm];
            if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return map;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var src = new SOURCE_NAME { header = new HEADER { type = 1, size = (uint)Marshal.SizeOf<SOURCE_NAME>(), adapterId = p.source.adapterId, id = p.source.id } };
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
                var tgt = new TARGET_NAME { header = new HEADER { type = 2, size = (uint)Marshal.SizeOf<TARGET_NAME>(), adapterId = p.target.adapterId, id = p.target.id } };
                DisplayConfigGetDeviceInfo(ref tgt);
                var col = new COLOR_INFO { header = new HEADER { type = 9, size = (uint)Marshal.SizeOf<COLOR_INFO>(), adapterId = p.target.adapterId, id = p.target.id } };
                bool hdrSup = false, hdrOn = false;
                if (DisplayConfigGetDeviceInfo(ref col) == 0) { hdrSup = (col.value & 1) != 0; hdrOn = (col.value & 2) != 0; }
                map[src.viewGdiDeviceName] = (tgt.monitorFriendlyDeviceName ?? "", tgt.outputTechnology, hdrSup, hdrOn);
            }
        }
        catch { }
        return map;
    }

    /// <summary>Monitores activos con su modo actual, lo que admiten y cómo están conectados.</summary>
    public static List<MonitorInfo> Monitors()
    {
        var cfg = ConfigInfo();
        var list = new List<MonitorInfo>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED) == 0) continue;
            var dev = dd.DeviceName; var gpu = (dd.DeviceString ?? "").Trim(); bool primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY) != 0;
            var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(dev, ENUM_CURRENT_SETTINGS, ref cur)) continue;
            int nw = 0, nh = 0, maxHz = 0;
            var m = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            for (int k = 0; EnumDisplaySettings(dev, k, ref m); k++)
            {
                if ((long)m.dmPelsWidth * m.dmPelsHeight > (long)nw * nh) { nw = m.dmPelsWidth; nh = m.dmPelsHeight; }
                if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight && m.dmBitsPerPel == cur.dmBitsPerPel)
                    maxHz = Math.Max(maxHz, m.dmDisplayFrequency);
            }
            int scale = 100;
            try
            {
                var h = MonitorFromPoint(new POINT { X = cur.dmPositionX + 1, Y = cur.dmPositionY + 1 }, 2);
                if (GetDpiForMonitor(h, 0, out var dx, out _) == 0) scale = (int)Math.Round(dx / 96.0 * 100);
            }
            catch { }
            cfg.TryGetValue(dev, out var c);
            bool internalPanel = c.Tech == 0x80000000 || c.Tech == 11 || c.Tech == 6;
            string name = string.IsNullOrWhiteSpace(c.Name) ? (internalPanel ? "Pantalla integrada" : "Monitor") : c.Name;
            list.Add(new MonitorInfo
            {
                Device = dev, Name = name, Gpu = gpu, Primary = primary, Internal = internalPanel, Connection = c.Name is null ? "" : Tech(c.Tech),
                Width = cur.dmPelsWidth, Height = cur.dmPelsHeight, Hz = cur.dmDisplayFrequency, NativeWidth = nw, NativeHeight = nh,
                MaxHz = maxHz, Scale = scale, HdrSupported = c.HdrSup, HdrOn = c.HdrOn
            });
        }
        return list.OrderByDescending(x => x.Primary).ToList();
    }

    /// <summary>Cambia la frecuencia de un monitor (solo a modos que el driver dice admitir). Devuelve la anterior.</summary>
    public static (bool Ok, int Previous, string Message) SetRefresh(string device, int hz)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) return (false, 0, "No se pudo leer el modo actual.");
        int prev = dm.dmDisplayFrequency;
        dm.dmDisplayFrequency = hz;
        dm.dmFields = DM_DISPLAYFREQUENCY;
        if (ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 0x02 /*CDS_TEST*/, IntPtr.Zero) != 0) return (false, prev, $"El monitor no acepta {hz} Hz con esta resolución.");
        int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 0x01 /*CDS_UPDATEREGISTRY*/, IntPtr.Zero);
        return r == 0 ? (true, prev, $"Pantalla a {hz} Hz.") : (false, prev, $"No se pudo cambiar (código {r}).");
    }

    // ---------- Análisis ----------

    sealed record Gpu(string Name, string Driver, DateTime? Date);

    static List<Gpu> Gpus()
    {
        var (_, outp) = Shell.PowerShell(
            "ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_VideoController | ForEach-Object { [pscustomobject]@{ n=$_.Name; v=$_.DriverVersion; d=$(if($_.DriverDate){$_.DriverDate.ToString('yyyy-MM-dd')}else{''}) } })", 60_000);
        var list = new List<Gpu>();
        int s = outp.IndexOf('[');
        if (s < 0) return list;
        using var doc = JsonDocument.Parse(outp[s..]);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var n = e.GetProperty("n").GetString() ?? "";
            if (n.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || n.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || n.Contains("Parsec", StringComparison.OrdinalIgnoreCase)) continue;
            DateTime? d = DateTime.TryParse(e.GetProperty("d").GetString(), out var dt) ? dt : null;
            list.Add(new Gpu(n, e.GetProperty("v").GetString() ?? "", d));
        }
        return list;
    }

    static bool Integrated(string gpu) =>
        System.Text.RegularExpressions.Regex.IsMatch(gpu, @"(Intel\(R\) (UHD|HD|Iris)|Radeon\(TM\) (Vega|Graphics)|Radeon Graphics|Vega \d+|AMD Radeon\(TM\) Graphics|Radeon \d{3}M)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static string? DxGlobal()
    {
        try { return Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences")?.GetValue("DirectXUserGlobalSettings") as string; }
        catch { return null; }
    }

    public static async Task<AnalysisReport> Run(Action<string> progress)
    {
        var rep = new AnalysisReport { Title = "Pantalla y gráficos" };
        progress("Leyendo monitores, frecuencias y resolución…");
        var mons = await Task.Run(Monitors);
        progress("Revisando la tarjeta gráfica y su driver…");
        var gpus = await Task.Run(Gpus);
        rep.Monitors = mons;

        // --- Métricas ---
        rep.Metrics.Add(new("Monitores", mons.Count.ToString()));
        foreach (var (m, i) in mons.Select((m, i) => (m, i)))
            rep.Metrics.Add(new(m.Primary ? "Principal" : $"Monitor {i + 1}", $"{m.Width}×{m.Height} · {m.Hz} Hz", m.CanBoost || m.NonNative ? Severity.Warn : Severity.Ok));
        foreach (var g in gpus)
            rep.Metrics.Add(new(Integrated(g.Name) ? "Gráficos integrados" : "Tarjeta gráfica", g.Name, Severity.Info));
        bool hags = false;
        try { hags = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers")?.GetValue("HwSchMode") is int hs && hs == 2; } catch { }
        rep.Metrics.Add(new("GPU por hardware (HAGS)", hags ? "activada" : "desactivada", hags ? Severity.Ok : Severity.Info));

        // --- Frecuencia por debajo de lo que admite ---
        foreach (var m in mons.Where(m => m.CanBoost))
            rep.Findings.Add(new(Severity.Warn, $"{m.Name} va a {m.Hz} Hz pero admite {m.MaxHz} Hz",
                $"Estás desaprovechando tu monitor: a {m.MaxHz} Hz todo se ve más fluido (ratón, ventanas y juegos) y baja la latencia.",
                $"Pulsa «Usar {m.MaxHz} Hz» en la tarjeta del monitor, abajo. Si algo va mal, se revierte solo en 15 segundos."));

        // --- Resolución no nativa ---
        foreach (var m in mons.Where(m => m.NonNative))
            rep.Findings.Add(new(Severity.Warn, $"{m.Name} no usa su resolución nativa",
                $"Está a {m.Width}×{m.Height} y su resolución real es {m.NativeWidth}×{m.NativeHeight}: el texto y la imagen se ven borrosos.",
                "Configuración › Pantalla › Resolución de pantalla: elige la marcada como «recomendada». Para ganar FPS, baja la resolución dentro del juego, no la de Windows."));

        // --- Varios monitores ---
        if (mons.Count > 1)
        {
            var hzs = mons.Select(m => m.Hz).Distinct().OrderBy(h => h).ToList();
            var fastest = mons.OrderByDescending(m => m.MaxHz).ThenByDescending(m => m.Hz).First();
            if (hzs.Count > 1)
                rep.Findings.Add(new(Severity.Info, $"Tus monitores van a frecuencias distintas ({string.Join(" y ", hzs)} Hz)",
                    "Es normal, pero con un vídeo o un juego abierto en el monitor lento, el rápido puede notar pequeños tirones porque Windows sincroniza el escritorio con ambos.",
                    $"Juega en el de {fastest.Hz} Hz y, mientras juegas, evita vídeos en el otro (o ciérralos con el modo juego)."));
            if (!fastest.Primary && fastest.MaxHz > mons.First(m => m.Primary).MaxHz)
                rep.Findings.Add(new(Severity.Info, $"Tu monitor más rápido ({fastest.Name}) no es el principal",
                    "Muchos juegos se abren en el monitor principal y algunos limitan los FPS a su frecuencia.",
                    "Configuración › Pantalla: selecciona ese monitor y marca «Convertir en la pantalla principal»."));
            var inner = mons.FirstOrDefault(m => m.Internal);
            if (inner is not null && mons.Any(m => !m.Internal))
                rep.Findings.Add(new(Severity.Info, "Usas la pantalla del portátil y un monitor externo a la vez",
                    "Mover dos pantallas exige más a la gráfica (sobre todo si es integrada) y le resta FPS al juego.",
                    "Para jugar, pulsa Win + P y elige «Solo segunda pantalla»: la GPU solo dibuja el monitor externo."));
        }

        // --- HDR ---
        foreach (var m in mons.Where(m => m.HdrSupported && !m.HdrOn))
            rep.Findings.Add(new(Severity.Info, $"{m.Name} admite HDR y lo tienes apagado",
                "En juegos y vídeos compatibles, el HDR da más brillo y color.",
                "Configuración › Pantalla › Usar HDR. Si el escritorio se ve apagado o lavado, déjalo desactivado: muchos monitores baratos no lo hacen bien."));

        // --- Gráfica ---
        var real = gpus.Where(g => !Integrated(g.Name)).ToList();
        if (gpus.Count > 1 && real.Count > 0 && gpus.Any(g => Integrated(g.Name)))
            rep.Findings.Add(new(Severity.Info, "Tienes gráfica integrada y dedicada",
                $"Windows a veces abre juegos con la integrada, mucho más lenta que la {real[0].Name}.",
                "Configuración › Pantalla › Gráficos: añade tus juegos y elige «Alto rendimiento»."));
        if (gpus.Count > 0 && real.Count == 0)
            rep.Findings.Add(new(Severity.Info, $"Tu gráfica es integrada ({gpus[0].Name})",
                "Usa la RAM del sistema como memoria de vídeo, así que la velocidad y los canales de la RAM influyen mucho en los FPS.",
                "Con dos módulos de RAM iguales (doble canal) puede rendir hasta un 50 % más. En juegos, baja la resolución de render o usa FSR en vez de bajar todo al mínimo."));
        foreach (var g in gpus.Where(g => g.Date is { } d && d < DateTime.Now.AddYears(-2)))
            rep.Findings.Add(new(Severity.Warn, $"El driver de {g.Name} es de {g.Date:yyyy}",
                "Los drivers de gráficos nuevos traen mejoras de rendimiento y correcciones para juegos recientes.",
                "Ve a Drivers y pulsa «Web del fabricante» en tu gráfica, o descarga el último desde AMD, NVIDIA o Intel."));

        // --- Ajustes de Windows ---
        if (!hags)
            rep.Findings.Add(new(Severity.Info, "La programación de GPU acelerada por hardware está desactivada",
                "Reduce algo la latencia y la carga de la CPU en muchas gráficas modernas.",
                "Actívala más abajo en esta sección: «Programación de GPU acelerada por hardware» (requiere reiniciar)."));
        var dx = DxGlobal() ?? "";
        if (!dx.Contains("SwapEffectUpgradeEnable=1"))
            rep.Findings.Add(new(Severity.Info, "Optimizaciones para juegos en ventana desactivadas",
                "Los juegos en ventana o «sin bordes» van con menos latencia y sin tirones cuando están activadas.",
                "Actívala más abajo en esta sección: «Optimizaciones para juegos en ventana»."));
        if (mons.Any(m => m.MaxHz >= 100) && !dx.Contains("VRROptimizeEnable=1"))
            rep.Findings.Add(new(Severity.Info, "La frecuencia variable (VRR) no está activada en Windows",
                "Con un monitor de alta frecuencia, VRR (FreeSync / G-Sync) elimina el tearing sin el retraso de la sincronización vertical.",
                "Actívala más abajo en esta sección: «Frecuencia variable (VRR) en ventana», y también en el panel de tu gráfica."));

        foreach (var f in rep.Findings) rep.Worst(f.Level);
        foreach (var x in rep.Metrics) rep.Worst(x.Level);
        int warn = rep.Findings.Count(f => f.Level >= Severity.Warn);
        rep.Verdict = warn == 0
            ? $"Tus {(mons.Count == 1 ? "pantalla está bien configurada" : $"{mons.Count} pantallas están bien configuradas")}. Abajo tienes sugerencias opcionales para sacarles más."
            : $"Hay {warn} {(warn == 1 ? "cosa" : "cosas")} que mejorar en tus pantallas o tu gráfica. Empieza por las marcadas en ámbar.";
        return rep;
    }
}
