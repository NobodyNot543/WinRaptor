using System.Text.Json;

namespace WinPulse;

/// <summary>Preferencias y estado pequeño de la app (perfil elegido, reinicio pendiente).</summary>
public sealed class AppSettings
{
    public string? LastProfile { get; set; }
    /// <summary>Momento en que quedó pendiente un reinicio. Se borra solo cuando Windows se reinicia después.</summary>
    public DateTime? RebootPendingSince { get; set; }
    public List<string> RebootReasons { get; set; } = new();

    static string FilePath => Path.Combine(TweakEngine.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var s = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
            // Si el equipo arrancó después de marcar el reinicio, ya está hecho.
            if (s.RebootPendingSince is { } since && LastBoot > since)
            {
                s.RebootPendingSince = null;
                s.RebootReasons.Clear();
                s.Save();
            }
            return s;
        }
        catch { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    public static DateTime LastBoot => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
}

public enum ImpactKind { None, Kernel, Services, System }

/// <summary>Clasifica lo que hace un ajuste para avisar de reinicios necesarios.</summary>
public static class Impact
{
    static readonly string[] KernelPaths =
    {
        @"system\currentcontrolset\control\session manager", @"system\currentcontrolset\control\prioritycontrol",
        @"system\currentcontrolset\control\graphicsdrivers", @"system\currentcontrolset\control\deviceguard",
        @"system\currentcontrolset\control\power", @"system\currentcontrolset\control\lsa",
        @"system\currentcontrolset\control\ci", @"system\currentcontrolset\control\kernel",
        @"system\currentcontrolset\services\tcpip", @"system\currentcontrolset\services\afd",
        @"system\currentcontrolset\services\ndis", @"system\currentcontrolset\services\mouclass",
        @"system\currentcontrolset\services\kbdclass", "memory management", "kernel"
    };
    static readonly string[] KernelCmds = { "bcdedit", "mmagent", "hypervisorlaunchtype", "disabledynamictick", "useplatformclock" };

    public static ImpactKind Of(Tweak t)
    {
        if (t.Restart != "reboot") return ImpactKind.None;
        foreach (var o in t.Apply)
        {
            var path = (o.Path ?? "").ToLowerInvariant();
            var cmd = (o.Cmd ?? "").ToLowerInvariant();
            if (KernelPaths.Any(path.Contains) || KernelCmds.Any(cmd.Contains)) return ImpactKind.Kernel;
        }
        if (t.Apply.Any(o => o.K == "svc") || t.Sec == "servicios") return ImpactKind.Services;
        return ImpactKind.System;
    }

    public static string Label(ImpactKind k) => k switch
    {
        ImpactKind.Kernel => "Núcleo de Windows",
        ImpactKind.Services => "Servicios",
        ImpactKind.System => "Sistema",
        _ => ""
    };
}
