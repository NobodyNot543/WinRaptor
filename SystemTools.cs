using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinPulse;

/// <summary>Acciones puntuales de mantenimiento (no son ajustes reversibles).</summary>
public static class SystemTools
{
    /// <summary>Fecha del punto de restauración más reciente, o null si no hay ninguno.</summary>
    public static DateTime? LastRestorePoint()
    {
        var (_, o) = Shell.PowerShell(
            "$r = Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Sort-Object SequenceNumber | Select-Object -Last 1; " +
            "if ($r) { $r.ConvertToDateTime($r.CreationTime).ToString('o') }", 60_000);
        return DateTime.TryParse(o.Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
    }

    /// <summary>Antes de aplicar ajustes: reutiliza un punto de menos de 24 h en vez de esperar minutos a VSS.</summary>
    public static string EnsureRecentRestorePoint()
    {
        var last = LastRestorePoint();
        if (last is { } d && DateTime.Now - d < TimeSpan.FromHours(24))
            return $"Ya hay un punto de restauración reciente ({d:dd/MM HH:mm}); no se crea otro.";
        return CreateRestorePoint();
    }

    public static string CreateRestorePoint()
    {
        // Windows solo permite un punto cada 24 h; se levanta el límite temporalmente.
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
        const string freq = "SystemRestorePointCreationFrequency";
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = lm.CreateSubKey(key, true);
        var old = k.GetValue(freq);
        k.SetValue(freq, 0, RegistryValueKind.DWord);
        try
        {
            var (code, output) = Shell.PowerShell(
                "Enable-ComputerRestore -Drive \"$env:SystemDrive\\\" -ErrorAction SilentlyContinue;" +
                "Checkpoint-Computer -Description 'WinRaptor' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop; 'OK'");
            if (code != 0 || !output.Contains("OK")) throw new Exception(output.Trim());
            return "Punto de restauración creado.";
        }
        finally
        {
            if (old is null) k.DeleteValue(freq, false);
            else k.SetValue(freq, old, RegistryValueKind.DWord);
        }
    }

    public static string CleanTemp()
    {
        long freed = 0; int deleted = 0, skipped = 0;
        var cutoff = DateTime.Now.AddHours(-24); // no tocar lo que esté en uso por instalaciones recientes
        var dirs = new[]
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.LastWriteTime > cutoff) { skipped++; continue; }
                    var len = fi.Length;
                    fi.Delete();
                    freed += len; deleted++;
                }
                catch { skipped++; }
            }
            // Carpetas vacías, de la más profunda a la menos.
            foreach (var d in Directory.EnumerateDirectories(dir, "*", opts).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
        }
        return $"Temporales: {deleted} archivos borrados, {freed / 1048576.0:0.0} MB liberados ({skipped} en uso u omitidos).";
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public static string EmptyRecycleBin()
    {
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x7); // sin confirmación, sin progreso, sin sonido
        return hr == 0 || hr == unchecked((int)0x8000FFFF) ? "Papelera vaciada." : $"No se pudo vaciar la papelera (0x{hr:X8}).";
    }

    public static string FlushDns() => Summarize("DNS", Shell.Run("ipconfig /flushdns"));

    public static string ResetNetwork()
    {
        var a = Shell.Run("netsh winsock reset");
        var b = Shell.Run("netsh int ip reset");
        return a.code == 0 && b.code == 0
            ? "Pila de red restablecida. Reinicia el equipo para completarlo."
            : "Restablecer red terminó con errores:\n" + a.output + b.output;
    }

    public static string RunSfc() => Summarize("SFC", Shell.Run("sfc /scannow", 3_600_000));

    public static string RunDism() =>
        Summarize("DISM", Shell.Run("DISM /Online /Cleanup-Image /RestoreHealth", 3_600_000));

    public static string OptimizeDrives() => Summarize("Optimizar unidades", Shell.PowerShell(
        "Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' } | " +
        "ForEach-Object { Optimize-Volume -DriveLetter $_.DriveLetter -ErrorAction SilentlyContinue; \"Unidad $($_.DriveLetter): optimizada\" }",
        3_600_000));

    public static string WingetUpgradeAll() => Summarize("winget", Shell.Run(
        "winget upgrade --all --silent --accept-source-agreements --accept-package-agreements --disable-interactivity",
        3_600_000));

    public static string RestartExplorer()
    {
        Shell.Run("taskkill /f /im explorer.exe");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        return "Explorador de Windows reiniciado.";
    }

    static string Summarize(string what, (int code, string output) r)
    {
        var lines = r.output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var tail = string.Join("\n", lines.TakeLast(6));
        return $"{what}: {(r.code == 0 ? "completado" : $"terminó con código {r.code}")}\n{tail}";
    }

    // ----- Información del equipo -----

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    public sealed record SysInfo(string Os, string Build, string Cpu, string Threads, string Ram, double RamPct,
                                 string Disk, double DiskPct, string Uptime);

    public static SysInfo SystemInfo()
    {
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var nt = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = nt?.GetValue("ProductName") as string ?? "Windows";
        var build = int.TryParse(nt?.GetValue("CurrentBuild") as string, out var b) ? b : 0;
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
        var buildText = $"Versión {nt?.GetValue("DisplayVersion")} · compilación {build}";

        using var cpuKey = lm.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var cpu = (cpuKey?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Desconocido";
        var threads = $"{Environment.ProcessorCount} hilos lógicos";

        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref mem);
        double totalGb = mem.TotalPhys / 1073741824.0, usedGb = (mem.TotalPhys - mem.AvailPhys) / 1073741824.0;
        var ram = $"{usedGb:0.0} / {totalGb:0} GB";

        var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        double freeGb = sys.AvailableFreeSpace / 1073741824.0, sizeGb = sys.TotalSize / 1073741824.0;
        var disk = $"{freeGb:0} GB libres de {sizeGb:0}";

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var uptime = $"{(int)up.TotalDays} d {up.Hours} h {up.Minutes} min";
        return new SysInfo(product, buildText, cpu, threads, ram, mem.MemoryLoad,
                           disk, sizeGb > 0 ? (sizeGb - freeGb) / sizeGb * 100 : 0, uptime);
    }
}
