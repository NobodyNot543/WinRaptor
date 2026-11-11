using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace WinPulse;

/// <summary>Últimos resultados de diagnósticos y análisis, guardados para que los consejos sobrevivan a cerrar la app.</summary>
public sealed class Insights
{
    public sealed class StoredReport
    {
        public DateTime At { get; set; }
        public string Title { get; set; } = "";
        public Severity Level { get; set; }
        public string Verdict { get; set; } = "";
        public List<DiagResult> Findings { get; set; } = new();
    }

    public DateTime? DiagAt { get; set; }
    public List<DiagResult> Diag { get; set; } = new();
    public Dictionary<string, StoredReport> Analyses { get; set; } = new();
    public DateTime? DriversAt { get; set; }
    public int DriverUpdates { get; set; }
    public int DriverProblems { get; set; }
    public int DriversOld { get; set; }
    public DateTime? UpgradesAt { get; set; }
    public int Upgrades { get; set; }
    public DateTime? RestorePointAt { get; set; }
    public DateTime? RestoreCheckedAt { get; set; }
    public DateTime? CheckedAt { get; set; }
    public DateTime? AppsAt { get; set; }
    public int AppsRecommended { get; set; }
    public List<string> Dismissed { get; set; } = new();

    static string FilePath => Path.Combine(TweakEngine.DataDir, "insights.json");

    public static Insights Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Insights>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    public void StoreAnalysis(string kind, AnalysisReport r)
    {
        Analyses[kind] = new StoredReport
        {
            At = DateTime.Now, Title = r.Title, Level = r.Level, Verdict = r.Verdict,
            Findings = r.Findings.Where(f => f.Level is Severity.Warn or Severity.Bad).ToList()
        };
        Save();
    }
}

/// <summary>Un consejo concreto, con su gravedad, de dónde sale y qué hacer.</summary>
public sealed class Tip
{
    public string Id { get; init; } = "";
    public Severity Level { get; init; }
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Advice { get; init; } = "";
    public string Source { get; init; } = "";
    public string ActionText { get; init; } = "";
    /// <summary>nav:sección · reboot · tool:clean · tool:restore</summary>
    public string Action { get; init; } = "";

    public string Glyph => Category switch
    {
        "reinicio" => "", "disco" => "", "memoria" => "", "inicio" => "",
        "ajustes" => "", "diagnostico" => "", "red" => "", "drivers" => "",
        "programas" => "", "seguridad" => "", "rendimiento" => "", "copia" => "", _ => ""
    };
    public Brush Fg => Level switch { Severity.Bad => Palette.HighFg, Severity.Warn => Palette.MediumFg, Severity.Ok => Palette.BasicFg, _ => Palette.Native };
    public Brush Bg => Level switch { Severity.Bad => Palette.HighBg, Severity.Warn => Palette.MediumBg, Severity.Ok => Palette.BasicBg, _ => Palette.NativeBg };
    public string LevelText => Level switch { Severity.Bad => "Importante", Severity.Warn => "Recomendado", Severity.Ok => "Bien", _ => "Sugerencia" };
    public Visibility AdviceVisibility => Advice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionVisibility => Action.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SourceVisibility => Source.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Hábito de mantenimiento y si se está cumpliendo.</summary>
public sealed class Habit
{
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";
    public bool? Ok { get; init; }
    public string Mark => Ok switch { true => "✓", false => "!", _ => "–" };
    public Brush Fg => Ok switch { true => Palette.BasicFg, false => Palette.MediumFg, _ => Palette.Idle };
    public Brush Bg => Ok switch { true => Palette.BasicBg, false => Palette.MediumBg, _ => Palette.IdleBg };
}

/// <summary>Estado actual del equipo que el asesor necesita (todo son lecturas rápidas, sin análisis).</summary>
public sealed record AdvisorInput(
    double Uptime, double DiskFreePct, double DiskFreeGb, double RamPct, int StartupEnabled,
    bool RebootPending, int RebootCount, string ProfileName, string ProfileKey, double ProfileCoverage,
    int NextLevelMissing, string NextLevelName, int ProfileMissing, int TotalTweaks, int ActiveTweaks);

public static class Advisor
{
    static string Ago(DateTime at)
    {
        var d = DateTime.Now - at;
        if (d.TotalMinutes < 2) return "ahora mismo";
        if (d.TotalHours < 1) return $"hace {(int)d.TotalMinutes} min";
        if (d.TotalDays < 1) return $"hace {(int)d.TotalHours} h";
        if (d.TotalDays < 2) return "ayer";
        return $"hace {(int)d.TotalDays} días";
    }

    static readonly Dictionary<string, (string Name, string Nav)> AnalysisInfo = new()
    {
        ["ping"] = ("Latencia de red", "analisis"), ["dns"] = ("DNS", "analisis"),
        ["stutter"] = ("Microtirones", "analisis"), ["slow"] = ("¿Por qué va lento?", "analisis"),
        ["display"] = ("Pantalla y gráficos", "analisis")
    };

    public static List<Tip> Build(AdvisorInput s, Insights ins)
    {
        var t = new List<Tip>();

        // --- Reinicio ---
        if (s.RebootPending)
            t.Add(new Tip { Id = "reboot", Level = Severity.Warn, Category = "reinicio", Title = $"Reinicia para completar {s.RebootCount} {(s.RebootCount == 1 ? "cambio" : "cambios")}",
                Detail = "Hay ajustes o drivers que Windows solo aplica al arrancar. Hasta entonces no notarás su efecto.",
                ActionText = "Ver cambios", Action = "reboot" });
        else if (s.Uptime >= 7)
            t.Add(new Tip { Id = "uptime", Level = s.Uptime >= 14 ? Severity.Warn : Severity.Info, Category = "reinicio",
                Title = $"Llevas {(int)s.Uptime} días sin reiniciar",
                Detail = "Con el tiempo se acumulan memoria ocupada, procesos colgados y actualizaciones a medio instalar.",
                Advice = "Reinicia (no «Apagar»: con el inicio rápido apagar no limpia la sesión). Una vez por semana es suficiente." });

        // --- Disco ---
        if (s.DiskFreePct < 10)
            t.Add(new Tip { Id = "disk", Level = Severity.Bad, Category = "disco", Title = $"El disco del sistema está casi lleno ({s.DiskFreeGb:0} GB libres)",
                Detail = $"Solo queda un {s.DiskFreePct:0} % libre. Por debajo del 10 % Windows se ralentiza y las actualizaciones pueden fallar.",
                Advice = "Limpia temporales y vacía la papelera; después desinstala juegos o programas que no uses.", ActionText = "Limpiar temporales", Action = "tool:clean" });
        else if (s.DiskFreePct < 20)
            t.Add(new Tip { Id = "disk", Level = Severity.Info, Category = "disco", Title = $"Queda poco espacio en el disco ({s.DiskFreePct:0} % libre)",
                Detail = "Conviene mantener al menos un 20 % libre para que el SSD rinda bien y haya sitio para actualizaciones.",
                ActionText = "Limpiar temporales", Action = "tool:clean" });

        // --- Memoria ---
        if (s.RamPct >= 85)
            t.Add(new Tip { Id = "ram", Level = Severity.Warn, Category = "memoria", Title = $"La memoria RAM va al {s.RamPct:0} %",
                Detail = "Cuando la RAM se llena Windows usa el disco como memoria y todo va a tirones.",
                Advice = "Cierra pestañas y programas que no uses. El análisis «¿Por qué va lento?» te dice qué la ocupa.",
                ActionText = "Analizar", Action = "analysis:slow" });

        // --- Programas de inicio ---
        if (s.StartupEnabled >= 8)
            t.Add(new Tip { Id = "startup", Level = s.StartupEnabled >= 12 ? Severity.Warn : Severity.Info, Category = "inicio",
                Title = $"{s.StartupEnabled} programas arrancan con Windows",
                Detail = "Cada uno retrasa el arranque y ocupa memoria aunque no lo uses.",
                Advice = "Desactiva los que no necesites nada más encender (chat, lanzadores de juegos, actualizadores).", ActionText = "Revisar", Action = "nav:arranque" });

        // --- Ajustes / perfil ---
        if (s.ProfileKey is "basic" or "medium" or "high" && s.ProfileMissing > 0)
            t.Add(new Tip { Id = "profile-missing", Level = Severity.Info, Category = "ajustes",
                Title = $"Faltan {s.ProfileMissing} ajustes de tu perfil {s.ProfileName}",
                Detail = $"Tienes aplicado el {s.ProfileCoverage:P0} del perfil. Completarlo da el resultado para el que está pensado.",
                ActionText = "Ver perfiles", Action = "nav:inicio" });
        if (s.NextLevelMissing > 0 && s.ProfileKey is "basic" or "none")
            t.Add(new Tip { Id = "profile-next", Level = Severity.Info, Category = "ajustes",
                Title = $"Puedes subir al perfil {s.NextLevelName}",
                Detail = $"{s.NextLevelMissing} ajustes más de rendimiento, red y energía. Todos se pueden deshacer.",
                ActionText = "Ver perfiles", Action = "nav:inicio" });

        // --- Diagnóstico ---
        if (ins.DiagAt is not { } dAt)
            t.Add(new Tip { Id = "diag-never", Level = Severity.Info, Category = "diagnostico", Title = "Aún no has hecho un diagnóstico",
                Detail = "Revisa en unos segundos el disco, la memoria, los errores del sistema, los drivers y la seguridad.",
                ActionText = "Ir a Diagnóstico", Action = "nav:diagnostico" });
        else
        {
            foreach (var d in ins.Diag.Where(d => d.Level is Severity.Warn or Severity.Bad))
                t.Add(new Tip { Id = "diag:" + d.Title, Level = d.Level, Category = "diagnostico", Title = d.Title, Detail = d.Detail,
                    Advice = d.Advice ?? "", Source = "Diagnóstico · " + Ago(dAt), ActionText = "Ver diagnóstico", Action = "nav:diagnostico" });
            if ((DateTime.Now - dAt).TotalDays > 14)
                t.Add(new Tip { Id = "diag-old", Level = Severity.Info, Category = "diagnostico", Title = "Repite el diagnóstico",
                    Detail = $"El último fue {Ago(dAt)}. Hazlo cada dos semanas para detectar problemas pronto.", ActionText = "Ir a Diagnóstico", Action = "nav:diagnostico" });
        }

        // --- Análisis ---
        foreach (var (kind, r) in ins.Analyses)
        {
            var (name, nav) = AnalysisInfo.TryGetValue(kind, out var v) ? v : (r.Title, "analisis");
            foreach (var f in r.Findings)
                t.Add(new Tip { Id = $"an:{kind}:{f.Title}", Level = f.Level, Category = kind is "ping" or "dns" ? "red" : "rendimiento",
                    Title = f.Title, Detail = f.Detail, Advice = f.Advice ?? "", Source = $"{name} · {Ago(r.At)}", ActionText = "Repetir análisis", Action = "analysis:" + kind });
        }
        if (!ins.Analyses.ContainsKey("stutter"))
            t.Add(new Tip { Id = "an-stutter", Level = Severity.Info, Category = "rendimiento", Title = "Comprueba si tu PC tiene microtirones",
                Detail = "En 15 segundos mide si Windows y los drivers responden a tiempo. Es la causa típica de tirones en juegos y audio.",
                ActionText = "Analizar", Action = "analysis:stutter" });
        if (!ins.Analyses.ContainsKey("display"))
            t.Add(new Tip { Id = "an-display", Level = Severity.Info, Category = "rendimiento", Title = "Revisa tus pantallas y tu gráfica",
                Detail = "Comprueba si tus monitores van a todos los Hz que admiten, si usan su resolución nativa y cómo aprovechar mejor la GPU.",
                ActionText = "Analizar", Action = "analysis:display" });
        if (!ins.Analyses.ContainsKey("ping"))
            t.Add(new Tip { Id = "an-ping", Level = Severity.Info, Category = "red", Title = "Mide la calidad de tu conexión",
                Detail = "Ping, estabilidad y si tu red se satura al descargar (bufferbloat). Útil si juegas online o haces videollamadas.",
                ActionText = "Analizar", Action = "analysis:ping" });

        // --- Drivers ---
        if (ins.DriversAt is not { } drAt || (DateTime.Now - drAt).TotalDays > 30)
            t.Add(new Tip { Id = "drv-check", Level = Severity.Info, Category = "drivers",
                Title = ins.DriversAt is null ? "Revisa tus drivers" : "Vuelve a revisar tus drivers",
                Detail = "Los drivers de gráficos, red y sonido influyen mucho en el rendimiento y la estabilidad.",
                ActionText = "Ir a Drivers", Action = "nav:drivers" });
        else
        {
            if (ins.DriverProblems > 0)
                t.Add(new Tip { Id = "drv-problems", Level = Severity.Bad, Category = "drivers", Title = $"{ins.DriverProblems} {(ins.DriverProblems == 1 ? "dispositivo tiene" : "dispositivos tienen")} problemas",
                    Detail = "Un dispositivo con error o sin driver puede dejar de funcionar o causar cuelgues.", Source = "Drivers · " + Ago(drAt),
                    ActionText = "Ver drivers", Action = "nav:drivers" });
            if (ins.DriverUpdates > 0)
                t.Add(new Tip { Id = "drv-updates", Level = Severity.Warn, Category = "drivers", Title = $"{ins.DriverUpdates} {(ins.DriverUpdates == 1 ? "driver nuevo disponible" : "drivers nuevos disponibles")}",
                    Detail = "Son oficiales de Windows Update. WinRaptor ya descarta los que serían más antiguos que los tuyos.", Source = "Drivers · " + Ago(drAt),
                    ActionText = "Instalar", Action = "nav:drivers" });
            if (ins.DriversOld > 0)
                t.Add(new Tip { Id = "drv-old", Level = Severity.Info, Category = "drivers", Title = $"{ins.DriversOld} drivers importantes tienen más de 3 años",
                    Detail = "Si Windows Update no los actualiza, la web del fabricante suele tener versiones más nuevas.", Source = "Drivers · " + Ago(drAt),
                    ActionText = "Ver drivers", Action = "nav:drivers" });
        }

        // --- Programas ---
        if (ins.UpgradesAt is { } upAt && ins.Upgrades > 0)
            t.Add(new Tip { Id = "upgrades", Level = ins.Upgrades >= 5 ? Severity.Warn : Severity.Info, Category = "programas",
                Title = $"{ins.Upgrades} programas tienen versión nueva", Detail = "Las versiones nuevas corrigen fallos de seguridad y de rendimiento.",
                Source = "Actualizar programas · " + Ago(upAt), ActionText = "Actualizar", Action = "nav:programas" });
        else if (ins.UpgradesAt is not { } u2 || (DateTime.Now - u2).TotalDays > 14)
            t.Add(new Tip { Id = "upgrades-check", Level = Severity.Info, Category = "programas", Title = "Comprueba si tus programas están al día",
                Detail = "WinRaptor busca versiones nuevas de todo lo instalado con winget.", ActionText = "Ir a Actualizar programas", Action = "nav:programas" });

        // --- Apps innecesarias ---
        if (ins.AppsAt is not null && ins.AppsRecommended > 0)
            t.Add(new Tip { Id = "apps", Level = Severity.Info, Category = "programas", Title = $"Puedes quitar {ins.AppsRecommended} apps innecesarias",
                Detail = "Apps de serie y de publicidad que no usas: ocupan espacio y algunas se actualizan o ejecutan en segundo plano.",
                ActionText = "Ver apps", Action = "nav:apps" });

        // --- Punto de restauración ---
        if (ins.RestoreCheckedAt is not null && (ins.RestorePointAt is not { } rp || (DateTime.Now - rp).TotalDays > 30))
            t.Add(new Tip { Id = "restore", Level = Severity.Info, Category = "copia",
                Title = ins.RestorePointAt is null ? "No tienes ningún punto de restauración" : "Tu último punto de restauración es antiguo",
                Detail = "Permite volver atrás si una actualización de Windows o un driver da problemas.",
                ActionText = "Crear ahora", Action = "tool:restore" });

        return t.Where(x => !ins.Dismissed.Contains(x.Id))
                .OrderByDescending(x => x.Level).ThenBy(x => x.Category).ToList();
    }

    /// <summary>Nota de 0 a 100: resta por cada problema según su gravedad.</summary>
    public static int Score(List<Tip> tips) =>
        Math.Clamp(100 - tips.Sum(x => x.Level switch { Severity.Bad => 15, Severity.Warn => 7, Severity.Info => 1, _ => 0 }), 0, 100);

    public static List<Habit> Habits(AdvisorInput s, Insights ins)
    {
        bool? Fresh(DateTime? at, int days) => at is null ? false : (DateTime.Now - at.Value).TotalDays <= days;
        string When(DateTime? at) => at is null ? "nunca" : Ago(at.Value);
        return new()
        {
            new() { Title = "Reiniciar al menos una vez por semana", Ok = s.Uptime < 7, Status = $"encendido desde hace {(s.Uptime < 1 ? "menos de un día" : $"{(int)s.Uptime} días")}" },
            new() { Title = "Mantener un 20 % del disco libre", Ok = s.DiskFreePct >= 20, Status = $"{s.DiskFreePct:0} % libre ({s.DiskFreeGb:0} GB)" },
            new() { Title = "Pocos programas al arrancar (menos de 8)", Ok = s.StartupEnabled < 8, Status = $"{s.StartupEnabled} activos" },
            new() { Title = "Diagnóstico cada dos semanas", Ok = Fresh(ins.DiagAt, 14), Status = "último: " + When(ins.DiagAt) },
            new() { Title = "Revisar drivers cada mes", Ok = Fresh(ins.DriversAt, 30), Status = "última revisión: " + When(ins.DriversAt) },
            new() { Title = "Programas actualizados", Ok = ins.UpgradesAt is null ? null : ins.Upgrades == 0 && Fresh(ins.UpgradesAt, 14) == true,
                    Status = ins.UpgradesAt is null ? "sin comprobar" : ins.Upgrades == 0 ? "todo al día · " + When(ins.UpgradesAt) : $"{ins.Upgrades} pendientes" },
            new() { Title = "Punto de restauración reciente", Ok = ins.RestoreCheckedAt is null ? null : Fresh(ins.RestorePointAt, 30),
                    Status = ins.RestoreCheckedAt is null ? "sin comprobar (pulsa «Revisar mi PC»)" : "último: " + When(ins.RestorePointAt) },
        };
    }
}
