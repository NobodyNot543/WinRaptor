using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPulse;

public sealed record ChartBar(string Label, double Value, string Display, Severity Level = Severity.Info);

static class Ink
{
    public static readonly Typeface Ui = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface UiBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    public static readonly Brush Muted = Palette.B("#64748B"), Sub = Palette.B("#94A3B8"), Text = Palette.B("#F1F5F9");
    public static readonly Brush Amber = Palette.B("#FBBF24"), Red = Palette.B("#F87171");

    public static FormattedText Fmt(Visual v, string s, double size, Brush b, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? UiBold : Ui, size, b, VisualTreeHelper.GetDpi(v).PixelsPerDip);

    public static Pen P(Brush b, double t, params double[] dash)
    {
        var p = new Pen(b, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dash.Length > 0) p.DashStyle = new DashStyle(dash, 0);
        p.Freeze();
        return p;
    }

    public static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

/// <summary>Gráfica de línea: curva suave, área degradada, rejilla con escala, línea de límite,
/// marca del máximo, cursor con el valor bajo el ratón y animación de dibujo.</summary>
public class LineChart : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double),
        typeof(LineChart), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    IReadOnlyList<double> _v = Array.Empty<double>();
    public double Threshold { get; set; }
    public string Unit { get; set; } = "ms";
    public int SplitAt { get; set; } = -1;
    public string SplitLeft { get; set; } = "";
    public string SplitRight { get; set; } = "";
    public double FixedMax { get; set; }
    public bool Compact { get; set; }
    /// <summary>En modo en vivo, el eje X abarca siempre este número de muestras (los datos entran por la derecha).</summary>
    public int Capacity { get; set; }
    public Color ColorA { get; set; } = Color.FromRgb(0x8B, 0x5C, 0xF6);
    public Color ColorB { get; set; } = Color.FromRgb(0x06, 0xB6, 0xD4);

    int _hover = -1;
    Rect _plot;
    List<Point> _pts = new();

    public LineChart() { ClipToBounds = true; Cursor = Cursors.Cross; }

    public void SetData(IReadOnlyList<double> values, bool animate)
    {
        _v = values;
        if (_hover >= values.Count) _hover = -1;
        if (animate)
        {
            BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1100))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else InvalidateVisual();
    }

    string F(double v) => (Math.Abs(v) < 10 && Unit != "%" ? v.ToString("0.#") : v.ToString("0")) + (Unit.Length > 0 ? " " + Unit : "");

    static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        double e = Math.Pow(10, Math.Floor(Math.Log10(v))), f = v / e;
        double nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return nf * e;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H)); // para recibir el ratón
        var v = _v;
        double padL = Compact ? 0 : 46, padR = Compact ? 8 : 14, padT = Compact ? 6 : 22, padB = Compact ? 16 : 22;
        _plot = new Rect(padL, padT, Math.Max(1, W - padL - padR), Math.Max(1, H - padT - padB));
        var plot = _plot;

        double dataMax = v.Count > 0 ? v.Max() : 0;
        double max = FixedMax > 0 ? FixedMax : NiceMax(Math.Max(dataMax, Threshold) * 1.12);

        // Rejilla y escala
        // 5 divisiones si el máximo es 5·10ⁿ o 2,5·10ⁿ, para que la escala salga en números redondos.
        double mant = max / Math.Pow(10, Math.Floor(Math.Log10(max)));
        int ticks = Compact ? 2 : (Math.Abs(mant - 5) < 1e-6 || Math.Abs(mant - 2.5) < 1e-6) ? 5 : 4;
        var grid = Ink.P(new SolidColorBrush(Color.FromArgb(34, 148, 163, 184)), 1, 1, 4);
        for (int i = 0; i <= ticks; i++)
        {
            double y = Math.Round(plot.Bottom - (double)i / ticks * plot.Height) + 0.5;
            dc.DrawLine(i == 0 ? Ink.P(new SolidColorBrush(Color.FromArgb(60, 148, 163, 184)), 1) : grid, new Point(plot.Left, y), new Point(plot.Right, y));
            if (!Compact)
            {
                var t = Ink.Fmt(this, F(max * i / ticks), 10, Ink.Muted);
                dc.DrawText(t, new Point(plot.Left - 10 - t.Width, y - t.Height / 2));
            }
            else if (i > 0)
            {
                var t = Ink.Fmt(this, F(max * i / ticks), 9, Ink.Muted);
                dc.DrawText(t, new Point(plot.Left + 2, y + 2));
            }
        }

        _pts.Clear();
        if (v.Count < 2)
        {
            var t = Ink.Fmt(this, "Recogiendo datos…", 11, Ink.Muted);
            dc.DrawText(t, new Point(plot.Left + (plot.Width - t.Width) / 2, plot.Top + (plot.Height - t.Height) / 2));
            return;
        }
        if (Compact)
        {
            var a = Ink.Fmt(this, Capacity > 0 ? $"hace {v.Count - 1} s" : "inicio", 9, Ink.Muted);
            dc.DrawText(a, new Point(plot.Left, plot.Bottom + 3));
            var b = Ink.Fmt(this, "ahora", 9, Ink.Muted);
            dc.DrawText(b, new Point(plot.Right - b.Width, plot.Bottom + 3));
        }

        // La serie ocupa siempre todo el ancho, aunque aún haya pocas muestras.
        int n = v.Count;
        int offset = 0;
        double X(int i) => plot.Left + (i + offset) * plot.Width / (n - 1);
        double Y(double val) => plot.Bottom - Math.Clamp(val / max, 0, 1) * plot.Height;
        for (int i = 0; i < v.Count; i++) _pts.Add(new Point(X(i), Y(v[i])));

        bool smooth = v.Count <= 160;
        var line = Build(_pts, smooth, false, plot);
        var area = Build(_pts, smooth, true, plot);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, plot.Left + (plot.Width + 6) * Progress, H)));

        // Zona «con carga» (por ejemplo, el ping durante la descarga)
        if (SplitAt > 0 && SplitAt < v.Count)
        {
            double sx = X(SplitAt);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(14, 251, 191, 36)), null, new Rect(sx, plot.Top, plot.Right - sx, plot.Height));
            dc.DrawLine(Ink.P(new SolidColorBrush(Color.FromArgb(110, 251, 191, 36)), 1, 3, 3), new Point(sx, plot.Top), new Point(sx, plot.Bottom));
        }

        var areaBrush = new LinearGradientBrush(Ink.A(ColorA, 95), Ink.A(ColorB, 0), 90);
        dc.DrawGeometry(areaBrush, null, area);
        var lineBrush = new LinearGradientBrush(ColorA, ColorB, 0);
        dc.DrawGeometry(null, new Pen(lineBrush, Compact ? 1.8 : 2.4) { LineJoin = PenLineJoin.Round }, line);

        // Límite
        if (Threshold > 0 && Threshold <= max)
        {
            double ty = Math.Round(Y(Threshold)) + 0.5;
            dc.DrawLine(Ink.P(Ink.Amber, 1.2, 4, 4), new Point(plot.Left, ty), new Point(plot.Right, ty));
            if (!Compact)
            {
                var t = Ink.Fmt(this, "límite " + F(Threshold), 10, Ink.Amber, true);
                var r = new Rect(plot.Left + 6, ty - t.Height - 5, t.Width + 10, t.Height + 2);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 20, 24, 33)), null, r, 4, 4);
                dc.DrawText(t, new Point(r.Left + 5, r.Top + 1));
            }
            // Muestras por encima del límite
            if (!Compact)
            {
                int shown = 0;
                for (int i = 0; i < v.Count && shown < 80; i++)
                    if (v[i] > Threshold) { dc.DrawEllipse(Ink.Red, null, _pts[i], 2.6, 2.6); shown++; }
            }
        }

        // En vivo: punto con halo en el valor actual
        if (Compact)
        {
            var lp = _pts[^1];
            dc.DrawEllipse(new SolidColorBrush(Ink.A(ColorB, 60)), null, lp, 7, 7);
            dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(ColorB), 2), lp, 3, 3);
        }

        // Máximo
        if (!Compact && dataMax > 0)
        {
            int mi = 0;
            for (int i = 1; i < v.Count; i++) if (v[i] > v[mi]) mi = i;
            var mp = _pts[mi];
            dc.DrawEllipse(new SolidColorBrush(Ink.A(ColorA, 60)), null, mp, 8, 8);
            dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(ColorA), 2), mp, 3.5, 3.5);
            var t = Ink.Fmt(this, "máx " + F(v[mi]), 10, Ink.Text, true);
            double tx = Math.Clamp(mp.X - t.Width / 2, plot.Left, plot.Right - t.Width);
            double ty = mp.Y - t.Height - 10 < 0 ? mp.Y + 10 : mp.Y - t.Height - 10;
            dc.DrawText(t, new Point(tx, ty));
        }
        dc.Pop();

        // Etiquetas de las zonas
        if (!Compact && SplitAt > 0 && SplitAt < v.Count)
        {
            var l = Ink.Fmt(this, SplitLeft, 10, Ink.Sub, true);
            dc.DrawText(l, new Point(plot.Left + 6, 3));
            var r = Ink.Fmt(this, SplitRight, 10, Ink.Amber, true);
            dc.DrawText(r, new Point(X(SplitAt) + 6, 3));
        }
        if (!Compact)
        {
            var a = Ink.Fmt(this, "inicio", 10, Ink.Muted);
            dc.DrawText(a, new Point(plot.Left, plot.Bottom + 6));
            var b = Ink.Fmt(this, $"{v.Count} muestras", 10, Ink.Muted);
            dc.DrawText(b, new Point(plot.Right - b.Width, plot.Bottom + 6));
        }

        // Cursor
        if (_hover >= 0 && _hover < _pts.Count && Progress >= 0.999)
        {
            var p = _pts[_hover];
            dc.DrawLine(Ink.P(new SolidColorBrush(Color.FromArgb(90, 203, 213, 225)), 1), new Point(p.X, plot.Top), new Point(p.X, plot.Bottom));
            dc.DrawEllipse(new SolidColorBrush(Ink.A(ColorB, 70)), null, p, 9, 9);
            dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(ColorB), 2), p, 4, 4);

            var t1 = Ink.Fmt(this, F(v[_hover]), 13, Ink.Text, true);
            var t2 = Ink.Fmt(this, Capacity > 0 ? $"hace {v.Count - 1 - _hover} s" : $"muestra {_hover + 1}", 10, Ink.Sub);
            double bw = Math.Max(t1.Width, t2.Width) + 20, bh = t1.Height + t2.Height + 12;
            double bx = p.X + 14 + bw > W ? p.X - 14 - bw : p.X + 14;
            double by = Math.Clamp(p.Y - bh / 2, 0, Math.Max(0, H - bh));
            var box = new Rect(bx, by, bw, bh);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(245, 24, 29, 40)), Ink.P(new SolidColorBrush(Color.FromArgb(255, 52, 60, 80)), 1), box, 8, 8);
            dc.DrawText(t1, new Point(bx + 10, by + 6));
            dc.DrawText(t2, new Point(bx + 10, by + 6 + t1.Height));
        }
    }

    static StreamGeometry Build(List<Point> p, bool smooth, bool closed, Rect plot)
    {
        double Cy(double y) => Math.Clamp(y, plot.Top, plot.Bottom);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (closed) { c.BeginFigure(new Point(p[0].X, plot.Bottom), true, true); c.LineTo(p[0], false, false); }
            else c.BeginFigure(p[0], false, false);
            for (int i = 0; i < p.Count - 1; i++)
            {
                if (!smooth) { c.LineTo(p[i + 1], true, true); continue; }
                Point p0 = p[Math.Max(i - 1, 0)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(i + 2, p.Count - 1)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Cy(p1.Y + (p2.Y - p0.Y) / 6));
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Cy(p2.Y - (p3.Y - p1.Y) / 6));
                c.BezierTo(c1, c2, p2, true, true);
            }
            if (closed) c.LineTo(new Point(p[^1].X, plot.Bottom), false, false);
        }
        g.Freeze();
        return g;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pts.Count == 0) return;
        double x = e.GetPosition(this).X;
        int best = 0;
        double bd = double.MaxValue;
        for (int i = 0; i < _pts.Count; i++)
        {
            double d = Math.Abs(_pts[i].X - x);
            if (d < bd) { bd = d; best = i; }
        }
        if (best != _hover) { _hover = best; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }
}

/// <summary>Barras horizontales con etiqueta, valor y crecimiento animado escalonado.</summary>
public class BarChart : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double),
        typeof(BarChart), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    IReadOnlyList<ChartBar> _items = Array.Empty<ChartBar>();
    const double Row = 36;
    int _hover = -1;

    public void SetData(IReadOnlyList<ChartBar> items)
    {
        _items = items;
        InvalidateMeasure();
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1000))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    protected override Size MeasureOverride(Size available) => new(0, _items.Count * Row);

    static (Color a, Color b) Colors(Severity s) => s switch
    {
        Severity.Ok => (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x34, 0xD3, 0x99)),
        Severity.Warn => (Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xFB, 0xBF, 0x24)),
        Severity.Bad => (Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xF8, 0x71, 0x71)),
        _ => (Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x06, 0xB6, 0xD4))
    };

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, ActualHeight));
        if (_items.Count == 0) return;
        double labelW = Math.Min(190, W * 0.32), valueW = 120;
        double trackX = labelW, trackW = Math.Max(10, W - labelW - valueW);
        double max = Math.Max(_items.Max(i => i.Value), 1e-9);
        var track = new SolidColorBrush(Color.FromRgb(0x1C, 0x22, 0x30));

        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            double cy = i * Row + Row / 2;
            if (i == _hover) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)), null, new Rect(-6, i * Row + 2, W + 12, Row - 4), 8, 8);

            var lbl = Ink.Fmt(this, it.Label, 12, i == _hover ? Ink.Text : Ink.Sub);
            lbl.MaxTextWidth = Math.Max(10, labelW - 14); lbl.MaxLineCount = 1; lbl.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(lbl, new Point(0, cy - lbl.Height / 2));

            var tr = new Rect(trackX, cy - 4, trackW, 8);
            dc.DrawRoundedRectangle(track, null, tr, 4, 4);
            // Cada barra arranca un poco después que la anterior.
            double p = Math.Clamp((Progress * (1 + 0.12 * _items.Count) - 0.12 * i), 0, 1);
            p = 1 - Math.Pow(1 - p, 3);
            double w = Math.Max(it.Value > 0 ? 8 : 0, it.Value / max * trackW * p);
            if (w > 0)
            {
                var (a, b) = Colors(it.Level);
                var fill = new LinearGradientBrush(a, b, 0);
                dc.DrawRoundedRectangle(new SolidColorBrush(Ink.A(b, 40)), null, new Rect(trackX, cy - 7, w, 14), 7, 7);
                dc.DrawRoundedRectangle(fill, null, new Rect(trackX, cy - 4, w, 8), 4, 4);
            }
            var val = Ink.Fmt(this, it.Display, 12.5, new SolidColorBrush(Color.FromArgb((byte)(255 * p), 0xF1, 0xF5, 0xF9)), true);
            dc.DrawText(val, new Point(W - val.Width, cy - val.Height / 2));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = (int)(e.GetPosition(this).Y / Row);
        if (h != _hover) { _hover = h; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }
}

public sealed record CompareRow(string Label, double Before, double After, double Max, string BeforeText, string AfterText);

/// <summary>Comparativa por filas: barra fina gris con el «antes» y barra de color con el «ahora».</summary>
public class CompareChart : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double),
        typeof(CompareChart), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    IReadOnlyList<CompareRow> _rows = Array.Empty<CompareRow>();
    const double Row = 42;
    int _hover = -1;

    public void SetData(IReadOnlyList<CompareRow> rows)
    {
        _rows = rows;
        InvalidateMeasure();
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1200))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    protected override Size MeasureOverride(Size available) => new(0, _rows.Count * Row);

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, ActualHeight));
        if (_rows.Count == 0) return;
        double labelW = Math.Min(170, W * 0.28), valueW = 130;
        double trackX = labelW, trackW = Math.Max(10, W - labelW - valueW);
        var track = new SolidColorBrush(Color.FromRgb(0x1C, 0x22, 0x30));
        var before = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x68));
        var after = new LinearGradientBrush(Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x06, 0xB6, 0xD4), 0);

        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            double top = i * Row, cy = top + Row / 2;
            if (i == _hover) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)), null, new Rect(-6, top + 2, W + 12, Row - 4), 8, 8);

            var lbl = Ink.Fmt(this, r.Label, 12, i == _hover ? Ink.Text : Ink.Sub);
            lbl.MaxTextWidth = Math.Max(10, labelW - 14); lbl.MaxLineCount = 1; lbl.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(lbl, new Point(0, cy - lbl.Height / 2));

            double p = Math.Clamp(Progress * (1 + 0.08 * _rows.Count) - 0.08 * i, 0, 1);
            p = 1 - Math.Pow(1 - p, 3);
            double max = Math.Max(r.Max, 1e-9);

            dc.DrawRoundedRectangle(track, null, new Rect(trackX, cy - 9, trackW, 5), 2.5, 2.5);
            double bw = r.Before / max * trackW * p;
            if (bw > 0) dc.DrawRoundedRectangle(before, null, new Rect(trackX, cy - 9, Math.Max(5, bw), 5), 2.5, 2.5);

            dc.DrawRoundedRectangle(track, null, new Rect(trackX, cy + 1, trackW, 8), 4, 4);
            double aw = r.After / max * trackW * p;
            if (aw > 0) dc.DrawRoundedRectangle(after, null, new Rect(trackX, cy + 1, Math.Max(8, aw), 8), 4, 4);

            byte alpha = (byte)(255 * p);
            var bt = Ink.Fmt(this, r.BeforeText + "  →  ", 12, new SolidColorBrush(Color.FromArgb(alpha, 0x64, 0x74, 0x8B)));
            var at = Ink.Fmt(this, r.AfterText, 13, new SolidColorBrush(Color.FromArgb(alpha, 0xF1, 0xF5, 0xF9)), true);
            double x = W - at.Width;
            dc.DrawText(at, new Point(x, cy - at.Height / 2));
            dc.DrawText(bt, new Point(x - bt.WidthIncludingTrailingWhitespace, cy - bt.Height / 2));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = (int)(e.GetPosition(this).Y / Row);
        if (h != _hover) { _hover = h; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }
}

/// <summary>Lectura barata del uso de CPU y RAM para el monitor en vivo (sin WMI ni contadores).</summary>
static class LiveStats
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    struct MemStatus
    {
        public uint Length, Load;
        public ulong Total, Avail, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemStatus m);

    static long _idle, _kernel, _user;

    public static double Cpu()
    {
        if (!GetSystemTimes(out var i, out var k, out var u)) return 0;
        long di = i - _idle, dk = k - _kernel, du = u - _user;
        bool first = _kernel == 0;
        _idle = i; _kernel = k; _user = u;
        long total = dk + du; // el tiempo de kernel incluye el tiempo inactivo
        return first || total <= 0 ? 0 : Math.Clamp((total - di) * 100.0 / total, 0, 100);
    }

    public static (double Pct, double UsedGb, double TotalGb) Ram()
    {
        var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
        if (!GlobalMemoryStatusEx(ref m) || m.Total == 0) return (0, 0, 0);
        double total = m.Total / 1073741824.0, used = (m.Total - m.Avail) / 1073741824.0;
        return (used / total * 100, used, total);
    }
}
