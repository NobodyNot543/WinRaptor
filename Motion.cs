using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPulse;

/// <summary>Animaciones de la interfaz. Solo se animan al entrar, al pasar el ratón o al pulsar:
/// nada se queda animándose en reposo, para no gastar CPU ni GPU.</summary>
public static class Motion
{
    static IEasingFunction OutQuint => new QuinticEase { EasingMode = EasingMode.EaseOut };
    static IEasingFunction OutCubic => new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Escala y desplazamiento propios del elemento (crea un grupo editable si hace falta).</summary>
    public static (ScaleTransform S, TranslateTransform T) Tx(UIElement el)
    {
        if (el.RenderTransform is TransformGroup g && !g.IsFrozen && g.Children.Count == 2
            && g.Children[0] is ScaleTransform s0 && g.Children[1] is TranslateTransform t0)
            return (s0, t0);
        var s = new ScaleTransform();
        var t = new TranslateTransform();
        el.RenderTransform = new TransformGroup { Children = { s, t } };
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        return (s, t);
    }

    static DoubleAnimation A(double? from, double to, int ms, int delay = 0, IEasingFunction? ease = null) =>
        new() { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = ease ?? OutCubic };

    /// <summary>Aparece desde abajo con un fundido.</summary>
    public static void Enter(UIElement el, double dy = 14, int delay = 0, int ms = 420)
    {
        var (_, t) = Tx(el);
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, A(0, 1, ms, delay));
        t.BeginAnimation(TranslateTransform.YProperty, A(dy, 0, ms + 80, delay, OutQuint));
    }

    /// <summary>Aparece deslizándose en horizontal con un fundido.</summary>
    public static void EnterX(UIElement el, double dx, int delay = 0, int ms = 480)
    {
        var (_, t) = Tx(el);
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, A(0, 1, ms, delay));
        t.BeginAnimation(TranslateTransform.XProperty, A(dx, 0, ms + 80, delay, OutQuint));
    }

    /// <summary>Aparición al terminar de cargar: de borroso a nítido, subiendo y creciendo un poco.</summary>
    public static void Reveal(UIElement el, int delay = 0, double dy = 18)
    {
        var (s, t) = Tx(el);
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, A(0, 1, 420, delay));
        t.BeginAnimation(TranslateTransform.YProperty, A(dy, 0, 600, delay, OutQuint));
        s.BeginAnimation(ScaleTransform.ScaleXProperty, A(0.975, 1, 600, delay, OutQuint));
        s.BeginAnimation(ScaleTransform.ScaleYProperty, A(0.975, 1, 600, delay, OutQuint));
        // Desenfoque temporal: se quita al acabar para no dejar ningún efecto activo.
        if (el.Effect is null)
        {
            var blur = new System.Windows.Media.Effects.BlurEffect { Radius = 12 };
            el.Effect = blur;
            var b = A(12, 0, 460, delay);
            b.Completed += (_, _) => { if (el.Effect == blur) el.Effect = null; };
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, b);
        }
    }

    /// <summary>Aparición en cascada de los bloques principales de una sección.</summary>
    public static void RevealChildren(FrameworkElement root, int step = 70, int startDelay = 0)
    {
        var p = root as Panel ?? (root as ScrollViewer)?.Content as Panel ?? (root as ContentControl)?.Content as Panel;
        if (p is null) { Reveal(root, startDelay); return; }
        int i = 0;
        foreach (UIElement c in p.Children)
        {
            if (c.Visibility != Visibility.Visible) continue;
            Reveal(c, startDelay + Math.Min(i++, 8) * step);
        }
    }

    /// <summary>Aparece creciendo ligeramente (diálogos).</summary>
    public static void Pop(UIElement el, int ms = 320)
    {
        var (s, _) = Tx(el);
        var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, A(0.94, 1, ms, 0, ease));
        s.BeginAnimation(ScaleTransform.ScaleYProperty, A(0.94, 1, ms, 0, ease));
    }

    public static void Fade(UIElement el, double to, int ms, Action? done = null)
    {
        var a = A(null, to, ms);
        if (done is not null) a.Completed += (_, _) => done();
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }

    /// <summary>Entrada escalonada de los hijos de un panel.</summary>
    public static void StaggerChildren(Panel p, int step = 40, double dy = 16, int startDelay = 0)
    {
        int i = 0;
        foreach (UIElement c in p.Children)
        {
            if (c.Visibility != Visibility.Visible) continue;
            Enter(c, dy, startDelay + Math.Min(i++, 16) * step);
        }
    }

    // ---------- Entrada escalonada de los elementos de una lista ----------

    public static readonly DependencyProperty StaggerProperty = DependencyProperty.RegisterAttached(
        "Stagger", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not FrameworkElement fe) return;
            fe.Loaded -= OnStaggerLoaded;
            if ((bool)e.NewValue) fe.Loaded += OnStaggerLoaded;
        }));
    public static void SetStagger(DependencyObject d, bool v) => d.SetValue(StaggerProperty, v);
    public static bool GetStagger(DependencyObject d) => (bool)d.GetValue(StaggerProperty);

    static readonly DependencyProperty EnteredProperty = DependencyProperty.RegisterAttached("Entered", typeof(bool), typeof(Motion));

    static void OnStaggerLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        if ((bool)fe.GetValue(EnteredProperty)) return;
        fe.SetValue(EnteredProperty, true);
        // Se anima el contenedor de la lista para no pisar la opacidad que el elemento tenga enlazada.
        UIElement target = fe;
        int index = 0;
        if (VisualTreeHelper.GetParent(fe) is ContentPresenter cp && ItemsControl.ItemsControlFromItemContainer(cp) is { } ic)
        {
            target = cp;
            index = Math.Max(0, ic.ItemContainerGenerator.IndexFromContainer(cp));
        }
        Enter(target, 12, Math.Min(index, 14) * 32, 380);
    }

    // ---------- Elevación al pasar el ratón (tarjetas) ----------

    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
        "Hover", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not UIElement el) return;
            el.MouseEnter -= HoverIn; el.MouseLeave -= HoverOut;
            if ((bool)e.NewValue) { el.MouseEnter += HoverIn; el.MouseLeave += HoverOut; }
        }));
    public static void SetHover(DependencyObject d, bool v) => d.SetValue(HoverProperty, v);
    public static bool GetHover(DependencyObject d) => (bool)d.GetValue(HoverProperty);

    static void HoverIn(object sender, MouseEventArgs e)
    {
        var (_, t) = Tx((UIElement)sender);
        t.BeginAnimation(TranslateTransform.YProperty, A(null, -2, 200, 0, OutQuint));
    }

    static void HoverOut(object sender, MouseEventArgs e)
    {
        var (_, t) = Tx((UIElement)sender);
        t.BeginAnimation(TranslateTransform.YProperty, A(null, 0, 260, 0, OutQuint));
    }

    // ---------- Pulsación (botones) ----------

    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not UIElement el) return;
            el.PreviewMouseLeftButtonDown -= PressDown; el.PreviewMouseLeftButtonUp -= PressUp;
            el.MouseLeave -= PressUp; el.LostMouseCapture -= PressUp;
            if ((bool)e.NewValue)
            {
                el.PreviewMouseLeftButtonDown += PressDown; el.PreviewMouseLeftButtonUp += PressUp;
                el.MouseLeave += PressUp; el.LostMouseCapture += PressUp;
            }
        }));
    public static void SetPress(DependencyObject d, bool v) => d.SetValue(PressProperty, v);
    public static bool GetPress(DependencyObject d) => (bool)d.GetValue(PressProperty);

    static void PressDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement { IsEnabled: false }) return;
        var (s, _) = Tx((UIElement)sender);
        s.BeginAnimation(ScaleTransform.ScaleXProperty, A(null, 0.965, 90));
        s.BeginAnimation(ScaleTransform.ScaleYProperty, A(null, 0.965, 90));
    }

    static void PressUp(object sender, EventArgs e)
    {
        var (s, _) = Tx((UIElement)sender);
        if (s.ScaleX >= 1 && !s.HasAnimatedProperties) return;
        var ease = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, A(null, 1, 260, 0, ease));
        s.BeginAnimation(ScaleTransform.ScaleYProperty, A(null, 1, 260, 0, ease));
    }
}
