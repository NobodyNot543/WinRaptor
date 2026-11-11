using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinPulse;

// ======================= Diagnóstico =======================

public enum Severity { Ok, Info, Warn, Bad }

public sealed record DiagResult(Severity Level, string Title, string Detail, string? Advice = null);

public static class Diagnostics
{
    public static List<DiagResult> Run()
    {
        var r = new List<DiagResult>();
        Safe(r, "Disco del sistema", () =>
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            double freePct = (double)d.AvailableFreeSpace / d.TotalSize * 100, freeGb = d.AvailableFreeSpace / 1073741824.0;
            return freePct < 8
                ? new(Severity.Bad, "Disco del sistema casi lleno", $"Solo quedan {freeGb:0.0} GB libres ({freePct:0} %).", "Usa «Limpiar temporales», vacía la papelera o desinstala programas. Windows necesita al menos un 10 % libre.")
                : freePct < 15
                    ? new(Severity.Warn, "Poco espacio en el disco del sistema", $"{freeGb:0} GB libres ({freePct:0} %).", "Conviene liberar espacio antes de la próxima actualización grande de Windows.")
                    : new(Severity.Ok, "Espacio en disco correcto", $"{freeGb:0} GB libres ({freePct:0} %).");
        });

        Safe(r, "Memoria", () =>
        {
            var i = SystemTools.SystemInfo();
            return i.RamPct >= 90
                ? new(Severity.Bad, "Memoria casi agotada", $"{i.Ram} en uso ({i.RamPct:0} %).", "Cierra programas, revisa los programas de inicio o amplía la RAM.")
                : i.RamPct >= 80
                    ? new(Severity.Warn, "Uso de memoria alto", $"{i.Ram} en uso ({i.RamPct:0} %).", "Revisa qué programas consumen más en el Administrador de tareas.")
                    : new(Severity.Ok, "Uso de memoria normal", $"{i.Ram} en uso ({i.RamPct:0} %).");
        });

        Safe(r, "Tiempo encendido", () =>
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            return up.TotalDays >= 7
                ? new(Severity.Warn, "Muchos días sin reiniciar", $"El equipo lleva {(int)up.TotalDays} días encendido.", "Reiniciar libera memoria y termina de instalar actualizaciones. Con el inicio rápido activo, «Apagar» no cuenta como reinicio.")
                : new(Severity.Ok, "Reiniciado recientemente", $"Encendido desde hace {(int)up.TotalDays} d {up.Hours} h.");
        });

        Safe(r, "Reinicio pendiente", () =>
        {
            using var lm = Reg.Base("HKLM");
            bool pending = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is not null
                        || lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null;
            return pending
                ? new(Severity.Warn, "Hay un reinicio pendiente", "Windows Update o una instalación necesitan reiniciar para terminar.", "Reinicia cuando puedas.")
                : new(Severity.Ok, "Sin reinicios pendientes", "No hay actualizaciones esperando a reiniciar.");
        });

        Safe(r, "Apagados inesperados", () =>
        {
            var (_, o) = Shell.PowerShell("try { @(Get-WinEvent -FilterHashtable @{LogName='System'; Id=41; StartTime=(Get-Date).AddDays(-30)} -ErrorAction Stop).Count } catch { 0 }");
            int n = int.TryParse(o.Trim().Split('\n').LastOrDefault()?.Trim(), out var c) ? c : 0;
            return n >= 3
                ? new(Severity.Bad, "Apagados o reinicios inesperados", $"{n} apagados bruscos en los últimos 30 días (evento Kernel-Power 41).", "Suele deberse a la fuente de alimentación, temperatura, overclock inestable o drivers. Revisa también las pantallas azules.")
                : n > 0
                    ? new(Severity.Warn, "Algún apagado inesperado", $"{n} en los últimos 30 días.", "Si no fue un corte de luz o un apagado forzado tuyo, vigílalo.")
                    : new(Severity.Ok, "Sin apagados inesperados", "Ninguno en los últimos 30 días.");
        });

        Safe(r, "Salud de los discos", () =>
        {
            var (_, o) = Shell.PowerShell("Get-PhysicalDisk | ForEach-Object { \"$($_.FriendlyName)|$($_.HealthStatus)|$($_.MediaType)\" }");
            var disks = o.Split('\n').Select(l => l.Trim().Split('|')).Where(p => p.Length == 3).ToList();
            if (disks.Count == 0) return new(Severity.Info, "Salud de los discos", "No se pudo leer el estado SMART.");
            var bad = disks.Where(p => !p[1].Equals("Healthy", StringComparison.OrdinalIgnoreCase)).ToList();
            var list = string.Join(" · ", disks.Select(p => $"{p[0]} ({p[2]}): {(p[1] == "Healthy" ? "bien" : p[1])}"));
            return bad.Count > 0
                ? new(Severity.Bad, "Un disco informa de problemas", list, "Haz copia de seguridad cuanto antes y comprueba el disco con la herramienta del fabricante.")
                : new(Severity.Ok, "Discos en buen estado", list);
        });

        Safe(r, "Controladores de red", () =>
        {
            var (_, o) = Shell.PowerShell(
                "Get-CimInstance Win32_PnPSignedDriver -Filter \"DeviceClass='NET'\" | Where-Object { $_.DriverDate -and $_.DeviceName -notmatch 'WAN Miniport|Virtual|Bluetooth|Microsoft|Hyper-V|TAP|Loopback' } | " +
                "ForEach-Object { \"$($_.DeviceName)|$($_.DriverDate.ToString('yyyy-MM-dd'))\" }");
            var nics = o.Split('\n').Select(l => l.Trim().Split('|')).Where(p => p.Length == 2 && DateTime.TryParse(p[1], out _))
                        .Select(p => (name: p[0], date: DateTime.Parse(p[1]))).ToList();
            if (nics.Count == 0) return new(Severity.Info, "Controladores de red", "No se encontraron adaptadores físicos.");
            var old = nics.Where(n => n.date < DateTime.Now.AddYears(-3)).ToList();
            return old.Count > 0
                ? new(Severity.Warn, "Controlador de red muy antiguo", string.Join(" · ", old.Select(n => $"{n.name}: {n.date:yyyy}")), "Descarga el driver más reciente de la web del fabricante de la placa o del adaptador.")
                : new(Severity.Ok, "Controladores de red al día", string.Join(" · ", nics.Select(n => $"{n.name}: {n.date:yyyy}")));
        });

        Safe(r, "Programas de inicio", () =>
        {
            int n = Startup.List().Count(e => e.Enabled);
            return n > 12
                ? new(Severity.Warn, "Muchos programas al iniciar", $"{n} programas arrancan con Windows.", "Desactiva los que no necesites en «Programas de inicio».")
                : new(Severity.Ok, "Programas de inicio razonables", $"{n} programas arrancan con Windows.");
        });

        Safe(r, "Archivos temporales", () =>
        {
            long size = 0;
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var d in new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }.Where(Directory.Exists))
                foreach (var f in Directory.EnumerateFiles(d, "*", opts)) { try { size += new FileInfo(f).Length; } catch { } }
            double mb = size / 1048576.0;
            return mb > 2048
                ? new(Severity.Warn, "Muchos archivos temporales", $"{mb / 1024:0.0} GB en carpetas temporales.", "Usa «Limpiar temporales» en Herramientas.")
                : new(Severity.Ok, "Temporales bajo control", $"{mb:0} MB en carpetas temporales.");
        });

        Safe(r, "Plan de energía", () =>
        {
            var (_, o) = Shell.Run("powercfg /getactivescheme");
            var m = Regex.Match(o, @"\((.+)\)");
            var name = m.Success ? m.Groups[1].Value : o.Trim();
            bool saver = o.Contains("a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.OrdinalIgnoreCase);
            return saver
                ? new(Severity.Warn, "Plan «Economizador» activo", name, "Limita mucho la CPU. Cambia a Equilibrado o Alto rendimiento en «CPU y energía».")
                : new(Severity.Ok, "Plan de energía activo", name);
        });

        Safe(r, "Firewall", () =>
        {
            var (_, o) = Shell.PowerShell("(Get-NetFirewallProfile | Where-Object { -not $_.Enabled }).Name -join ', '");
            var off = o.Trim();
            return off.Length > 0
                ? new(Severity.Bad, "Firewall desactivado", $"Perfiles sin firewall: {off}.", "Actívalo con «Asegurar el firewall activo» en Seguridad.")
                : new(Severity.Ok, "Firewall activo", "Todos los perfiles de red están protegidos.");
        });

        return r.OrderByDescending(x => x.Level).ToList();
    }

    static void Safe(List<DiagResult> r, string what, Func<DiagResult> f)
    {
        try { r.Add(f()); }
        catch (Exception ex) { r.Add(new(Severity.Info, what, "No se pudo comprobar: " + ex.Message)); }
    }
}

// ======================= Programas de inicio =======================

public sealed class StartupEntry : Observable
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Location { get; init; } = "";
    /// <summary>Rama y subclave de StartupApproved que controla esta entrada.</summary>
    public string ApprovedHive { get; init; } = "";
    public string ApprovedKey { get; init; } = "";
    bool _enabled;
    public bool Enabled { get => _enabled; set { _enabled = value; On(); } }
}

/// <summary>
/// Mismo mecanismo que el Administrador de tareas: no borra nada, solo marca la entrada
/// como habilitada o deshabilitada en Explorer\StartupApproved.
/// </summary>
public static class Startup
{
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupEntry> List()
    {
        var list = new List<StartupEntry>();
        void FromRun(string hive, string key, string approvedSub, string location)
        {
            using var b = Reg.Base(hive);
            using var k = b.OpenSubKey(key);
            if (k is null) return;
            foreach (var name in k.GetValueNames().Where(n => n.Length > 0))
                list.Add(Make(name, k.GetValue(name)?.ToString() ?? "", location, hive, approvedSub));
        }
        void FromFolder(string folder, string hive, string location)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                list.Add(Make(Path.GetFileName(f), f, location, hive, "StartupFolder"));
        }

        FromRun("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Usuario");
        FromRun("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", "Todos los usuarios");
        FromRun("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Todos los usuarios (32 bits)");
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "HKCU", "Carpeta Inicio");
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "HKLM", "Carpeta Inicio común");
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static StartupEntry Make(string name, string cmd, string location, string hive, string approvedSub)
    {
        using var b = Reg.Base(hive);
        using var k = b.OpenSubKey(Approved + approvedSub);
        // Byte 0 par (02, 06) = habilitado; impar (03, 07) = deshabilitado. Sin valor = habilitado.
        bool enabled = k?.GetValue(name) is not byte[] data || data.Length == 0 || data[0] % 2 == 0;
        return new StartupEntry
        {
            Name = name, Command = cmd, Location = location, ApprovedHive = hive,
            ApprovedKey = Approved + approvedSub, Enabled = enabled
        };
    }

    public static void SetEnabled(StartupEntry e, bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        using var b = Reg.Base(e.ApprovedHive);
        using var k = b.CreateSubKey(e.ApprovedKey, true)!;
        k.SetValue(e.Name, data, RegistryValueKind.Binary);
        Log.Write($"Inicio: {(enabled ? "habilitado" : "deshabilitado")} {e.Name}");
    }
}

// ======================= Actualizaciones con winget =======================

public sealed class UpgradeItem : Observable
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Current { get; init; } = "";
    public string Available { get; init; } = "";
    public string VersionText => $"{Current}  →  {Available}";
    bool _selected = true;
    public bool IsSelected { get => _selected; set { _selected = value; On(); } }
}

public static class Winget
{
    static readonly Regex SafeId = new(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]*$");

    public static List<UpgradeItem> ListUpgrades()
    {
        var (_, output) = Shell.PowerShell(
            "winget upgrade --include-unknown --accept-source-agreements --disable-interactivity | Out-String -Width 500", 300_000);
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l).ToList();
        int sep = lines.FindIndex(l => l.Trim().Length > 20 && l.Trim().All(c => c == '-'));
        if (sep < 1) return new();

        // Inicio de cada columna según la cabecera (Nombre, Id, Versión, Disponible, Origen).
        var header = lines[sep - 1];
        var starts = Regex.Matches(header, @"\S+").Select(m => m.Index).ToList();
        if (starts.Count < 4) return new();
        string Col(string row, int i)
        {
            if (starts[i] >= row.Length) return "";
            int end = i + 1 < starts.Count ? Math.Min(starts[i + 1], row.Length) : row.Length;
            return row[starts[i]..end].Trim();
        }

        var items = new List<UpgradeItem>();
        foreach (var row in lines.Skip(sep + 1))
        {
            if (row.Trim().Length == 0 || row.Length < starts[3]) break;
            var id = Col(row, 1);
            if (!SafeId.IsMatch(id)) continue;
            items.Add(new UpgradeItem { Name = Col(row, 0), Id = id, Current = Col(row, 2), Available = Col(row, 3) });
        }
        return items;
    }

    public static string Upgrade(UpgradeItem item)
    {
        if (!SafeId.IsMatch(item.Id)) return $"Identificador no válido: {item.Id}";
        var (code, output) = Shell.Run(
            $"winget upgrade --id \"{item.Id}\" --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity",
            1_800_000);
        var tail = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        return code == 0 ? $"Actualizado: {item.Name}" : $"No se pudo actualizar {item.Name} (código {code}). {tail}";
    }
}

// ======================= Perfiles propios =======================

public sealed class UserProfile
{
    public string Name { get; set; } = "";
    public List<string> Tweaks { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.Now;
    public string Summary => $"{Tweaks.Count} ajustes · creado el {Created:d}";
}

public static class Profiles
{
    static string FilePath => Path.Combine(TweakEngine.DataDir, "profiles.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static List<UserProfile> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<UserProfile>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public static void Save(List<UserProfile> list) => File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Opts));
}

// ======================= Historial de cambios =======================

/// <summary>Una foto del estado tras un cambio: qué ajustes aplicó WinRaptor y cómo estaban los programas de inicio.</summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime At { get; set; } = DateTime.Now;
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public List<string> Applied { get; set; } = new();
    public Dictionary<string, bool> Startup { get; set; } = new();
    /// <summary>Punto al que lleva «Deshacer último cambio» desde aquí (permite retroceder varias veces seguidas).</summary>
    public string? UndoTarget { get; set; }
}

public static class History
{
    const int MaxEntries = 200;
    static string FilePath => Path.Combine(TweakEngine.DataDir, "history.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static List<HistoryEntry> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public static void Save(List<HistoryEntry> list)
    {
        // Se conserva siempre el primer punto («Antes de usar WinRaptor»).
        if (list.Count > MaxEntries) list.RemoveRange(1, list.Count - MaxEntries);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Opts));
        File.Move(tmp, FilePath, true);
    }

    public static string StartupKey(StartupEntry e) => $"{e.ApprovedHive}|{e.ApprovedKey}|{e.Name}";

    public static Dictionary<string, bool> StartupSnapshot()
    {
        try
        {
            return Startup.List().GroupBy(StartupKey).ToDictionary(g => g.Key, g => g.First().Enabled);
        }
        catch { return new(); }
    }
}

// ======================= Accesos rápidos =======================

public static class Shortcuts
{
    public static readonly (string Glyph, string Title, string Desc, string Target)[] All =
    {
        ("", "Configuración de gráficos", "Preferencias de GPU por aplicación y HAGS.", "ms-settings:display-advancedgraphics"),
        ("", "Panel de control de NVIDIA", "Ajustes del driver de NVIDIA (si está instalado).", @"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel"),
        ("", "AMD Software", "Centro de control de Radeon (si está instalado).", @"shell:AppsFolder\AdvancedMicroDevicesInc-2.AMDRadeonSoftware_0a9w5gt2pkcy4!App"),
        ("", "Opciones de rendimiento", "Efectos visuales y memoria virtual de Windows.", "SystemPropertiesPerformance.exe"),
        ("", "Opciones de energía", "Planes de energía y configuración avanzada.", "powercfg.cpl"),
        ("", "Administrador de dispositivos", "Drivers y hardware del equipo.", "devmgmt.msc"),
        ("", "Monitor de recursos", "CPU, disco, red y memoria por proceso en detalle.", "resmon.exe"),
        ("", "Monitor de confiabilidad", "Historial de fallos y errores del sistema.", "perfmon.exe /rel"),
    };

    public static void Open(string target)
    {
        System.Diagnostics.ProcessStartInfo psi;
        if (target.StartsWith("ms-settings:") || target.StartsWith("shell:"))
            psi = new("explorer.exe", target);           // URIs y apps empaquetadas
        else
        {
            var parts = target.Split(' ', 2);            // "perfmon.exe /rel" → archivo + argumentos
            psi = new(parts[0], parts.Length > 1 ? parts[1] : "");
        }
        psi.UseShellExecute = true;
        System.Diagnostics.Process.Start(psi);
        Log.Write("Abierto: " + target);
    }
}
