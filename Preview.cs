using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace WinPulse;

/// <summary>Un ajuste dentro de la vista previa: se puede quitar de la lista y ver qué toca exactamente.</summary>
public sealed class PreviewItemVM : Observable
{
    public TweakVM V { get; }
    public PreviewItemVM(TweakVM v) => V = v;
    public string Title => V.Title;
    public string Desc => V.Desc;
    public string RiskText => V.RiskText;
    public Brush RiskBg => V.RiskBg;
    public Brush RiskFg => V.RiskFg;
    public string RestartText => V.RestartText;
    public Visibility RestartVisibility => V.RestartVisibility;
    public string BreaksText => V.BreaksText;
    public Visibility BreaksVisibility => V.BreaksVisibility;
    public ImpactKind Impact => WinPulse.Impact.Of(V.T);
    public Visibility KernelVisibility => Impact == ImpactKind.Kernel ? Visibility.Visible : Visibility.Collapsed;

    bool _include = true;
    public bool Include { get => _include; set { if (_include == value) return; _include = value; On(); On(nameof(ItemOpacity)); } }
    public double ItemOpacity => _include ? 1 : 0.45;

    bool _expanded;
    public bool Expanded
    {
        get => _expanded;
        set { _expanded = value; if (value && _ops is null) _ops = Preview.Describe(V.T); On(); On(nameof(Ops)); On(nameof(DetailsVisibility)); On(nameof(DetailsLabel)); }
    }
    List<string>? _ops;
    public List<string> Ops => _ops ?? new();
    public Visibility DetailsVisibility => _expanded ? Visibility.Visible : Visibility.Collapsed;
    public string DetailsLabel => _expanded ? "Ocultar detalles técnicos" : $"Ver qué cambia exactamente ({V.T.Apply.Count} {(V.T.Apply.Count == 1 ? "cambio" : "cambios")})";
}

public sealed class PreviewGroupVM
{
    public string Name { get; init; } = "";
    public string Glyph { get; init; } = "";
    public List<PreviewItemVM> Items { get; init; } = new();
    public string CountText => $"{Items.Count} {(Items.Count == 1 ? "ajuste" : "ajustes")}";
}

public enum PreviewResult { Cancel, Mark, Apply }

public static class Preview
{
    static string Val(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => $"\"{v.GetString()}\"",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "1", JsonValueKind.False => "0",
        JsonValueKind.Undefined or JsonValueKind.Null => "—",
        _ => v.GetRawText()
    };

    static string Current(Op o)
    {
        try
        {
            using var b = Reg.Base(o.Hive ?? "HKLM");
            using var k = b.OpenSubKey(o.Path ?? "");
            var v = k?.GetValue(o.Name ?? "", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
            return v switch { null => "no existe", int i => unchecked((uint)i).ToString(), string s => $"\"{s}\"", _ => v.ToString() ?? "?" };
        }
        catch { return "?"; }
    }

    static string StartLabel(int start, bool delayed) => start switch
    {
        2 => delayed ? "automático (retrasado)" : "automático", 3 => "manual", 4 => "desactivado", 0 or 1 => "sistema", _ => "no instalado"
    };

    static string ModeLabel(string? mode) => mode switch
    {
        "disabled" => "desactivado", "demand" => "manual", "auto" => "automático", "delayed-auto" => "automático (retrasado)", _ => mode ?? "?"
    };

    static string PowerName(string? guid) => (guid ?? "").ToLowerInvariant() switch
    {
        "48e6b7a6-50f5-4782-a5d4-53bb8f07e226" => "suspensión selectiva de USB",
        "ee12f906-d277-404b-b6da-e5fa1a576df5" => "ahorro de energía de PCI Express",
        "893dee8e-2bef-41e0-89c6-b55d0929964c" => "estado mínimo del procesador (%)",
        "bc5038f7-23e0-4960-96da-33abaf5935ec" => "estado máximo del procesador (%)",
        "0cc5b647-c1df-4637-891a-dec35c318583" => "núcleos mínimos activos (aparcamiento)",
        "be337238-0d82-4146-a960-4f3749d470c7" => "modo de aceleración (boost) del procesador",
        "6738e2c4-e8a5-4a42-b16a-e040e769756e" => "apagar el disco tras (s)",
        "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e" => "apagar la pantalla tras (s)",
        "29f6c1db-86da-48c5-9fdb-f2b67b1f44da" => "suspender tras (s)",
        "9d7815a6-7ee4-497e-8888-515a05f02364" => "hibernar tras (s)",
        "12bbebe6-58d6-4636-95bb-3217ef867c1a" => "ahorro de energía del Wi-Fi",
        "5ca83367-6e45-459f-a27b-476b1d01c936" => "acción al cerrar la tapa",
        "aded5e82-b909-4619-9949-f5d71dac0bcb" => "brillo de la pantalla (%)",
        "fbd9aa66-9553-4097-ba44-ed6e9d65eab8" => "brillo adaptable",
        "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d" => "temporizadores de reactivación",
        "d4e98f31-5ffe-4ce1-be31-1b38b384c009" => "temporizadores de reactivación",
        "94d3a615-a899-4ac5-ae2b-e4d8f634367f" => "directiva de refrigeración",
        _ => $"ajuste avanzado {Trunc(guid, 8)}"
    };

    static string Trunc(string? s, int n) => s is null ? "" : s.Length <= n ? s : s[..n] + "…";

    /// <summary>Qué toca cada operación del ajuste, con el valor actual cuando se puede leer.</summary>
    public static List<string> Describe(Tweak t)
    {
        var list = new List<string>();
        foreach (var o in t.Apply)
        {
            string line = o.K switch
            {
                "reg" => $"Registro · {o.Hive}\\{o.Path}\\{o.Name}\n    ahora: {Current(o)}   →   nuevo: {Val(o.Value)}",
                "regdel" => $"Registro · borra {o.Hive}\\{o.Path}\\{o.Name}\n    ahora: {Current(o)}",
                "svc" => SvcLine(o),
                "power" => $"Plan de energía (enchufado) · {PowerName(o.Setting)}   →   {Val(o.Value)}",
                "dx" => $"DirectX (ajustes de gráficos) · {o.Name}   →   {Val(o.Value)}",
                "iface" => $"Red, en cada adaptador · {o.Name}   →   {Val(o.Value)}",
                "run" => $"Comando · {Trunc(o.Cmd, 160)}",
                _ => o.K
            };
            list.Add(line);
        }
        list.Add("WinRaptor guarda el valor original de cada cambio: «Deshacer» lo deja exactamente como estaba.");
        return list;
    }

    static string SvcLine(Op o)
    {
        try
        {
            var (start, delayed) = Services.GetStart(o.Name ?? "");
            return $"Servicio · {o.Name}\n    inicio ahora: {StartLabel(start, delayed)}   →   nuevo: {ModeLabel(o.Mode)}";
        }
        catch { return $"Servicio · {o.Name}   →   {ModeLabel(o.Mode)}"; }
    }
}
