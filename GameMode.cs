using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinPulse;

/// <summary>Programa abierto que se puede cerrar mientras juegas.</summary>
public sealed class GameAppVM : Observable
{
    public string Process { get; init; } = "";
    public string Name { get; init; } = "";
    public string Note { get; init; } = "";
    public int Count { get; init; }
    public double MemMb { get; init; }
    public bool Background { get; init; }
    public string Meta => $"{(Count > 1 ? $"{Count} procesos · " : "")}{MemMb:0} MB{(Note.Length > 0 ? " · " + Note : "")}";
    bool _sel;
    public bool IsSelected { get => _sel; set { if (_sel == value) return; _sel = value; On(); } }
}

/// <summary>Servicio que se puede pausar durante la partida.</summary>
public sealed class GameServiceVM : Observable
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Desc { get; init; } = "";
    public bool Running { get; init; }
    public string State => Running ? "En marcha" : "Ya detenido";
    public Brush StateFg => Running ? Palette.MediumFg : Palette.Idle;
    public bool CanChange => Running;
    bool _sel;
    public bool IsSelected { get => _sel; set { if (_sel == value) return; _sel = value; On(); } }
}

/// <summary>Lo que cambió el modo juego, para dejarlo todo como estaba al terminar (también tras un cierre inesperado).</summary>
public sealed class GameSession
{
    public bool Active { get; set; }
    public DateTime StartedAt { get; set; }
    public string? PrevPowerScheme { get; set; }
    public string? PowerSchemeName { get; set; }
    public List<string> StoppedServices { get; set; } = new();
    public bool ToastsChanged { get; set; }
    public int? PrevToast { get; set; }
    public List<string> ClosedPaths { get; set; } = new();
    public List<string> ClosedNames { get; set; } = new();
    public bool Reopen { get; set; }
    public bool Timer { get; set; }
    public double FreedMb { get; set; }

    static string FilePath => Path.Combine(TweakEngine.DataDir, "gamemode.json");

    public static GameSession? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<GameSession>(File.ReadAllText(FilePath)) : null; }
        catch { return null; }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    public static void Clear() { try { File.Delete(FilePath); } catch { } }
}

public sealed record GameOptions(
    List<GameAppVM> Close, List<GameServiceVM> Services, bool PowerPlan, bool FreeMemory, bool Silence, bool Timer, bool Reopen);

public static class GameMode
{
    // Apps en segundo plano conocidas: (proceso, nombre, ¿marcada por defecto?, nota)
    static readonly (string Proc, string Name, bool Def, string Note)[] Known =
    {
        ("OneDrive", "OneDrive", true, "sincroniza archivos en segundo plano"),
        ("ms-teams", "Microsoft Teams", true, ""), ("Teams", "Microsoft Teams (clásico)", true, ""),
        ("Skype", "Skype", true, ""), ("PhoneExperienceHost", "Vínculo móvil", true, ""), ("YourPhone", "Vínculo móvil", true, ""),
        ("Widgets", "Widgets", true, "noticias de la barra de tareas"), ("Copilot", "Copilot", true, ""),
        ("CCXProcess", "Adobe Creative Cloud", true, ""), ("Creative Cloud", "Adobe Creative Cloud", true, ""), ("AdobeIPCBroker", "Adobe (servicio)", true, ""),
        ("GoogleDriveFS", "Google Drive", true, "sincroniza archivos"), ("Dropbox", "Dropbox", true, "sincroniza archivos"),
        ("Microsoft.Photos", "Fotos", true, ""), ("Cortana", "Cortana", true, ""), ("olk", "Outlook", true, ""),
        ("Spotify", "Spotify", false, "por si escuchas música"), ("WhatsApp", "WhatsApp", false, ""), ("Telegram", "Telegram", false, ""),
        ("msedge", "Microsoft Edge", false, "se cerrarán sus pestañas"), ("chrome", "Google Chrome", false, "se cerrarán sus pestañas"),
        ("firefox", "Firefox", false, "se cerrarán sus pestañas"), ("brave", "Brave", false, "se cerrarán sus pestañas"), ("opera", "Opera", false, "se cerrarán sus pestañas"),
    };

    // Nunca se cierran: el sistema, WinRaptor, lanzadores de juegos, chat de voz, drivers y herramientas de juego.
    static readonly HashSet<string> NeverClose = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "WinRaptor", "dwm", "csrss", "winlogon", "System", "Idle", "svchost", "lsass", "services", "smss", "wininit", "fontdrvhost",
        "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
        "ApplicationFrameHost", "SystemSettings", "LockApp", "audiodg", "conhost", "powershell", "pwsh", "cmd", "WindowsTerminal", "OpenConsole",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "Agent", "RiotClientServices", "RiotClientUx",
        "RobloxPlayerBeta", "RobloxStudioBeta", "Minecraft.Windows", "javaw", "XboxPcApp", "GameBar", "GameBarFTServer", "gamingservices",
        "EADesktop", "EABackgroundService", "UbisoftConnect", "upc", "GalaxyClient", "Overwolf", "Discord", "nvcontainer", "NVIDIA Share",
        "RadeonSoftware", "AMDRSServ", "AMDRSSrcExt", "RtkUWP", "RtkAudUService64", "SecurityHealthSystray", "MsMpEng", "NisSrv",
        "Taskmgr", "mmc", "devenv", "Code", "node", "claude", "msiexec", "TrustedInstaller", "TiWorker", "MoUsoCoreWorker"
    };

    static readonly (string Name, string Label, string Desc, bool Def)[] Svcs =
    {
        ("WSearch", "Indexación de búsqueda", "Escanea el disco en segundo plano para la búsqueda de Windows.", true),
        ("wuauserv", "Windows Update", "Evita que descargue o instale actualizaciones a mitad de partida.", true),
        ("UsoSvc", "Orquestador de actualizaciones", "Lanza las comprobaciones de Windows Update.", true),
        ("DoSvc", "Optimización de distribución", "Comparte actualizaciones con otros equipos usando tu conexión.", true),
        ("BITS", "Transferencia en segundo plano", "Descargas de Windows y de algunas apps.", true),
        ("SysMain", "SysMain (Superfetch)", "Precarga programas en memoria y usa el disco.", true),
        ("DiagTrack", "Telemetría", "Envía datos de uso a Microsoft.", true),
        ("Spooler", "Cola de impresión", "Solo hace falta para imprimir.", true),
        ("WerSvc", "Informe de errores", "Recoge y envía informes de cuelgues.", true),
    };

    /// <summary>Apps abiertas que se pueden cerrar: las conocidas en segundo plano y las ventanas abiertas (estas sin marcar).</summary>
    public static List<GameAppVM> DetectApps()
    {
        var self = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var procs = System.Diagnostics.Process.GetProcesses().Where(p => { try { return p.SessionId == self; } catch { return false; } }).ToList();
        var result = new List<GameAppVM>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Known)
        {
            var list = procs.Where(p => p.ProcessName.Equals(k.Proc, StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count == 0 || !seen.Add(k.Name)) continue;
            seen.Add(k.Proc);
            result.Add(new GameAppVM { Process = k.Proc, Name = k.Name, Note = k.Note, Count = list.Count, MemMb = list.Sum(Mem), Background = true, IsSelected = k.Def });
        }
        foreach (var g in procs.Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0; } catch { return false; } })
                               .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            if (NeverClose.Contains(g.Key) || seen.Contains(g.Key)) continue;
            string name = g.Key;
            try { var d = g.First().MainModule?.FileVersionInfo.FileDescription; if (!string.IsNullOrWhiteSpace(d)) name = d!; } catch { }
            var all = procs.Where(p => p.ProcessName.Equals(g.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            result.Add(new GameAppVM { Process = g.Key, Name = name, Note = "ventana abierta", Count = all.Count, MemMb = all.Sum(Mem), IsSelected = false });
        }
        return result.OrderByDescending(a => a.IsSelected).ThenByDescending(a => a.MemMb).ToList();
    }

    static double Mem(System.Diagnostics.Process p) { try { return p.WorkingSet64 / 1048576.0; } catch { return 0; } }

    public static List<GameServiceVM> DetectServices()
    {
        var names = string.Join(",", Svcs.Select(s => $"'{s.Name}'"));
        var (_, outp) = Shell.PowerShell($"Get-Service -Name {names} -ErrorAction SilentlyContinue | ForEach-Object {{ $_.Name + '|' + $_.Status }}", 60_000);
        var running = outp.Split('\n').Select(l => l.Trim().Split('|')).Where(p => p.Length == 2 && p[1] == "Running").Select(p => p[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = outp.Split('\n').Select(l => l.Trim().Split('|')).Where(p => p.Length == 2).Select(p => p[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Svcs.Where(s => present.Contains(s.Name))
                   .Select(s => new GameServiceVM { Name = s.Name, Label = s.Label, Desc = s.Desc, Running = running.Contains(s.Name), IsSelected = s.Def && running.Contains(s.Name) })
                   .OrderByDescending(s => s.Running).ToList();
    }

    // ---------- Energía ----------

    static readonly Regex Guid = new(@"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s+\(([^)]*)\)(\s*\*)?", RegexOptions.IgnoreCase);

    /// <summary>Plan activo y el mejor plan de rendimiento disponible (Máximo rendimiento, si no Alto rendimiento).</summary>
    public static (string? Active, string? ActiveName, string? Best, string? BestName) PowerPlans()
    {
        var (_, outp) = Shell.Run("powercfg /list", 30_000);
        string? active = null, activeName = null, best = null, bestName = null;
        int bestRank = 0, activeRank = -1;
        foreach (Match m in Guid.Matches(outp))
        {
            var g = m.Groups[1].Value.ToLowerInvariant(); var name = m.Groups[2].Value.Trim();
            if (m.Groups[3].Success && m.Groups[3].Value.Contains('*')) { active = g; activeName = name; }
            var n = name.ToLowerInvariant();
            int rank = g == "e9a42b02-d5df-448d-aa00-03f14749eb61" || n.Contains("máximo") || n.Contains("maximo") || n.Contains("ultimate") ? 2
                     : g == "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" || n.Contains("alto rendimiento") || n.Contains("high performance") ? 1 : 0;
            if (rank > bestRank) { bestRank = rank; best = g; bestName = name; }
            if (m.Groups[3].Success && m.Groups[3].Value.Contains('*')) activeRank = rank;
        }
        // Si el plan activo ya es del nivel más alto (p. ej. hay dos «Máximo rendimiento»), se deja el tuyo.
        if (active is not null && activeRank >= bestRank && bestRank > 0) { best = active; bestName = activeName; }
        return (active, activeName, best, bestName);
    }

    // ---------- Memoria ----------

    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TokenPriv { public int Count; public Luid Luid; public int Attr; }
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string? sys, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPriv np, int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int cls, ref int info, int len);
    [DllImport("ntdll.dll")] static extern int NtSetTimerResolution(uint desired, bool set, out uint current);

    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out var tok)) return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return;
            var tp = new TokenPriv { Count = 1, Luid = luid, Attr = 2 };
            AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(tok); }
    }

    /// <summary>Vacía la memoria en espera (caché de archivos que Windows puede volver a leer). Devuelve MB liberados.</summary>
    public static double PurgeStandby()
    {
        var before = LiveStats.Ram();
        EnablePrivilege("SeProfileSingleProcessPrivilege");
        int cmd = 4; // MemoryPurgeStandbyList
        NtSetSystemInformation(80, ref cmd, sizeof(int));
        Thread.Sleep(400);
        var after = LiveStats.Ram();
        return Math.Max(0, (before.UsedGb - after.UsedGb) * 1024);
    }

    public static bool GlobalTimerEnabled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel");
            return k?.GetValue("GlobalTimerResolutionRequests") is int v && v == 1;
        }
        catch { return false; }
    }

    public static void SetTimer(bool on) { try { NtSetTimerResolution(5000, on, out _); } catch { } }

    // ---------- Activar / terminar ----------

    const string ToastKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";

    public static GameSession Start(GameOptions o, Action<string, bool> step)
    {
        var s = new GameSession { Active = true, StartedAt = DateTime.Now, Reopen = o.Reopen };
        s.Save(); // por si se cierra a medias, se puede restaurar igualmente

        // Apps
        foreach (var a in o.Close)
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(a.Process);
            string? path = null;
            foreach (var p in procs)
            {
                try { path ??= p.MainModule?.FileName; } catch { }
                try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
            }
            foreach (var p in procs)
            {
                try { if (!p.WaitForExit(2500)) p.Kill(); } catch { }
            }
            if (path is not null && a.Background) s.ClosedPaths.Add(path);
            s.ClosedNames.Add(a.Name);
            step("Cerrada: " + a.Name, true);
            s.Save();
        }

        // Servicios
        foreach (var sv in o.Services)
        {
            var (_, outp) = Shell.PowerShell($"Stop-Service -Name '{sv.Name}' -Force -ErrorAction SilentlyContinue; (Get-Service -Name '{sv.Name}').Status", 60_000);
            bool ok = outp.Contains("Stopped");
            if (ok) s.StoppedServices.Add(sv.Name);
            step("Pausado: " + sv.Label, ok);
            s.Save();
        }

        // Energía
        if (o.PowerPlan)
        {
            var (active, _, best, bestName) = PowerPlans();
            if (best is not null && best != active)
            {
                Shell.Run($"powercfg /setactive {best}");
                s.PrevPowerScheme = active;
                s.PowerSchemeName = bestName;
                step("Plan de energía: " + bestName, true);
            }
            else step("Plan de energía: ya estaba al máximo", true);
            s.Save();
        }

        // Notificaciones
        if (o.Silence)
        {
            using var k = Registry.CurrentUser.CreateSubKey(ToastKey, true);
            var prev = k.GetValue("ToastEnabled") as int?;
            if (prev != 0)
            {
                s.PrevToast = prev;
                s.ToastsChanged = true;
                k.SetValue("ToastEnabled", 0, RegistryValueKind.DWord);
            }
            step("Notificaciones silenciadas", true);
            s.Save();
        }

        // Temporizador
        if (o.Timer && GlobalTimerEnabled())
        {
            SetTimer(true);
            s.Timer = true;
            step("Temporizador de alta precisión (0,5 ms)", true);
        }

        // Memoria (al final, cuando ya se cerró todo)
        if (o.FreeMemory)
        {
            s.FreedMb = PurgeStandby();
            step($"Memoria en caché liberada ({s.FreedMb:0} MB)", true);
        }
        s.Save();
        return s;
    }

    public static void End(GameSession s, Action<string, bool> step)
    {
        if (s.Timer) { SetTimer(false); step("Temporizador normal", true); }
        if (s.PrevPowerScheme is not null) { Shell.Run($"powercfg /setactive {s.PrevPowerScheme}"); step("Plan de energía anterior", true); }
        if (s.ToastsChanged)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(ToastKey, true);
                if (s.PrevToast is int v) k.SetValue("ToastEnabled", v, RegistryValueKind.DWord); else k.DeleteValue("ToastEnabled", false);
            }
            catch { }
            step("Notificaciones activadas", true);
        }
        foreach (var name in s.StoppedServices)
        {
            var (_, outp) = Shell.PowerShell($"Start-Service -Name '{name}' -ErrorAction SilentlyContinue; (Get-Service -Name '{name}').Status", 60_000);
            step("Reanudado: " + name, outp.Contains("Running") || outp.Contains("StartPending"));
        }
        if (s.Reopen)
            foreach (var path in s.ClosedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // A través del Explorador, para que se abran como usuario normal y no como administrador.
                try { System.Diagnostics.Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false }); step("Abierta de nuevo: " + Path.GetFileNameWithoutExtension(path), true); }
                catch { step("No se pudo abrir: " + Path.GetFileNameWithoutExtension(path), false); }
            }
        GameSession.Clear();
    }
}
