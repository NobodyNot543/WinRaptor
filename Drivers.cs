using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace WinPulse;

/// <summary>Driver instalado de un fabricante (los de Microsoft se actualizan con Windows).</summary>
public sealed class DriverInfo
{
    public string Name { get; init; } = "";
    public string Class { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Date { get; init; }
    public string ClassLabel => Drivers.ClassName(Class);
    public bool IsOld => Date is { } d && d < DateTime.Now.AddYears(-3) && Drivers.Important(Class);
    public string DateText => Date is { } d ? d.ToString("dd/MM/yyyy") : "fecha desconocida";
    public string AgeText => Date is { } d ? Drivers.Age(d) : "";
    public Visibility OldVisibility => IsOld ? Visibility.Visible : Visibility.Collapsed;
    public Brush DateBrush => IsOld ? Palette.MediumFg : Palette.Idle;
    public string Glyph => Drivers.ClassGlyph(Class);
}

public sealed class DeviceProblem
{
    public string Name { get; init; } = "";
    public int Code { get; init; }
    public string Class { get; init; } = "";
    public string Explanation => Drivers.ProblemText(Code);
}

/// <summary>Driver nuevo ofrecido por Windows Update.</summary>
public sealed class DriverUpdate : Observable
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Class { get; init; } = "";
    public DateTime? Date { get; init; }
    public double SizeMb { get; init; }
    public string HardwareId { get; init; } = "";
    public bool Older { get; set; }
    public string InstalledText { get; set; } = "";
    public Visibility OlderVisibility => Older ? Visibility.Visible : Visibility.Collapsed;
    public string Meta => string.Join(" · ", new[] { Provider, Drivers.ClassName(Class), Date?.ToString("dd/MM/yyyy") ?? "", SizeMb > 0 ? $"{SizeMb:0.#} MB" : "" }.Where(s => s.Length > 0));
    public string Glyph => Drivers.ClassGlyph(Class);

    bool _sel = true;
    public bool IsSelected { get => _sel; set { if (_sel == value) return; _sel = value; On(); } }

    string _status = "";
    public string Status { get => _status; set { _status = value; On(); On(nameof(StatusVisibility)); } }
    public Visibility StatusVisibility => _status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    Brush _statusFg = Palette.Idle;
    public Brush StatusFg { get => _statusFg; set { _statusFg = value; On(); } }
}

public static class Drivers
{
    // Clases en las que un driver viejo sí suele notarse (rendimiento, red, sonido, estabilidad).
    static readonly HashSet<string> ImportantClasses = new(StringComparer.OrdinalIgnoreCase)
        { "DISPLAY", "NET", "MEDIA", "BLUETOOTH", "SYSTEM", "HDC", "SCSIADAPTER", "USB", "CAMERA" };

    public static bool Important(string cls) => ImportantClasses.Contains(cls);

    public static string ClassName(string cls) => cls.ToUpperInvariant() switch
    {
        "DISPLAY" => "Gráficos", "NET" => "Red", "MEDIA" => "Sonido", "BLUETOOTH" => "Bluetooth",
        "SYSTEM" => "Sistema / placa", "HDC" or "SCSIADAPTER" => "Almacenamiento", "USB" => "USB",
        "CAMERA" or "IMAGE" => "Cámara", "HIDCLASS" or "KEYBOARD" or "MOUSE" => "Entrada", "SOFTWARECOMPONENT" => "Componente",
        "SECURITYDEVICES" => "Seguridad", "VIDEO" => "Gráficos", "OTHERHARDWARE" => "Otros", "AUDIO" or "SOUND" => "Sonido", "NETWORK" => "Red", "PRINTER" => "Impresora", "BIOMETRIC" => "Biometría", "MONITOR" => "Monitor",
        "" => "Otros", _ => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cls.ToLowerInvariant())
    };

    public static string ClassGlyph(string cls) => cls.ToUpperInvariant() switch
    {
        "DISPLAY" or "MONITOR" or "VIDEO" => "", "NET" => "", "MEDIA" => "", "BLUETOOTH" => "",
        "HDC" or "SCSIADAPTER" => "", "USB" => "", "CAMERA" or "IMAGE" => "",
        "HIDCLASS" or "KEYBOARD" or "MOUSE" => "", "SECURITYDEVICES" => "", "PRINTER" => "",
        _ => ""
    };

    public static string Age(DateTime d)
    {
        var years = (DateTime.Now - d).TotalDays / 365.25;
        return years >= 1 ? $"hace {Math.Floor(years):0} {(years < 2 ? "año" : "años")}" : $"hace {Math.Max(1, (int)((DateTime.Now - d).TotalDays / 30))} meses";
    }

    public static string ProblemText(int code) => code switch
    {
        1 => "No está bien configurado.",
        3 => "El driver puede estar dañado o falta memoria.",
        10 => "El dispositivo no puede iniciarse.",
        12 => "No encuentra recursos libres (conflicto).",
        14 => "Necesita reiniciar el equipo.",
        18 or 24 => "Hay que reinstalar el driver.",
        19 => "La información de configuración en el registro está dañada.",
        21 or 47 => "Windows lo está quitando o lo ha preparado para quitar.",
        22 => "El dispositivo está desactivado.",
        28 => "No tiene driver instalado.",
        31 => "El driver no funciona bien con Windows.",
        39 => "El driver está dañado o falta.",
        43 => "Windows lo detuvo porque informó de problemas.",
        45 => "No está conectado ahora mismo.",
        52 => "La firma digital del driver no es válida.",
        _ => $"Error del dispositivo (código {code})."
    };

    /// <summary>Página oficial de drivers del fabricante, o una búsqueda si no se conoce.</summary>
    public static string VendorUrl(DriverInfo d)
    {
        var p = (d.Provider + " " + d.Name).ToLowerInvariant();
        if (p.Contains("nvidia")) return "https://www.nvidia.com/es-es/drivers/";
        if (p.Contains("amd") || p.Contains("advanced micro devices") || p.Contains("radeon")) return "https://www.amd.com/es/support/download/drivers.html";
        if (p.Contains("intel")) return "https://www.intel.la/content/www/xl/es/support/detect.html";
        if (p.Contains("lenovo")) return "https://pcsupport.lenovo.com/";
        if (p.Contains("realtek")) return "https://www.realtek.com/Download/";
        return "https://www.bing.com/search?q=" + Uri.EscapeDataString($"{d.Name} driver descarga oficial");
    }

    // ---------- Inventario ----------

    public static (List<DriverInfo> Third, int Total, List<DeviceProblem> Problems) Inventory()
    {
        const string script =
            "$d = Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceName -and $_.DriverVersion } | ForEach-Object { [pscustomobject]@{ " +
            "n=$_.DeviceName; c=[string]$_.DeviceClass; p=[string]$_.DriverProviderName; v=$_.DriverVersion; " +
            "d=$(if($_.DriverDate){$_.DriverDate.ToString('yyyy-MM-dd')}else{''}); h=[string]$_.HardWareID } };" +
            "$p = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.ConfigManagerErrorCode -ne 0 -and $_.ConfigManagerErrorCode -ne 45 } | ForEach-Object { [pscustomobject]@{ n=[string]$_.Name; e=[int]$_.ConfigManagerErrorCode; c=[string]$_.PNPClass } });" +
            "@{ d=@($d); p=$p } | ConvertTo-Json -Depth 3 -Compress";
        var (_, outp) = Shell.PowerShell(script, 180_000);
        int start = outp.IndexOf('{');
        if (start < 0) throw new Exception("No se pudo leer la lista de drivers.");
        using var doc = JsonDocument.Parse(outp[start..]);
        var all = new List<DriverInfo>();
        var installed = new Dictionary<string, (DateTime? Date, string Version, string Name)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.RootElement.GetProperty("d").EnumerateArray())
        {
            DateTime? date = DateTime.TryParseExact(e.GetProperty("d").GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
            var hw = e.TryGetProperty("h", out var hp) ? hp.GetString() ?? "" : "";
            if (hw.Length > 0) installed[hw] = (date, e.GetProperty("v").GetString() ?? "", e.GetProperty("n").GetString() ?? "");
            all.Add(new DriverInfo
            {
                Name = e.GetProperty("n").GetString() ?? "", Class = e.GetProperty("c").GetString() ?? "",
                Provider = e.GetProperty("p").GetString() ?? "", Version = e.GetProperty("v").GetString() ?? "", Date = date
            });
        }
        Installed = installed;
        var third = all.Where(d => d.Provider.Length > 0 && !d.Provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
                       .GroupBy(d => d.Name + "|" + d.Version).Select(g => g.First())
                       .OrderByDescending(d => d.IsOld).ThenBy(d => d.ClassLabel).ThenBy(d => d.Name).ToList();
        var problems = doc.RootElement.GetProperty("p").EnumerateArray().Select(e => new DeviceProblem
        {
            Name = e.GetProperty("n").GetString() ?? "Dispositivo desconocido", Code = e.GetProperty("e").GetInt32(), Class = e.GetProperty("c").GetString() ?? ""
        }).ToList();
        return (third, all.Count, problems);
    }

    // ---------- Windows Update ----------

    const string Criteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";

    /// <summary>Drivers nuevos que ofrece Windows Update para este equipo (oficiales y firmados).</summary>
    public static List<DriverUpdate> SearchUpdates()
    {
        string script =
            "try { $s = New-Object -ComObject Microsoft.Update.Session; $s.ClientApplicationID = 'WinRaptor';" +
            $"$r = $s.CreateUpdateSearcher().Search(\"{Criteria}\");" +
            "$o = @(foreach ($u in $r.Updates) { [pscustomobject]@{ id=$u.Identity.UpdateID; t=$u.Title; p=[string]$u.DriverProvider; c=[string]$u.DriverClass; " +
            "d=$(if($u.DriverVerDate){$u.DriverVerDate.ToString('yyyy-MM-dd')}else{''}); s=[math]::Round($u.MaxDownloadSize/1MB,1); h=[string]$u.DriverHardwareID } });" +
            "ConvertTo-Json -InputObject $o -Compress } catch { 'ERROR|' + $_.Exception.HResult + '|' + $_.Exception.Message }";
        var (_, outp) = Shell.PowerShell(script, 900_000);
        var txt = outp.Trim();
        if (txt.StartsWith("ERROR|")) throw new Exception(WuError(txt));
        if (txt == "tiempo de espera agotado") throw new Exception("Windows Update no respondió en 15 minutos. Inténtalo más tarde.");
        int start = txt.IndexOf('[');
        if (start < 0) return new();
        using var doc = JsonDocument.Parse(txt[start..]);
        var list = doc.RootElement.EnumerateArray().Select(e => new DriverUpdate
        {
            HardwareId = e.TryGetProperty("h", out var hp) ? hp.GetString() ?? "" : "",
            Id = e.GetProperty("id").GetString() ?? "", Title = e.GetProperty("t").GetString() ?? "",
            Provider = e.GetProperty("p").GetString() ?? "", Class = e.GetProperty("c").GetString() ?? "",
            Date = DateTime.TryParseExact(e.GetProperty("d").GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null,
            SizeMb = e.GetProperty("s").GetDouble()
        }).Where(u => u.Id.Length > 0).ToList();

        // Windows Update a veces ofrece drivers MÁS VIEJOS que el instalado (p. ej. el de gráficos del fabricante del portátil).
        foreach (var u in list)
            if (u.HardwareId.Length > 0 && Installed.TryGetValue(u.HardwareId, out var cur) && cur.Date is { } cd && u.Date is { } ud && ud < cd)
            {
                u.Older = true;
                u.InstalledText = $"Es más antiguo que el que tienes (v{cur.Version}, del {cd:dd/MM/yyyy}). No se recomienda instalarlo.";
                u.IsSelected = false;
            }
        return list.OrderBy(u => u.Older).ThenByDescending(u => u.Date).ToList();
    }

    /// <summary>Drivers instalados por id de hardware (del último inventario), para comparar con lo que ofrece Windows Update.</summary>
    public static Dictionary<string, (DateTime? Date, string Version, string Name)> Installed { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    static string WuError(string line)
    {
        var p = line.Split('|', 3);
        var code = p.Length > 1 && int.TryParse(p[1], out var h) ? h : 0;
        return unchecked((uint)code) switch
        {
            0x80070422 => "El servicio Windows Update está desactivado. Actívalo (o deshaz el ajuste que lo desactiva) para buscar drivers.",
            0x8024402C or 0x80072EE7 or 0x80072EFD => "No hay conexión con Windows Update. Comprueba tu conexión a Internet.",
            _ => "Windows Update devolvió un error: " + (p.Length > 2 ? p[2] : line)
        };
    }

    public enum Step { Searching, Downloading, Installing, Done }

    /// <summary>Descarga e instala los drivers elegidos de uno en uno, avisando de cada paso.
    /// Devuelve los resultados por Id (true = instalado) y si Windows pide reiniciar.</summary>
    public static (Dictionary<string, bool> Results, bool Reboot) Install(IReadOnlyList<string> ids, Action<Step, string?> progress)
    {
        var results = new Dictionary<string, bool>();
        bool reboot = false;
        if (ids.Count == 0) return (results, false);
        var idList = string.Join(",", ids.Select(i => "'" + i.Replace("'", "") + "'"));
        var crit = string.Join(" or ", ids.Select(i => $"IsInstalled=0 and UpdateID='{i.Replace("'", "")}'")); // WUA solo admite OR en el nivel superior
        string script =
            "try { $s = New-Object -ComObject Microsoft.Update.Session; $s.ClientApplicationID = 'WinRaptor';" +
            $"$ids = @({idList}); 'STAGE|search';" +
            $"$r = $s.CreateUpdateSearcher().Search(\"{crit}\");" +
            "foreach ($u in $r.Updates) { $id = $u.Identity.UpdateID; if ($ids -notcontains $id) { continue };" +
            " try { if (-not $u.EulaAccepted) { $u.AcceptEula() };" +
            " $c = New-Object -ComObject Microsoft.Update.UpdateColl; [void]$c.Add($u);" +
            " 'DL|' + $id; $d = $s.CreateUpdateDownloader(); $d.Updates = $c; [void]$d.Download();" +
            " 'IN|' + $id; $i = $s.CreateUpdateInstaller(); $i.Updates = $c; $res = $i.Install();" +
            " if ($res.RebootRequired) { 'REBOOT|1' }; 'RES|' + $id + '|' + $res.GetUpdateResult(0).ResultCode" +
            " } catch { 'RES|' + $id + '|4|' + $_.Exception.Message } }" +
            "} catch { 'ERROR|' + $_.Exception.HResult + '|' + $_.Exception.Message }";
        string? error = null;
        Shell.PowerShellStream(script, line =>
        {
            var p = line.Split('|');
            switch (p[0])
            {
                case "STAGE": progress(Step.Searching, null); break;
                case "DL": progress(Step.Downloading, p.ElementAtOrDefault(1)); break;
                case "IN": progress(Step.Installing, p.ElementAtOrDefault(1)); break;
                case "REBOOT": reboot = true; break;
                case "RES":
                    // 2 = correcto, 3 = correcto con errores
                    bool ok = p.Length > 2 && (p[2] == "2" || p[2] == "3");
                    results[p[1]] = ok;
                    if (!ok) Log.Write($"Driver {p[1]}: no se instaló (código {p.ElementAtOrDefault(2)}) {p.ElementAtOrDefault(3)}");
                    progress(Step.Done, p[1]);
                    break;
                case "ERROR": error = WuError(line); break;
            }
        });
        if (error is not null) throw new Exception(error);
        foreach (var id in ids) results.TryAdd(id, false); // los que Windows Update ya no ofrecía
        return (results, reboot);
    }

    /// <summary>Vuelve a detectar el hardware (igual que «Buscar cambios de hardware» del Administrador de dispositivos).</summary>
    public static string Rescan()
    {
        var (code, outp) = Shell.Run("pnputil /scan-devices", 120_000);
        return code == 0 ? "Hardware detectado de nuevo. Si había dispositivos sin driver, Windows intentará instalarlos." : "No se pudo volver a detectar el hardware: " + outp.Trim();
    }
}
