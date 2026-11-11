using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace WinPulse;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void On([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

static class Palette
{
    public static SolidColorBrush B(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
    public static readonly SolidColorBrush
        BasicBg = B("#0F2E24"), BasicFg = B("#34D399"),
        MediumBg = B("#33260B"), MediumFg = B("#FBBF24"),
        HighBg = B("#3A1518"), HighFg = B("#F87171"),
        OkBg = B("#0F2E24"), Ok = B("#34D399"),
        NativeBg = B("#0C2733"), Native = B("#67E8F9"),
        IdleBg = B("#1A1F2A"), Idle = B("#64748B");
}

public sealed class NavEntry
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Hint { get; init; } = "";
    public string Group { get; init; } = "";
    public int Count { get; init; }
    public Visibility CountVisibility => Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class TweakVM : Observable
{
    public Tweak T { get; }
    public TweakVM(Tweak t) => T = t;

    public string Title => T.Title;
    public string Desc => T.Desc;
    public string RiskText => T.Profile switch { "high" => "Avanzado", "medium" => "Medio", _ => "Básico" };
    public Brush RiskBg => T.Profile switch { "high" => Palette.HighBg, "medium" => Palette.MediumBg, _ => Palette.BasicBg };
    public Brush RiskFg => T.Profile switch { "high" => Palette.HighFg, "medium" => Palette.MediumFg, _ => Palette.BasicFg };
    public string RestartText => T.Restart switch
    {
        "reboot" => "Requiere reiniciar",
        "session" => "Reiniciar Explorador",
        _ => ""
    };
    public Visibility RestartVisibility => T.Restart.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string BreaksText => T.Breaks.Count == 0 ? "" : "Puede afectar a: " + string.Join(", ", T.Breaks);
    public Visibility BreaksVisibility => T.Breaks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    bool _selected;
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; On(); } }

    bool _applied, _undoable, _available = true;
    public bool IsApplied => _applied;
    public bool CanUndo => _undoable;
    public bool IsAvailable => _available;
    public double CardOpacity => _available ? 1 : 0.5;

    public void SetStatus(bool applied, bool undoable, bool available)
    {
        _applied = applied; _undoable = undoable; _available = available;
        if (!available) IsSelected = false;
        On(nameof(IsApplied)); On(nameof(CanUndo)); On(nameof(IsAvailable)); On(nameof(CardOpacity));
        On(nameof(StatusText)); On(nameof(StatusBrush)); On(nameof(StatusBg)); On(nameof(UndoVisibility));
    }

    public string StatusText => !_available ? "No disponible" : _applied ? (_undoable ? "● Aplicado" : "● Ya activo") : "○ Sin aplicar";
    public Brush StatusBrush => !_available ? Palette.Idle : _applied ? (_undoable ? Palette.Ok : Palette.Native) : Palette.Idle;
    public Brush StatusBg => !_available ? Palette.IdleBg : _applied ? (_undoable ? Palette.OkBg : Palette.NativeBg) : Palette.IdleBg;
    public Visibility UndoVisibility => _undoable ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class DiagVM
{
    public DiagResult R { get; }
    public DiagVM(DiagResult r) => R = r;
    public string Title => R.Title;
    public string Detail => R.Detail;
    public string Advice => R.Advice ?? "";
    public Visibility AdviceVisibility => string.IsNullOrEmpty(R.Advice) ? Visibility.Collapsed : Visibility.Visible;
    public string Glyph => R.Level switch { Severity.Ok => "", Severity.Info => "", Severity.Warn => "", _ => "" };
    public Brush IconFg => R.Level switch { Severity.Ok => Palette.BasicFg, Severity.Info => Palette.Native, Severity.Warn => Palette.MediumFg, _ => Palette.HighFg };
    public Brush IconBg => R.Level switch { Severity.Ok => Palette.BasicBg, Severity.Info => Palette.NativeBg, Severity.Warn => Palette.MediumBg, _ => Palette.HighBg };
}

public sealed class HistoryVM
{
    public HistoryEntry E { get; }
    public bool IsCurrent { get; }
    public HistoryVM(HistoryEntry e, bool current) { E = e; IsCurrent = current; }
    public string Title => E.Title;
    public string When => E.At.Date == DateTime.Today ? $"Hoy, {E.At:HH:mm}" : E.At.ToString("dd/MM/yyyy HH:mm");
    public string Detail => E.Detail;
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RestoreVisibility => IsCurrent ? Visibility.Collapsed : Visibility.Visible;
    public string Glyph => E.Title.StartsWith("Vuelta") ? "" : E.Applied.Count == 0 && E.Title.StartsWith("Antes") ? "" : "";
}

public sealed class MetricVM
{
    public Metric M { get; }
    public MetricVM(Metric m) => M = m;
    public string Label => M.Label.ToUpperInvariant();
    public string Value => M.Value;
    public Brush ValueBrush => M.Level switch
    {
        Severity.Ok => Palette.BasicFg, Severity.Warn => Palette.MediumFg, Severity.Bad => Palette.HighFg, _ => Palette.B("#F1F5F9")
    };
    public Brush DotBrush => M.Level switch
    {
        Severity.Ok => Palette.BasicFg, Severity.Warn => Palette.MediumFg, Severity.Bad => Palette.HighFg, _ => Palette.Idle
    };
}

public sealed class DnsRowVM
{
    public DnsResult R { get; }
    public bool IsBest { get; }
    readonly double _frac;
    public DnsRowVM(DnsResult r, bool best, double worst)
    {
        R = r; IsBest = best;
        _frac = r.Median == double.MaxValue || worst <= 0 ? 1 : Math.Clamp(r.Median / worst, 0.04, 1);
    }
    public GridLength BarStar => new(_frac, GridUnitType.Star);
    public GridLength RestStar => new(1 - _frac + 1e-6, GridUnitType.Star);
    static readonly Brush BestBar = new LinearGradientBrush(Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x34, 0xD3, 0x99), 0);
    static readonly Brush NormalBar = new LinearGradientBrush(Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x06, 0xB6, 0xD4), 0);
    public Brush BarBrush => R.Median == double.MaxValue ? Palette.HighFg : IsBest ? BestBar : NormalBar;
    public string Name => R.Provider.Name;
    public string Ips => R.Provider.Secondary.Length > 0 ? $"{R.Provider.Primary} · {R.Provider.Secondary}" : R.Provider.Primary;
    public string Note => R.Provider.Note;
    public string MedianText => R.Median < double.MaxValue ? $"{R.Median:0} ms" : "sin respuesta";
    public string Reliability => R.Failed == 0 ? "sin fallos" : $"{R.Failed} de {R.Total} fallos";
    public Brush MedianBrush => R.Median == double.MaxValue ? Palette.HighFg : IsBest ? Palette.BasicFg : Palette.B("#F1F5F9");
    public Visibility BestVisibility => IsBest ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CurrentVisibility => R.IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UseVisibility => R.IsCurrent || R.Median == double.MaxValue || !DnsBench.Providers.Contains(R.Provider) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Tarjeta de «antes → ahora». <c>Better</c>: true si el cambio es una mejora, false si empeora, null si es neutro.</summary>
public sealed class CompareVM
{
    public string Label { get; init; } = "";
    public string Before { get; init; } = "";
    public string After { get; init; } = "";
    public string Delta { get; init; } = "";
    public string Note { get; init; } = "";
    public bool? Better { get; init; }
    public string Glyph { get; init; } = "";
    public Brush DeltaFg => Better switch { true => Palette.BasicFg, false => Palette.HighFg, _ => Palette.Idle };
    public Brush DeltaBg => Better switch { true => Palette.BasicBg, false => Palette.HighBg, _ => Palette.IdleBg };
    public Visibility DeltaVisibility => Delta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Fila de la tabla de mediciones (primera medición frente a la última).</summary>
public sealed class MeasureRowVM
{
    public string Label { get; init; } = "";
    public string First { get; init; } = "";
    public string Last { get; init; } = "";
    public string Delta { get; init; } = "";
    public bool? Better { get; init; }
    public Brush DeltaFg => Better switch { true => Palette.BasicFg, false => Palette.HighFg, _ => Palette.Idle };
}

/// <summary>Línea de la pantalla de trabajo: un ajuste ya procesado.</summary>
public sealed class BusyLogItem
{
    public string Text { get; init; } = "";
    public string Tag { get; init; } = "";
    public bool Ok { get; init; }
    public bool Kernel { get; init; }
    public Visibility OkVis => Ok ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FailVis => Ok ? Visibility.Collapsed : Visibility.Visible;
    public Brush Bg => Ok ? Palette.BasicBg : Palette.HighBg;
    public Visibility TagVis => Tag.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Brush TagFg => Kernel ? Palette.MediumFg : Palette.Idle;
    public Brush TagBg => Kernel ? Palette.MediumBg : Palette.IdleBg;
}

/// <summary>Cambio que necesita reiniciar, en el aviso de reinicio.</summary>
public sealed class RebootItemVM
{
    public string Text { get; init; } = "";
    public ImpactKind K { get; init; }
    public string Kind => Impact.Label(K);
    public Brush Fg => K == ImpactKind.Kernel ? Palette.MediumFg : K == ImpactKind.Services ? Palette.Native : Palette.Idle;
    public Brush Bg => K == ImpactKind.Kernel ? Palette.MediumBg : K == ImpactKind.Services ? Palette.NativeBg : Palette.IdleBg;
}
