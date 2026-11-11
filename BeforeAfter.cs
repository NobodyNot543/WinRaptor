using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace WinPulse;

/// <summary>Medición del sistema en un momento dado, para comparar antes y después de los cambios.</summary>
public sealed class Snapshot
{
    public DateTime At { get; set; } = DateTime.Now;
    public string Label { get; set; } = "";
    public int Processes { get; set; }
    public int Services { get; set; }
    public double RamGb { get; set; }
    public int Startup { get; set; }
    public int Active { get; set; }
}

public static class Snapshots
{
    const int Max = 120;
    static string FilePath => Path.Combine(TweakEngine.DataDir, "snapshots.json");
    static readonly object Gate = new();

    public static List<Snapshot> Load()
    {
        lock (Gate)
        {
            try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Snapshot>>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch { return new(); }
        }
    }

    static void Save(List<Snapshot> list)
    {
        lock (Gate)
        {
            if (list.Count > Max) list.RemoveRange(1, list.Count - Max); // la primera se conserva siempre
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static Snapshot Measure(string label, int active)
    {
        var (_, used, _) = LiveStats.Ram();
        // Recuento independiente del idioma de Windows («sc query» traduce sus etiquetas).
        int services = 0;
        var (_, outp) = Shell.PowerShell("(Get-CimInstance Win32_Service -Filter \"State='Running'\" | Measure-Object).Count", 60_000);
        int.TryParse(outp.Trim().Split('\n').LastOrDefault()?.Trim(), out services);
        int startup = 0;
        try { startup = Startup.List().Count(e => e.Enabled); } catch { }
        return new Snapshot
        {
            Label = label, Processes = Process.GetProcesses().Length, Services = services,
            RamGb = Math.Round(used, 2), Startup = startup, Active = active
        };
    }

    /// <summary>Guarda una medición si la última es más antigua que <paramref name="minAge"/>.</summary>
    public static Snapshot? TakeIfOlder(TimeSpan minAge, string label, int active)
    {
        var list = Load();
        if (list.Count > 0 && DateTime.Now - list[^1].At < minAge) return null;
        var s = Measure(label, active);
        list.Add(s);
        Save(list);
        return s;
    }

    public static Snapshot Take(string label, int active)
    {
        var s = Measure(label, active);
        var list = Load();
        list.Add(s);
        Save(list);
        return s;
    }
}

public sealed record BootSample(DateTime At, double Seconds);

public static class BootTimes
{
    /// <summary>Duración de cada arranque registrada por Windows (evento 100 de Diagnostics-Performance).</summary>
    public static List<BootSample> Read()
    {
        const string script =
            "Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Diagnostics-Performance/Operational'; Id=100} -MaxEvents 80 -ErrorAction SilentlyContinue | " +
            "ForEach-Object { $x=[xml]$_.ToXml(); $b=($x.Event.EventData.Data | Where-Object Name -eq 'BootTime').'#text'; " +
            "$_.TimeCreated.ToUniversalTime().ToString('o') + '|' + $b }";
        var list = new List<BootSample>();
        try
        {
            var (_, outp) = Shell.PowerShell(script, 60_000);
            foreach (var line in outp.Split('\n'))
            {
                var p = line.Trim().Split('|');
                if (p.Length == 2 && DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                    && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0)
                    list.Add(new BootSample(at.ToLocalTime(), ms / 1000.0));
            }
        }
        catch { }
        return list.OrderBy(b => b.At).ToList();
    }
}
