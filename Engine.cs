using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinPulse;

// ---------- Modelo del catálogo ----------

/// <summary>Una operación atómica de un ajuste.</summary>
public sealed class Op
{
    /// <summary>reg | regdel | iface | svc | power | run</summary>
    public string K { get; set; } = "";
    public string? Hive { get; set; }
    public string? Path { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public JsonElement Value { get; set; }
    public string? Cmd { get; set; }
    public string? Mode { get; set; }
    public string? Sub { get; set; }
    public string? Setting { get; set; }
}

public sealed class Tweak
{
    public string Id { get; set; } = "";
    public string Sec { get; set; } = "";
    /// <summary>basic | medium | high</summary>
    public string Profile { get; set; } = "basic";
    public string Title { get; set; } = "";
    public string Desc { get; set; } = "";
    public List<string> Breaks { get; set; } = new();
    /// <summary>"" | session | reboot</summary>
    public string Restart { get; set; } = "";
    public List<Op> Apply { get; set; } = new();
    /// <summary>Operaciones extra al deshacer (además de restaurar las copias automáticas).</summary>
    public List<Op> Revert { get; set; } = new();

    public int Rank => Profile switch { "high" => 3, "medium" => 2, _ => 1 };
}

// ---------- Estado persistente ----------

public sealed class Backup
{
    /// <summary>reg | svc | power</summary>
    public string Kind { get; set; } = "";
    public string Hive { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public string? RegKind { get; set; }
    public string? Data { get; set; }
    public long Number { get; set; }
    public bool Delayed { get; set; }
}

public sealed class AppliedTweak
{
    public DateTime At { get; set; }
    public List<Backup> Backups { get; set; } = new();
}

public sealed class AppState
{
    public Dictionary<string, AppliedTweak> Applied { get; set; } = new();
}

// ---------- Motor ----------

public sealed class TweakEngine
{
    public static readonly string DataDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPulse");
    static readonly string StatePath = System.IO.Path.Combine(DataDir, "state.json");
    const string IfacesPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    static readonly HashSet<string> LiveCheckable = new() { "reg", "regdel", "iface", "svc", "power", "dx" };

    readonly object _lock = new();
    public AppState State { get; private set; } = new();
    public List<Tweak> Tweaks { get; }

    public TweakEngine()
    {
        Directory.CreateDirectory(DataDir);
        using var s = typeof(TweakEngine).Assembly.GetManifestResourceStream("WinPulse.tweaks.json")
                      ?? throw new InvalidOperationException("Falta el catálogo de ajustes.");
        Tweaks = JsonSerializer.Deserialize<List<Tweak>>(s, JsonOpts) ?? new();
        try
        {
            if (File.Exists(StatePath))
                State = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), JsonOpts) ?? new();
        }
        catch (Exception ex) { Log.Write("No se pudo leer el estado guardado: " + ex.Message); }
    }

    void SaveState()
    {
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(State, JsonOpts));
        File.Move(tmp, StatePath, true);
    }

    public bool HasBackup(Tweak t) { lock (_lock) return State.Applied.ContainsKey(t.Id); }

    /// <summary>Ajustes aplicados por WinRaptor, del más antiguo al más reciente.</summary>
    public List<string> AppliedIds()
    {
        lock (_lock) return State.Applied.OrderBy(a => a.Value.At).Select(a => a.Key).ToList();
    }

    /// <summary>False si el ajuste depende de algo que no existe en este equipo (p. ej. un servicio ausente).</summary>
    public bool IsAvailable(Tweak t)
    {
        foreach (var op in t.Apply)
        {
            if (op.K == "svc")
            {
                using var b = Reg.Base("HKLM");
                using var k = b.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + op.Name);
                if (k is null) return false;
            }
            else if (op.K == "power")
            {
                // Ajustes como la tapa o el brillo adaptativo no existen en todos los equipos.
                try { Power.GetAc(op.Sub!, op.Setting!); } catch { return false; }
            }
        }
        return true;
    }

    /// <summary>Quita un registro de estado sin copias que restaurar (restos de un intento fallido).</summary>
    public void ForgetIfEmpty(Tweak t)
    {
        lock (_lock)
        {
            if (State.Applied.TryGetValue(t.Id, out var rec) && rec.Backups.Count == 0 && t.Revert.Count == 0)
            {
                State.Applied.Remove(t.Id);
                SaveState();
            }
        }
    }

    // ----- Aplicar / deshacer -----

    public bool Apply(Tweak t)
    {
        lock (_lock)
        {
            var rec = State.Applied.TryGetValue(t.Id, out var existing) ? existing : new AppliedTweak { At = DateTime.Now };
            bool ok = true;
            int succeeded = 0;
            foreach (var op in t.Apply)
            {
                try { RunOp(op, rec.Backups); succeeded++; }
                catch (Exception ex) { ok = false; Log.Write($"  [{t.Id}] {op.K} falló: {ex.Message}"); }
            }
            if (succeeded == 0 && rec.Backups.Count == 0)
            {
                // No se cambió nada: no se marca como aplicado.
                Log.Write("No se pudo aplicar: " + t.Title);
                return false;
            }
            State.Applied[t.Id] = rec;
            SaveState();
            Log.Write((ok ? "Aplicado: " : "Aplicado con errores: ") + t.Title);
            return ok;
        }
    }

    public bool Revert(Tweak t)
    {
        lock (_lock)
        {
            bool ok = true;
            if (State.Applied.TryGetValue(t.Id, out var rec))
            {
                for (int i = rec.Backups.Count - 1; i >= 0; i--)
                {
                    try { Restore(rec.Backups[i]); }
                    catch (Exception ex) { ok = false; Log.Write($"  [{t.Id}] restaurar falló: {ex.Message}"); }
                }
            }
            foreach (var op in t.Revert)
            {
                try { RunOp(op, null); }
                catch (Exception ex) { ok = false; Log.Write($"  [{t.Id}] deshacer {op.K} falló: {ex.Message}"); }
            }
            State.Applied.Remove(t.Id);
            SaveState();
            Log.Write((ok ? "Deshecho: " : "Deshecho con errores: ") + t.Title);
            return ok;
        }
    }

    void RunOp(Op op, List<Backup>? backups)
    {
        switch (op.K)
        {
            case "reg":
                BackupReg(op.Hive!, op.Path!, op.Name!, backups);
                Reg.Set(op.Hive!, op.Path!, op.Name!, op.Type ?? "DWORD", op.Value);
                break;
            case "regdel":
                BackupReg(op.Hive!, op.Path!, op.Name!, backups);
                Reg.Delete(op.Hive!, op.Path!, op.Name!);
                break;
            case "iface":
                var paths = InterfacePaths();
                if (paths.Count == 0) throw new Exception("no hay adaptadores de red con IP");
                foreach (var p in paths)
                {
                    BackupReg("HKLM", p, op.Name!, backups);
                    Reg.Set("HKLM", p, op.Name!, op.Type ?? "DWORD", op.Value);
                }
                break;
            case "svc":
                BackupSvc(op.Name!, backups);
                Services.SetMode(op.Name!, op.Mode!);
                break;
            case "power":
                BackupPower(op.Sub!, op.Setting!, backups);
                Power.SetAc(op.Sub!, op.Setting!, op.Value.GetInt64());
                break;
            case "dx":
                BackupReg("HKCU", DirectX.Path, DirectX.Value, backups);
                DirectX.Set(op.Name!, op.Value.GetString()!);
                break;
            case "run":
                var (code, output) = Shell.Run(op.Cmd!);
                if (code != 0) throw new Exception($"código {code}: {output.Trim()}");
                break;
            default:
                throw new NotSupportedException("operación desconocida: " + op.K);
        }
    }

    static bool AlreadyBacked(List<Backup> b, string kind, string hive, string path, string name) =>
        b.Any(x => x.Kind == kind && x.Hive.Equals(hive, StringComparison.OrdinalIgnoreCase)
                   && x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                   && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static void BackupReg(string hive, string path, string name, List<Backup>? backups)
    {
        if (backups is null || AlreadyBacked(backups, "reg", hive, path, name)) return;
        backups.Add(Reg.Snapshot(hive, path, name));
    }

    static void BackupSvc(string name, List<Backup>? backups)
    {
        var (start, delayed) = Services.GetStart(name); // lanza si el servicio no existe
        if (backups is null || AlreadyBacked(backups, "svc", "", "", name)) return;
        backups.Add(new Backup { Kind = "svc", Name = name, Existed = true, Number = start, Delayed = delayed });
    }

    static void BackupPower(string sub, string setting, List<Backup>? backups)
    {
        var current = Power.GetAc(sub, setting);
        if (backups is null || AlreadyBacked(backups, "power", "", sub, setting)) return;
        backups.Add(new Backup { Kind = "power", Path = sub, Name = setting, Existed = true, Number = current });
    }

    static void Restore(Backup b)
    {
        switch (b.Kind)
        {
            case "reg": Reg.Restore(b); break;
            case "svc": Services.SetStart(b.Name, (int)b.Number, b.Delayed); break;
            case "power": Power.SetAc(b.Path, b.Name, b.Number); break;
        }
    }

    // ----- Comprobación de estado -----

    /// <summary>True si el ajuste está activo en el sistema ahora mismo.</summary>
    public bool IsApplied(Tweak t)
    {
        bool liveCheckable = t.Apply.Count > 0 && t.Apply.All(o => LiveCheckable.Contains(o.K));
        if (!liveCheckable) return HasBackup(t);
        try
        {
            foreach (var op in t.Apply)
            {
                bool match = op.K switch
                {
                    "reg" => Reg.Equals(op.Hive!, op.Path!, op.Name!, op.Type ?? "DWORD", op.Value),
                    "regdel" => !Reg.Exists(op.Hive!, op.Path!, op.Name!),
                    "iface" => InterfacePaths() is { Count: > 0 } ps
                               && ps.All(p => Reg.Equals("HKLM", p, op.Name!, op.Type ?? "DWORD", op.Value)),
                    "svc" => Services.GetStart(op.Name!).start == Services.ModeToStart(op.Mode!),
                    "power" => Power.GetAc(op.Sub!, op.Setting!) == op.Value.GetInt64(),
                    "dx" => DirectX.Get(op.Name!) == op.Value.GetString(),
                    _ => false
                };
                if (!match) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Claves de los adaptadores de red que tienen una IP asignada.</summary>
    static List<string> InterfacePaths()
    {
        var result = new List<string>();
        using var root = Reg.Base("HKLM").OpenSubKey(IfacesPath);
        if (root is null) return result;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k is null) continue;
            bool hasIp = k.GetValue("DhcpIPAddress") is string dhcp && dhcp.Length > 0 && dhcp != "0.0.0.0"
                         || k.GetValue("IPAddress") is string[] ips && ips.Any(i => i.Length > 0 && i != "0.0.0.0");
            if (hasIp) result.Add(IfacesPath + "\\" + id);
        }
        return result;
    }
}

// ---------- Registro de Windows ----------

static class Reg
{
    public static RegistryKey Base(string hive) => RegistryKey.OpenBaseKey(hive.ToUpperInvariant() switch
    {
        "HKCU" => RegistryHive.CurrentUser,
        "HKLM" => RegistryHive.LocalMachine,
        "HKCR" => RegistryHive.ClassesRoot,
        "HKU" => RegistryHive.Users,
        _ => throw new ArgumentException("rama desconocida: " + hive)
    }, RegistryView.Registry64);

    static int ToDword(JsonElement v) => unchecked((int)(uint)v.GetInt64());

    public static void Set(string hive, string path, string name, string type, JsonElement v)
    {
        using var b = Base(hive);
        using var k = b.CreateSubKey(path, true) ?? throw new Exception("no se pudo abrir " + path);
        switch (type.ToUpperInvariant())
        {
            case "DWORD": k.SetValue(name, ToDword(v), RegistryValueKind.DWord); break;
            case "QWORD": k.SetValue(name, v.GetInt64(), RegistryValueKind.QWord); break;
            case "SZ": k.SetValue(name, v.GetString() ?? "", RegistryValueKind.String); break;
            case "EXPAND_SZ": k.SetValue(name, v.GetString() ?? "", RegistryValueKind.ExpandString); break;
            default: throw new NotSupportedException("tipo de valor: " + type);
        }
    }

    public static void Delete(string hive, string path, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path, true);
        k?.DeleteValue(name, false);
    }

    public static bool Exists(string hive, string path, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path);
        return k?.GetValue(name) is not null;
    }

    public static bool Equals(string hive, string path, string name, string type, JsonElement v)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path);
        var cur = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return type.ToUpperInvariant() switch
        {
            "DWORD" => cur is int i && i == ToDword(v),
            "QWORD" => cur is long l && l == v.GetInt64(),
            _ => cur is string s && s == v.GetString()
        };
    }

    public static Backup Snapshot(string hive, string path, string name)
    {
        var bk = new Backup { Kind = "reg", Hive = hive, Path = path, Name = name };
        using var b = Base(hive);
        using var k = b.OpenSubKey(path);
        var cur = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (k is null || cur is null) return bk;
        var kind = k.GetValueKind(name);
        bk.Existed = true;
        bk.RegKind = kind.ToString();
        bk.Data = kind switch
        {
            RegistryValueKind.DWord => ((int)cur).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => ((long)cur).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.MultiString => string.Join("\0", (string[])cur),
            RegistryValueKind.Binary => Convert.ToBase64String((byte[])cur),
            _ => cur.ToString()
        };
        return bk;
    }

    public static void Restore(Backup bk)
    {
        if (!bk.Existed) { Delete(bk.Hive, bk.Path, bk.Name); return; }
        var kind = Enum.Parse<RegistryValueKind>(bk.RegKind!);
        object value = kind switch
        {
            RegistryValueKind.DWord => int.Parse(bk.Data!, CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => long.Parse(bk.Data!, CultureInfo.InvariantCulture),
            RegistryValueKind.MultiString => bk.Data!.Split('\0'),
            RegistryValueKind.Binary => Convert.FromBase64String(bk.Data!),
            _ => bk.Data ?? ""
        };
        using var b = Base(bk.Hive);
        using var k = b.CreateSubKey(bk.Path, true)!;
        k.SetValue(bk.Name, value, kind);
    }
}

// ---------- Preferencias globales de DirectX ----------

/// <summary>
/// DirectXUserGlobalSettings es una lista "Clave=valor;Clave2=valor;". Se modifica solo la clave pedida
/// y se conserva el resto (la copia de seguridad guarda la cadena completa original).
/// </summary>
static class DirectX
{
    public const string Path = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string Value = "DirectXUserGlobalSettings";

    static List<(string key, string val)> Read()
    {
        using var b = Reg.Base("HKCU");
        using var k = b.OpenSubKey(Path);
        var raw = k?.GetValue(Value) as string ?? "";
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(p => p.Split('=', 2))
                  .Select(p => (p[0], p.Length > 1 ? p[1] : ""))
                  .ToList();
    }

    public static string? Get(string key) =>
        Read().Where(p => p.key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(p => p.val).FirstOrDefault();

    public static void Set(string key, string val)
    {
        var list = Read().Where(p => !p.key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add((key, val));
        using var b = Reg.Base("HKCU");
        using var k = b.CreateSubKey(Path, true)!;
        k.SetValue(Value, string.Concat(list.Select(p => $"{p.key}={p.val};")), RegistryValueKind.String);
    }
}

// ---------- Servicios ----------

static class Services
{
    const string Root = @"SYSTEM\CurrentControlSet\Services\";

    public static int ModeToStart(string mode) => mode switch
    {
        "auto" => 2, "demand" => 3, "disabled" => 4,
        _ => throw new ArgumentException("modo de servicio: " + mode)
    };

    public static (int start, bool delayed) GetStart(string name)
    {
        using var b = Reg.Base("HKLM");
        using var k = b.OpenSubKey(Root + name) ?? throw new Exception($"el servicio {name} no existe en este equipo");
        int start = k.GetValue("Start") is int s ? s : 3;
        bool delayed = k.GetValue("DelayedAutostart") is int d && d == 1;
        return (start, delayed);
    }

    public static void SetMode(string name, string mode)
    {
        var (code, output) = Shell.Run($"sc config \"{name}\" start= {mode}");
        if (code != 0)
        {
            // Algunos servicios rechazan sc config; se escribe Start directamente (efectivo al reiniciar).
            using var b = Reg.Base("HKLM");
            using var k = b.OpenSubKey(Root + name, true) ?? throw new Exception(output.Trim());
            k.SetValue("Start", ModeToStart(mode), RegistryValueKind.DWord);
        }
        if (mode == "disabled") Shell.Run($"sc stop \"{name}\"");
    }

    public static void SetStart(string name, int start, bool delayed)
    {
        string? mode = start switch { 2 => delayed ? "delayed-auto" : "auto", 3 => "demand", 4 => "disabled", _ => null };
        if (mode is not null && Shell.Run($"sc config \"{name}\" start= {mode}").code == 0) return;
        using var b = Reg.Base("HKLM");
        using var k = b.OpenSubKey(Root + name, true) ?? throw new Exception($"el servicio {name} no existe");
        k.SetValue("Start", start, RegistryValueKind.DWord);
    }
}

// ---------- Plan de energía ----------

static class Power
{
    static readonly Regex Hex = new(@"0x([0-9a-fA-F]{8})");

    /// <summary>Lee el índice de corriente alterna del plan activo (independiente del idioma de Windows).</summary>
    public static long GetAc(string sub, string setting)
    {
        var (code, output) = Shell.Run($"powercfg /query SCHEME_CURRENT {sub} {setting}");
        var m = Hex.Matches(output);
        if (code != 0 || m.Count < 2) throw new Exception("powercfg no devolvió el valor de " + setting);
        // Las dos últimas cifras hex son el índice AC y el DC, en ese orden.
        return Convert.ToInt64(m[^2].Groups[1].Value, 16);
    }

    public static void SetAc(string sub, string setting, long value)
    {
        var (code, output) = Shell.Run($"powercfg /setacvalueindex SCHEME_CURRENT {sub} {setting} {value}");
        if (code != 0) throw new Exception(output.Trim());
        Shell.Run("powercfg /setactive SCHEME_CURRENT");
    }
}

// ---------- Procesos ----------

public static class Shell
{
    static Encoding Oem => Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

    public static (int code, string output) Run(string cmd, int timeoutMs = 300_000) =>
        Exec("cmd.exe", "/d /c " + cmd, timeoutMs, Oem);

    public static (int code, string output) PowerShell(string script, int timeoutMs = 600_000)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8;$ProgressPreference='SilentlyContinue';" + script));
        return Exec("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeoutMs, Encoding.UTF8);
    }

    /// <summary>Como <see cref="PowerShell"/>, pero entrega cada línea en cuanto se escribe (para mostrar progreso).</summary>
    public static int PowerShellStream(string script, Action<string> onLine, int timeoutMs = 3_600_000)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8;$ProgressPreference='SilentlyContinue';" + script));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi) ?? throw new Exception("no se pudo iniciar PowerShell");
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onLine(e.Data.Trim()); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onLine("ERR|" + e.Data.Trim()); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return -1;
        }
        p.WaitForExit(); // vacía los eventos pendientes
        return p.ExitCode;
    }

    static (int, string) Exec(string file, string args, int timeoutMs, Encoding enc)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = enc, StandardErrorEncoding = enc
        };
        using var p = Process.Start(psi) ?? throw new Exception("no se pudo iniciar " + file);
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return (-1, "tiempo de espera agotado");
        }
        // sfc/DISM escriben UTF-16: se quitan los nulos para que se lea.
        return (p.ExitCode, (so.Result + se.Result).Replace("\0", ""));
    }
}

// ---------- Registro de actividad ----------

public static class Log
{
    static readonly object Lock = new();
    public static event Action<string>? Written;
    public static string FilePath => System.IO.Path.Combine(TweakEngine.DataDir, "winpulse.log");

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (Lock)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}"); } catch { }
        }
        Written?.Invoke(line);
    }
}
