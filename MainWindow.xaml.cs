using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPulse;

public partial class MainWindow : Window
{
    static readonly (string Key, string Name, string Glyph, string Hint)[] Sections =
    {
        ("inicio", "Inicio", "\uE80F", "Estado del equipo y perfiles recomendados."),
        ("consejos", "Consejos", "\uE82F", "Qué conviene hacer para que tu PC vaya siempre lo mejor posible, según tus diagnósticos y análisis."),
        ("juego", "Modo juego", "\uE7FC", "Deja tu PC listo para jugar: cierra lo que sobra, pausa tareas de fondo y lo restaura todo al terminar."),
        ("sistema", "Sistema", "\uE770", "Arranque, actualizaciones y comportamiento general de Windows."),
        ("rendimiento", "Rendimiento", "\uEC4A", "Juegos, gráficos y prioridad de los programas en primer plano."),
        ("graficos", "Gráficos", "\uE7F4", "Tus pantallas, la frecuencia (Hz), la tarjeta gráfica y los ajustes de imagen para juegos."),
        ("cpu", "CPU y energía", "\uE945", "Plan de energía y ahorro de los componentes con el equipo enchufado."),
        ("red", "Red y latencia", "\uE701", "Ajustes de TCP/IP y respuesta del ratón."),
        ("servicios", "Servicios", "\uE713", "Servicios que puedes desactivar. Al deshacer vuelven a su modo de inicio original."),
        ("privacidad", "Privacidad", "\uE72E", "Telemetría, publicidad, IA integrada y permisos de las apps."),
        ("personalizacion", "Personalización", "\uE771", "Aspecto del Explorador, la barra de tareas y el menú Inicio."),
        ("seguridad", "Seguridad", "\uEA18", "Ajustes que refuerzan la protección del equipo."),
        ("reparacion", "Reparación", "\uE7BA", "Desbloquea herramientas de Windows que un virus o una política hayan deshabilitado."),
        ("historial", "Historial de cambios", "", "Cada cambio guarda un punto. Un clic en «Volver aquí» deja el equipo como estaba en ese momento."),
        ("antesdespues", "Antes y después", "\uE8AB", "Compara tu equipo antes de WinRaptor con cómo está ahora: ajustes, programas de inicio, arranque y mediciones."),
        ("diagnostico", "Diagnóstico", "\uE9D9", "Revisa el estado del equipo y te dice qué conviene arreglar."),
        ("arranque", "Programas de inicio", "\uE7E8", "Elige qué programas arrancan con Windows. Igual que el Administrador de tareas: no se borra nada."),
        ("programas", "Actualizar programas", "\uE896", "Programas instalados con una versión nueva disponible en winget."),
        ("drivers", "Drivers", "\uE772", "Comprueba tus drivers e instala los nuevos oficiales de Windows Update en un clic."),
        ("apps", "Quitar apps de Microsoft", "\uE71D", "Desinstala las apps de Microsoft y de serie que no usas. La Microsoft Store y todo lo necesario están protegidos."),
        ("herramientas", "Herramientas", "\uE90F", "Mantenimiento puntual y accesos rápidos a la configuración de Windows."),
        ("registro", "Actividad", "\uE81C", "Todo lo que WinRaptor ha hecho en esta sesión."),
    };

    readonly TweakEngine _engine;
    readonly List<TweakVM> _all;
    readonly List<NavEntry> _nav;
    readonly ObservableCollection<UserProfile> _profiles = new(Profiles.Load());
    readonly List<HistoryEntry> _history = History.Load();
    string _section = "inicio";
    bool _appsScanned, _diagRan, _startupLoaded, _upgradesScanned;

    FrameworkElement[] AllPanels => new FrameworkElement[]
        { HomePanel, TweaksPanel, AppsPanel, ToolsPanel, LogPanel, DiagPanel, StartupPanel, UpgradesPanel, HistoryPanel, AnalysisPanel, BeforeAfterPanel, DriversPanel, TipsPanel, GamePanel };

    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
        FeatHero.SizeChanged += (_, _) => FeatClip.Rect = new Rect(0, 0, FeatHero.ActualWidth, FeatHero.ActualHeight);
        // Si el modo juego está activo al cerrar WinRaptor, se restaura todo antes de salir.
        Closing += async (_, e) =>
        {
            if (_game?.Active != true || _closingAfterGame) return;
            e.Cancel = true;
            await EndGameAsync();
            _closingAfterGame = true;
            Close();
        };
        // Nada empieza solo: cada sección espera a que el usuario pulse su botón.
        DiagSummary.Text = "Pulsa «Analizar el equipo» para revisar disco, memoria, drivers, eventos y seguridad.";
        UpgradesSummary.Text = "Pulsa «Buscar actualizaciones» para ver qué programas tienen versión nueva.";
        AppsSummary.Text = "Pulsa «Buscar apps» para ver qué apps preinstaladas tienes.";
        DriverSummary.Text = "Pulsa «Buscar actualizaciones» para revisar tus drivers y buscar nuevos en Windows Update.";
        // Al arrancar, ningún campo de texto debe hacer que Inicio se desplace hasta él.
        HomePanel.RequestBringIntoView += (_, e) => { if (!_userScrolledHome && e.TargetObject is TextBox) e.Handled = true; };
        HomePanel.PreviewMouseDown += (_, _) => _userScrolledHome = true;
        HomePanel.PreviewKeyDown += (_, _) => _userScrolledHome = true;
        _engine = new TweakEngine();
        _all = _engine.Tweaks.Select(t => new TweakVM(t)).ToList();
        foreach (var vm in _all)
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TweakVM.IsSelected)) UpdateSelectionText(); };

        Log.Written += line => Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        _nav = Sections.Select(s => new NavEntry
        {
            Key = s.Key, Name = s.Name, Glyph = s.Glyph, Hint = s.Hint, Count = _all.Count(v => v.T.Sec == s.Key), Group = GroupOf(s.Key)
        }).ToList();
        var navView = new System.Windows.Data.ListCollectionView(_nav);
        navView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(NavEntry.Group)));
        LoadUser(); // antes de la lista: al asignarla ya se muestra Inicio con el saludo
        NavList.ItemsSource = navView;
        MyProfilesList.ItemsSource = _profiles;
        BuildShortcuts();
        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · {_all.Count} ajustes";
        ProfBasic.Text = $"{_all.Count(v => v.T.Rank <= 1)} ajustes";
        ProfMedium.Text = $"{_all.Count(v => v.T.Rank <= 2)} ajustes";
        ProfHigh.Text = $"{_all.Count} ajustes";
        BuildTools();
        NavList.SelectedIndex = 0;

        SourceInitialized += (_, _) => ApplyWindowEffects();
        StateChanged += (_, _) =>
        {
            // Con WindowChrome la ventana maximizada se sale unos píxeles de la pantalla.
            Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        };
        Loaded += async (_, _) =>
        {
            var sw = Stopwatch.StartNew();
            StartSplash();
            SplashStep("Leyendo tu equipo…", 0.25);
            await LoadSystemInfoAsync(animate: false);
            SplashStep($"Comprobando {_all.Count} ajustes…", 0.6);
            await RefreshStatusAsync();
            SplashStep("Preparando el historial…", 0.88);
            await Task.Run(InitHistory);
            ShowHistory();
            SplashStep($"{UserInfo.Greeting()}, {UserInfo.FirstName(_userName)}", 1);
            var wait = 2100 - (int)sw.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay(wait);
            await HideSplashAsync();
            StartLiveMonitor();
            UpdateRebootChip();
            UpdateGameUI();
            if (_game?.Active == true) ShowToast("El modo juego seguía activo. Entra en «Modo juego» y pulsa «Terminar y restaurar» para dejar tu PC como estaba.");
            System.Windows.Input.Keyboard.ClearFocus();
            HomePanel.ScrollToTop();
            int act = _all.Count(v => v.IsApplied);
            _ = Task.Run(() => { try { Snapshots.TakeIfOlder(TimeSpan.FromHours(6), "Al abrir WinRaptor", act); } catch (Exception ex) { Log.Write("Medición automática: " + ex.Message); } });
        };
    }

    static string GroupOf(string key) => key switch
    {
        "inicio" or "consejos" or "juego" => "",
        "sistema" or "rendimiento" or "graficos" or "cpu" or "red" or "servicios" or "privacidad" or "personalizacion" or "seguridad" or "reparacion" => "OPTIMIZAR",
        "analisis" or "antesdespues" or "historial" or "diagnostico" => "ANALIZAR",
        _ => "MANTENIMIENTO"
    };

    // ---------- Usuario ----------

    string _userName = Environment.UserName;
    bool _userScrolledHome;

    void LoadUser()
    {
        _userName = UserInfo.DisplayName();
        UserNameText.Text = _userName;
        UserCard.ToolTip = $"{_userName} · {Environment.UserDomainName}\\{Environment.UserName} · equipo {Environment.MachineName}";
        AvatarInitials.Text = UserInfo.Initials(_userName);
        if (UserInfo.PicturePath() is not { } pic) return;
        try
        {
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(pic);
            bi.DecodePixelWidth = 128;
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            AvatarCircle.Fill = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
            AvatarInitials.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { Log.Write("No se pudo cargar la foto de la cuenta: " + ex.Message); }
    }

    // ---------- Pantalla de carga ----------

    GradientStop[] _scanStops = Array.Empty<GradientStop>();

    void StartSplash()
    {
        var back = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
        var (s, _) = Motion.Tx(SplashLogo);
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = back });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = back });
        Motion.Fade(SplashLogo, 1, 400);
        Motion.Enter(SplashTitle, 12, 220, 500);
        Motion.Enter(SplashSub, 10, 320, 500);
        Motion.Enter(SplashProgress, 8, 420, 500);

        // El raptor aparece de la cola a la cabeza…
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var solid = new GradientStop(Colors.White, 0);
        var fade = new GradientStop(Colors.Transparent, 0.1);
        SplashMark.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5),
            GradientStops = { new GradientStop(Colors.White, 0), solid, fade }
        };
        solid.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1100)) { EasingFunction = ease });
        fade.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(0.1, 1.1, TimeSpan.FromMilliseconds(1100)) { EasingFunction = ease });

        // …y luego un destello recorre su silueta en bucle, como el pulso de un electrocardiograma.
        var s1 = new GradientStop(Colors.Transparent, -0.3);
        var s2 = new GradientStop(Colors.White, -0.15);
        var s3 = new GradientStop(Colors.Transparent, 0);
        SplashScan.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5),
            GradientStops = { s1, s2, s3 }
        };
        SplashScan.Opacity = 1;
        var begin = TimeSpan.FromMilliseconds(900);
        void Sweep(GradientStop g, double from)
        {
            var a = new DoubleAnimationUsingKeyFrames { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(from + 1.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(from + 1.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700))));
            g.BeginAnimation(GradientStop.OffsetProperty, a);
        }
        Sweep(s1, -0.3); Sweep(s2, -0.15); Sweep(s3, 0);
        _scanStops = new[] { s1, s2, s3 };

        var glow = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(975))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        SplashGlow.BeginAnimation(OpacityProperty, glow);
    }

    void SplashStep(string text, double frac)
    {
        SplashStatus.Text = text;
        SplashBar.BeginAnimation(WidthProperty, new DoubleAnimation(260 * frac, TimeSpan.FromMilliseconds(500))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    async Task HideSplashAsync()
    {
        var (s, _) = Motion.Tx(SplashLogo);
        var inE = new CubicEase { EasingMode = EasingMode.EaseIn };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.15, TimeSpan.FromMilliseconds(450)) { EasingFunction = inE });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.15, TimeSpan.FromMilliseconds(450)) { EasingFunction = inE });
        var done = new TaskCompletionSource();
        Motion.Fade(Splash, 0, 450, () => done.TrySetResult());

        // La interfaz entra mientras la pantalla de carga se desvanece.
        Motion.EnterX(SideBar, -28, 80, 550);
        Motion.Enter(HeaderStack, 10, 200, 450);
        if (_section == "inicio") Motion.StaggerChildren(HomeStack, 55, 20, 250);
        _scoreTarget = -1;
        BeginAnimation(ScoreProperty, null);
        Score = 0;
        UpdateSummary();
        AnimateSysBars();

        await done.Task;
        Splash.Visibility = Visibility.Collapsed;
        // Se paran las animaciones en bucle para que no consuman nada después.
        foreach (var g in _scanStops) g.BeginAnimation(GradientStop.OffsetProperty, null);
        SplashScan.OpacityMask = null;
        SplashScan.Visibility = Visibility.Collapsed;
        SplashGlow.BeginAnimation(OpacityProperty, null);
    }

    // ---------- Monitor en vivo ----------

    readonly List<double> _cpuLive = new(), _ramLive = new();
    System.Windows.Threading.DispatcherTimer? _liveTimer;

    void StartLiveMonitor()
    {
        LiveStats.Cpu(); // primera lectura de referencia
        _liveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _liveTimer.Tick += (_, _) =>
        {
            _liveTimer.Interval = TimeSpan.FromSeconds(1);
            double cpu = LiveStats.Cpu();
            var (ram, _, _) = LiveStats.Ram();
            Push(_cpuLive, cpu); Push(_ramLive, ram);
            DrawLive();
        };
        _liveTimer.Start();

        static void Push(List<double> l, double v) { l.Add(v); if (l.Count > 60) l.RemoveAt(0); }
    }

    void DrawLive()
    {
        // Solo se redibuja si se está viendo: minimizado o en otra sección no gasta nada.
        if (WindowState == WindowState.Minimized || HomePanel.Visibility != Visibility.Visible || _cpuLive.Count == 0) return;
        double cpu = _cpuLive[^1];
        var (ram, used, total) = LiveStats.Ram();
        {
            CpuLiveText.Text = $"{cpu:0}%";
            CpuLiveText.Foreground = cpu >= 90 ? Palette.HighFg : cpu >= 70 ? Palette.MediumFg : (Brush)FindResource("Text");
            CpuLiveSub.Text = $"media {_cpuLive.Average():0}% · máx {_cpuLive.Max():0}%";
            RamLiveText.Text = $"{ram:0}%";
            RamLiveText.Foreground = ram >= 90 ? Palette.HighFg : ram >= 80 ? Palette.MediumFg : (Brush)FindResource("Text");
            RamLiveSub.Text = $"{used:0.0} de {total:0} GB";
            CpuLive.SetData(_cpuLive.ToList(), false);
            RamLive.SetData(_ramLive.ToList(), false);
        }
    }

    // ---------- Puntuación animada ----------

    public static readonly DependencyProperty ScoreProperty = DependencyProperty.Register(nameof(Score), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, e) => ((MainWindow)d).OnScore((double)e.NewValue)));
    public double Score { get => (double)GetValue(ScoreProperty); set => SetValue(ScoreProperty, value); }
    double _scoreTarget = -1;

    void OnScore(double f)
    {
        HeroScore.Text = $"{f * 100:0}%";
        SetArc(f);
    }

    void AnimateScore(double target)
    {
        if (Math.Abs(target - _scoreTarget) < 1e-9) return;
        _scoreTarget = target;
        BeginAnimation(ScoreProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(1400))
        { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---------- Ventana ----------

    /// <summary>Ajusta el tamaño al área útil de la pantalla (sin la barra de tareas) y centra la ventana,
    /// para que la barra de título nunca quede fuera, también con escalado al 125 % o 150 %.</summary>
    void FitToScreen()
    {
        var wa = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, wa.Width);
        MinHeight = Math.Min(MinHeight, wa.Height);
        Width = Math.Max(MinWidth, Math.Min(Width, wa.Width * 0.94));
        Height = Math.Max(MinHeight, Math.Min(Height, wa.Height * 0.94));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void ApplyWindowEffects()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));  // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = redondeadas
    }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void ShowToast(string text)
    {
        ToastText.Text = text;
        // Más tiempo para mensajes largos.
        double hold = Math.Clamp(2600 + text.Length * 28, 3200, 7000);
        static KeyTime K(double ms) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms));
        var outE = new CubicEase { EasingMode = EasingMode.EaseOut };
        var inE = new CubicEase { EasingMode = EasingMode.EaseIn };

        var op = new DoubleAnimationUsingKeyFrames();
        op.KeyFrames.Add(new EasingDoubleKeyFrame(0, K(0)));
        op.KeyFrames.Add(new EasingDoubleKeyFrame(1, K(220), outE));
        op.KeyFrames.Add(new EasingDoubleKeyFrame(1, K(hold)));
        op.KeyFrames.Add(new EasingDoubleKeyFrame(0, K(hold + 320), inE));
        Toast.BeginAnimation(OpacityProperty, op);

        var x = new DoubleAnimationUsingKeyFrames();
        x.KeyFrames.Add(new EasingDoubleKeyFrame(36, K(0)));
        x.KeyFrames.Add(new EasingDoubleKeyFrame(0, K(420), new QuinticEase { EasingMode = EasingMode.EaseOut }));
        x.KeyFrames.Add(new EasingDoubleKeyFrame(0, K(hold)));
        x.KeyFrames.Add(new EasingDoubleKeyFrame(24, K(hold + 320), inE));
        ToastX.BeginAnimation(TranslateTransform.XProperty, x);

        ToastBarScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(hold)) { BeginTime = TimeSpan.FromMilliseconds(220) });
    }

    // ---------- Navegación ----------

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not NavEntry n) return;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        _ = ShowSectionAnimatedAsync(n.Key);
    }

    // ---------- Transición de carga entre secciones ----------

    int _navGen;
    Task _sectionLoad = Task.CompletedTask;
    FrameworkElement? _shownPanel;

    /// <summary>Muestra la sección con una carga breve: barra de progreso arriba, esqueleto con brillo y entrada del contenido.</summary>
    async Task ShowSectionAnimatedAsync(string key)
    {
        int gen = ++_navGen;
        _sectionLoad = Task.CompletedTask;
        bool cards = key is "inicio" or "consejos" or "juego" or "antesdespues" or "herramientas";
        SkelGrid.Visibility = cards ? Visibility.Visible : Visibility.Collapsed;
        SkelList.Visibility = cards ? Visibility.Collapsed : Visibility.Visible;
        if (SkelRows.ItemsSource is null) SkelRows.ItemsSource = new double[] { 210, 260, 180, 240, 200, 230, 190 };
        Skeleton.BeginAnimation(OpacityProperty, null);
        Skeleton.Opacity = 0;
        Skeleton.Visibility = Visibility.Visible;
        Skeleton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)));
        SkeletonShimmer(true);
        RouteStart();

        ShowSection(key, animate: false);
        var load = _sectionLoad;
        await Task.WhenAll(Task.Delay(280), Task.WhenAny(load, Task.Delay(3000)));
        if (gen != _navGen) return; // el usuario ya pulsó otra sección

        RouteFinish();
        Motion.Fade(Skeleton, 0, 170, () =>
        {
            if (gen != _navGen) return;
            Skeleton.Visibility = Visibility.Collapsed;
            SkeletonShimmer(false);
        });
        AnimateSectionIn(key);
    }

    void SkeletonShimmer(bool on)
    {
        var tt = (TranslateTransform)((LinearGradientBrush)FindResource("SkelBrush")).RelativeTransform;
        tt.BeginAnimation(TranslateTransform.XProperty, on
            ? new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }
            : null);
    }

    void RouteStart()
    {
        RouteBar.BeginAnimation(OpacityProperty, null);
        RouteBar.Opacity = 1;
        RouteScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 0.72, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void RouteFinish()
    {
        RouteScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        RouteBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(160) });
    }

    /// <summary>Entrada del contenido de la sección: bloque a bloque en Inicio y Herramientas, deslizándose en el resto.</summary>
    void AnimateSectionIn(string key)
    {
        if (_shownPanel is not { } panel) return;
        panel.BeginAnimation(OpacityProperty, null);
        panel.Opacity = 1;
        if (key == "inicio") Motion.RevealChildren(HomeStack, 70);
        else if (key == "herramientas")
        {
            Motion.RevealChildren(ToolsGrid, 45, 20);
            Motion.RevealChildren(ShortcutsGrid, 30, 260);
        }
        else Motion.RevealChildren(panel, 80);
    }

    void ShowSection(string key, bool animate = true)
    {
        _section = key;
        var s = _nav.First(x => x.Key == key);
        SectionTitle.Text = key == "inicio" ? $"{UserInfo.Greeting()}, {UserInfo.FirstName(_userName)}" : s.Name;
        SectionHint.Text = s.Hint;
        bool isTweaks = s.Count > 0;
        FrameworkElement panel = key switch
        {
            "inicio" => HomePanel, "apps" => AppsPanel, "herramientas" => ToolsPanel, "registro" => LogPanel,
            "diagnostico" => DiagPanel, "arranque" => StartupPanel, "programas" => UpgradesPanel,
            "historial" => HistoryPanel, "analisis" => AnalysisPanel, "antesdespues" => BeforeAfterPanel, "drivers" => DriversPanel, "consejos" => TipsPanel, "juego" => GamePanel, _ => TweaksPanel
        };
        foreach (var p in AllPanels) p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
        // La barra de «Aplicar seleccionados» solo tiene sentido donde hay ajustes.
        ActionBar.Visibility = isTweaks || key == "inicio" ? Visibility.Visible : Visibility.Collapsed;
        if (isTweaks) ShowTweaks(_all.Where(v => v.T.Sec == key).ToList());
        BuildAnalysisStrip(key);
        if (key == "inicio") UpdateSummary();

        // Transición: la cabecera entra ya; el contenido entra ahora o tras la carga (ShowSectionAnimatedAsync).
        Motion.Enter(HeaderStack, 8, 0, 320);
        _shownPanel = panel;
        if (key == "inicio") { HomePanel.ScrollToTop(); DrawLive(); }
        if (animate) AnimateSectionIn(key);
        else
        {
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 0;
        }
        if (key == "arranque" && !_startupLoaded) LoadStartup_Click(this, new RoutedEventArgs());
        if (key == "antesdespues") _sectionLoad = ShowBeforeAfterAsync();
        if (key == "consejos") _sectionLoad = ShowTipsAsync();
        if (key == "juego")
        {
            UpdateGameUI();
            var loads = new List<Task>();
            if (!_gameDetected && _game?.Active != true) loads.Add(GameDetectAsync());
            if (!_libLoaded) loads.Add(LoadLibraryAsync());
            _sectionLoad = Task.WhenAll(loads);
        }
    }

    void ShowTweaks(List<TweakVM> list)
    {
        TweakList.ItemsSource = list;
        int active = list.Count(v => v.IsApplied);
        ListCount.Text = $"{list.Count} ajustes · {active} activos";
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (q.Length == 0) { ShowSection(_section); return; }
        var results = _all.Where(v => v.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                   || v.Desc.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        foreach (var p in AllPanels) p.Visibility = p == TweaksPanel ? Visibility.Visible : Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Visible;
        ShowTweaks(results);
        SectionTitle.Text = "Resultados";
        SectionHint.Text = $"{results.Count} ajustes coinciden con «{q}».";
    }

    // ---------- Estado ----------

    async Task RefreshStatusAsync()
    {
        var results = await Task.Run(() => _all.Select(v =>
        {
            bool available = _engine.IsAvailable(v.T);
            if (!available) _engine.ForgetIfEmpty(v.T);
            return (v, available, applied: available && _engine.IsApplied(v.T), undo: _engine.HasBackup(v.T));
        }).ToList());
        foreach (var (v, available, applied, undo) in results) v.SetStatus(applied || undo, undo, available);
        UpdateSummary();
        UpdateSelectionText();
        if (TweakList.ItemsSource is List<TweakVM> shown) ShowTweaks(shown);
    }

    void UpdateSummary()
    {
        int active = _all.Count(v => v.IsApplied), mine = _all.Count(v => v.CanUndo);
        double frac = _all.Count == 0 ? 0 : (double)active / _all.Count;
        AnimateScore(frac);
        UpdateProfileBadge();
        HeroTitle.Text = frac switch
        {
            >= 0.85 => "Tu equipo está muy bien optimizado",
            >= 0.5 => "Buen punto de partida, aún hay margen",
            _ => "Hay bastante que optimizar"
        };
        AppliedSummary.Text = $"{active} de {_all.Count} optimizaciones activas · {mine} aplicadas con WinRaptor · {_all.Count - active} disponibles";
    }

    void SetArc(double frac)
    {
        frac = Math.Clamp(frac, 0, 0.9999);
        const double r = 55, c = 60;
        double a = frac * 2 * Math.PI;
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0,
                                        a > Math.PI, SweepDirection.Clockwise, true));
        ScoreArc.Data = new PathGeometry(new[] { fig });
    }

    void UpdateSelectionText()
    {
        int n = _all.Count(v => v.IsSelected);
        int pending = _all.Count(v => v.IsSelected && !v.IsApplied);
        SelectionCount.Text = n.ToString();
        SelectionText.Text = n == 0 ? "Ningún ajuste seleccionado" : $"{n} seleccionados · {pending} pendientes de aplicar";
    }

    void LoadSystemInfo() => _ = LoadSystemInfoAsync(animate: true);

    SystemTools.SysInfo? _sys;

    void AnimateSysBars()
    {
        if (_sys is not { } i) return;
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        RamBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
            new DoubleAnimation(0, i.RamPct, TimeSpan.FromMilliseconds(1300)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(450) });
        DiskBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
            new DoubleAnimation(0, i.DiskPct, TimeSpan.FromMilliseconds(1300)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(550) });
    }

    async Task LoadSystemInfoAsync(bool animate)
    {
        try
        {
            var i = await Task.Run(SystemTools.SystemInfo);
            _sys = i;
            OsText.Text = i.Os;
            OsSub.Text = $"{i.Build} · encendido {i.Uptime}";
            CpuText.Text = i.Cpu;
            CpuSub.Text = i.Threads;
            RamText.Text = $"{i.Ram}  ·  {i.RamPct:0}%";
            DiskText.Text = i.Disk;
            if (animate) AnimateSysBars();
        }
        catch (Exception ex) { Log.Write("No se pudo leer la información del equipo: " + ex.Message); }
    }

    // ---------- Selección ----------

    async void Profile_Click(object sender, RoutedEventArgs e)
    {
        int level = int.Parse((string)((Button)sender).Tag);
        _settings.LastProfile = null; // perfil de WinRaptor: se detecta por nivel
        _settings.Save();
        string name = level switch { 3 => "Avanzado", 2 => "Medio", _ => "Básico" };
        var set = _all.Where(v => v.T.Rank <= level && v.IsAvailable).ToList();
        var todo = set.Where(v => !v.IsApplied).ToList();
        Log.Write($"Vista previa del perfil {name}: {todo.Count} cambios.");
        await PreviewAndRunAsync($"Perfil {name}", todo, set.Count - todo.Count);
    }

    void SelectAllVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var v in (TweakList.ItemsSource as IEnumerable<TweakVM>) ?? []) v.IsSelected = v.IsAvailable;
    }

    void SelectNoneVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var v in (TweakList.ItemsSource as IEnumerable<TweakVM>) ?? []) v.IsSelected = false;
    }

    // ---------- Aplicar / deshacer ----------

    // ---------- Perfil activo ----------

    readonly AppSettings _settings = AppSettings.Load();

    (string Name, string Key, double Coverage) DetectProfile()
    {
        static double Cov(IEnumerable<TweakVM> set)
        {
            var l = set.Where(v => v.IsAvailable).ToList();
            return l.Count == 0 ? 0 : (double)l.Count(v => v.IsApplied) / l.Count;
        }
        // Un perfil propio cuenta si es el último que marcaste y casi todos sus ajustes siguen activos.
        if (_settings.LastProfile is { } lp && _profiles.FirstOrDefault(p => p.Name == lp) is { } up)
        {
            var ids = up.Tweaks.ToHashSet();
            double c = Cov(_all.Where(v => ids.Contains(v.T.Id)));
            if (c >= 0.9) return (up.Name, "custom", c);
        }
        for (int lvl = 3; lvl >= 1; lvl--)
        {
            double c = Cov(_all.Where(v => v.T.Rank <= lvl));
            if (c >= 0.9) return (lvl switch { 3 => "Avanzado", 2 => "Medio", _ => "Básico" }, lvl switch { 3 => "high", 2 => "medium", _ => "basic" }, c);
        }
        return ("Personalizado", "none", Cov(_all.Where(v => v.T.Rank <= 1)));
    }

    void UpdateProfileBadge()
    {
        var (name, key, cov) = DetectProfile();
        var (fg, bg, border) = key switch
        {
            "high" => (Palette.HighFg, Palette.HighBg, "#6E2A2E"),
            "medium" => (Palette.MediumFg, Palette.MediumBg, "#6B5216"),
            "basic" => (Palette.BasicFg, Palette.BasicBg, "#1F5C45"),
            "custom" => (Palette.B("#C4B5FD"), Palette.B("#241F3D"), "#3A3170"),
            _ => (Palette.Idle, Palette.IdleBg, "#2E3546")
        };
        ProfileText.Text = name;
        ProfileText.Foreground = fg;
        ProfileDot.Fill = fg;
        ProfileChip.Background = bg;
        ProfileChip.ToolTip = key == "none"
            ? "Tus ajustes no coinciden con ningún perfil: es una combinación propia."
            : $"Perfil activo: {name} ({cov:P0} de sus ajustes aplicados). Pulsa para ver los perfiles.";
        HeroProfileName.Text = name;
        HeroProfileName.Foreground = fg;
        HeroProfileDot.Fill = fg;
        HeroProfileCov.Text = key == "none" ? "combinación propia" : $"{cov:P0} de sus ajustes";
        HeroProfile.Background = bg;
        HeroProfile.BorderBrush = Palette.B(border);
    }

    void ProfileChip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        NavList.SelectedItem = _nav.First(n => n.Key == "inicio");

    // ---------- Reinicio necesario ----------

    /// <summary>Anota los cambios que no harán efecto hasta reiniciar y avisa al usuario.</summary>
    void NoteReboot(IEnumerable<TweakVM> changed, bool ask = true) =>
        NoteRebootItems(changed.Select(v => (v.Title, Impact.Of(v.T))), ask);

    void NoteRebootItems(IEnumerable<(string Title, ImpactKind K)> changed, bool ask = true)
    {
        var items = changed.Where(x => x.K != ImpactKind.None).ToList();
        if (items.Count == 0) return;
        _settings.RebootPendingSince ??= DateTime.Now;
        foreach (var (title, k) in items)
        {
            var r = $"{(int)k}|{title}";
            if (!_settings.RebootReasons.Contains(r)) _settings.RebootReasons.Add(r);
        }
        _settings.Save();
        UpdateRebootChip();
        if (ask) ShowRebootDialog();
    }

    void UpdateRebootChip()
    {
        bool pending = _settings.RebootPendingSince is not null;
        if (pending && RebootChip.Visibility != Visibility.Visible)
        {
            RebootChip.Visibility = Visibility.Visible;
            Motion.Pop(RebootChip, 420);
        }
        else if (!pending) RebootChip.Visibility = Visibility.Collapsed;
    }

    void ShowRebootDialog()
    {
        var reasons = _settings.RebootReasons
            .Select(r => r.Split('|', 2)).Where(p => p.Length == 2 && int.TryParse(p[0], out _))
            .Select(p => new RebootItemVM { K = (ImpactKind)int.Parse(p[0]), Text = p[1] })
            .OrderBy(x => x.K == ImpactKind.Kernel ? 0 : x.K == ImpactKind.Services ? 1 : 2).ToList();
        if (reasons.Count == 0) return;
        int kernel = reasons.Count(x => x.K == ImpactKind.Kernel);
        RebootTitle.Text = kernel > 0 ? "Hay cambios en el núcleo de Windows" : "Algunos cambios se completan al reiniciar";
        RebootText.Text = kernel > 0
            ? $"{kernel} {(kernel == 1 ? "cambio afecta" : "cambios afectan")} al núcleo de Windows (memoria, prioridades del procesador, red o drivers). " +
              "El kernel solo lee esos valores al arrancar: hasta que reinicies, siguen funcionando los valores anteriores."
            : $"{reasons.Count} {(reasons.Count == 1 ? "cambio" : "cambios")} en servicios o configuración del sistema no harán efecto hasta que reinicies.";
        RebootList.ItemsSource = reasons.Take(6).ToList();
        RebootMore.Text = reasons.Count > 6 ? $"y {reasons.Count - 6} más" : "";
        RebootMore.Visibility = reasons.Count > 6 ? Visibility.Visible : Visibility.Collapsed;

        RebootOverlay.BeginAnimation(OpacityProperty, null);
        RebootOverlay.Opacity = 0;
        RebootOverlay.Visibility = Visibility.Visible;
        Motion.Fade(RebootOverlay, 1, 220);
        Motion.Pop(RebootCard, 420);
    }

    void HideRebootDialog() => Motion.Fade(RebootOverlay, 0, 200, () => RebootOverlay.Visibility = Visibility.Collapsed);

    void RebootChip_Click(object sender, RoutedEventArgs e) => ShowRebootDialog();

    void RebootLater_Click(object sender, RoutedEventArgs e)
    {
        HideRebootDialog();
        ShowToast("De acuerdo. El aviso «Reinicio pendiente» seguirá arriba hasta que reinicies Windows.");
    }

    void RebootNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10 /c \"WinRaptor: reinicio para completar los cambios\"")
            { CreateNoWindow = true, UseShellExecute = false });
            Log.Write("Reinicio solicitado por el usuario para completar los cambios.");
            HideRebootDialog();
            ShowToast("Windows se reiniciará en 10 segundos. Guarda lo que tengas abierto.");
        }
        catch (Exception ex) { ShowToast("No se pudo reiniciar: " + ex.Message); }
    }

    // ---------- Vista previa de cambios ----------

    TaskCompletionSource<PreviewResult>? _previewTcs;
    List<PreviewItemVM> _previewItems = new();

    /// <summary>Enseña qué se va a cambiar y deja quitar elementos. Devuelve qué eligió el usuario y qué ajustes quedaron incluidos.</summary>
    async Task<(PreviewResult Result, List<TweakVM> Items)> ShowPreviewAsync(string title, string sub, List<TweakVM> todo, int alreadyActive, bool allowMark)
    {
        _previewItems = todo.Select(v => new PreviewItemVM(v)).ToList();
        foreach (var it in _previewItems)
            it.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PreviewItemVM.Include)) UpdatePreviewSummary(); };
        var order = Sections.Select(s => s.Key).ToList();
        PreviewGroups.ItemsSource = _previewItems.GroupBy(i => i.V.T.Sec).OrderBy(g => order.IndexOf(g.Key))
            .Select(g =>
            {
                var s = Sections.FirstOrDefault(x => x.Key == g.Key);
                return new PreviewGroupVM { Name = s.Name ?? g.Key, Glyph = s.Glyph ?? "", Items = g.ToList() };
            }).ToList();

        PreviewTitle.Text = title;
        PreviewSub.Text = sub;
        PvActive.Text = $"{alreadyActive} ya activos (no se tocan)";
        PvActiveChip.Visibility = alreadyActive > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewMarkBtn.Visibility = allowMark ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreviewSummary();

        PreviewOverlay.BeginAnimation(OpacityProperty, null);
        PreviewOverlay.Opacity = 0;
        PreviewOverlay.Visibility = Visibility.Visible;
        Motion.Fade(PreviewOverlay, 1, 220);
        Motion.Pop(PreviewCard, 420);

        _previewTcs = new TaskCompletionSource<PreviewResult>();
        var result = await _previewTcs.Task;
        Motion.Fade(PreviewOverlay, 0, 200, () => PreviewOverlay.Visibility = Visibility.Collapsed);
        return (result, _previewItems.Where(i => i.Include).Select(i => i.V).ToList());
    }

    void UpdatePreviewSummary()
    {
        var inc = _previewItems.Where(i => i.Include).ToList();
        int reboot = inc.Count(i => i.V.T.Restart == "reboot"), kernel = inc.Count(i => i.Impact == ImpactKind.Kernel);
        PvCount.Text = $"{inc.Count} de {_previewItems.Count} cambios incluidos";
        PvReboot.Text = reboot > 0 ? $"{reboot} necesitan reiniciar" : "No hace falta reiniciar";
        PvKernel.Text = $"{kernel} tocan el núcleo de Windows";
        PvKernelChip.Visibility = kernel > 0 ? Visibility.Visible : Visibility.Collapsed;
        var breaks = inc.SelectMany(i => i.V.T.Breaks).Distinct().ToList();
        PvBreaks.Text = "Con estos cambios puede dejar de funcionar: " + string.Join(", ", breaks) + ". Quita esos ajustes si lo usas.";
        PvBreaksBox.Visibility = breaks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewApplyText.Text = inc.Count > 0 ? $"Aplicar {inc.Count} {(inc.Count == 1 ? "cambio" : "cambios")}" : "Aplicar";
        PreviewApplyBtn.IsEnabled = PreviewMarkBtn.IsEnabled = inc.Count > 0;
    }

    void PreviewDetails_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is PreviewItemVM it) it.Expanded = !it.Expanded;
    }

    void PreviewAll_Click(object sender, RoutedEventArgs e) { foreach (var i in _previewItems) i.Include = true; }
    void PreviewNone_Click(object sender, RoutedEventArgs e) { foreach (var i in _previewItems) i.Include = false; }
    void PreviewCancel_Click(object sender, RoutedEventArgs e) => _previewTcs?.TrySetResult(PreviewResult.Cancel);
    void PreviewMark_Click(object sender, RoutedEventArgs e) => _previewTcs?.TrySetResult(PreviewResult.Mark);
    void PreviewApply_Click(object sender, RoutedEventArgs e) => _previewTcs?.TrySetResult(PreviewResult.Apply);

    /// <summary>Vista previa de un conjunto de ajustes (perfil) y, según lo que elija el usuario, los marca o los aplica.</summary>
    async Task PreviewAndRunAsync(string title, List<TweakVM> candidates, int alreadyActive)
    {
        if (candidates.Count == 0)
        {
            ShowToast("Todos los ajustes de este perfil ya están activos. No hay nada que cambiar.");
            return;
        }
        var (res, items) = await ShowPreviewAsync(title,
            "Esto es lo que WinRaptor cambiaría en tu sistema. Quita lo que no quieras y abre «Ver qué cambia exactamente» para ver cada valor. Nada se toca hasta que pulses «Aplicar».",
            candidates, alreadyActive, allowMark: true);
        if (res == PreviewResult.Cancel || items.Count == 0) return;
        foreach (var v in _all) v.IsSelected = false;
        foreach (var v in items) v.IsSelected = true;
        if (res == PreviewResult.Mark)
        {
            ShowToast($"{items.Count} ajustes marcados. Revísalos en cada sección y pulsa «Aplicar seleccionados».");
            return;
        }
        await ApplyTweaksAsync(items);
    }

    // ---------- Pantalla de progreso ----------

    readonly ObservableCollection<BusyLogItem> _busyLog = new();
    readonly Stopwatch _busyClock = new();

    void BusyReset()
    {
        _busyLog.Clear();
        BusyLog.ItemsSource = _busyLog;
        _busyClock.Restart();
        BusyProgressBox.Visibility = Visibility.Collapsed;
        BusyIndeterminate.Visibility = Visibility.Visible;
        BusyFill.BeginAnimation(WidthProperty, null);
        BusyFill.Width = 0;
        BusySpinner.Visibility = Visibility.Visible;
        BusyDone.Visibility = Visibility.Collapsed;
    }

    void BusyProgress(int done, int total, string unit = "ajustes")
    {
        if (total <= 0) return;
        if (BusyProgressBox.Visibility != Visibility.Visible)
        {
            BusyIndeterminate.Visibility = Visibility.Collapsed;
            BusyProgressBox.Visibility = Visibility.Visible;
        }
        double frac = (double)done / total;
        BusyCount.Text = $"{done} de {total} {unit}";
        BusyPct.Text = $"{frac:P0}";
        double w = Math.Max(0, BusyTrack.ActualWidth > 0 ? BusyTrack.ActualWidth : 384);
        BusyFill.BeginAnimation(WidthProperty, new DoubleAnimation(w * frac, TimeSpan.FromMilliseconds(320))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var t = _busyClock.Elapsed;
        string left = done > 0 && done < total
            ? $" · quedan unos {TimeSpan.FromTicks(t.Ticks / done * (total - done)).TotalSeconds:0} s" : "";
        BusyElapsed.Text = $"{t.TotalSeconds:0} s{left}";
    }

    void BusyLogAdd(string text, bool ok, string tag = "")
    {
        _busyLog.Insert(0, new BusyLogItem { Text = text, Ok = ok, Tag = !ok ? "error" : tag });
        while (_busyLog.Count > 4) _busyLog.RemoveAt(_busyLog.Count - 1);
    }

    void BusyLogAdd(TweakVM v, bool ok)
    {
        var k = Impact.Of(v.T);
        _busyLog.Insert(0, new BusyLogItem
        {
            Text = v.Title, Ok = ok, Kernel = k == ImpactKind.Kernel,
            Tag = !ok ? "error" : k == ImpactKind.Kernel ? "núcleo" : k != ImpactKind.None ? "reinicio" : ""
        });
        while (_busyLog.Count > 4) _busyLog.RemoveAt(_busyLog.Count - 1);
    }

    void SetBusyDetail(string detail)
    {
        if (BusyDetail.Text == detail) return;
        BusyDetail.Text = detail;
        BusyDetail.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(220)));
    }

    bool _cometsBuilt;

    /// <summary>Dibuja un arco tipo cometa: muchos tramos cortos cuya opacidad y color crecen hacia la cabeza.</summary>
    static void BuildComet(Grid host, double size, double thickness, double sweepDeg, Color tail, Color head, int segments = 0)
    {
        // Degradado circular dibujado píxel a píxel (a 3x para que se vea nítido): cola transparente y cabeza intensa, sin costuras.
        host.Children.Clear();
        const double sc = 3;
        int S = (int)Math.Ceiling(size * sc);
        var px = new byte[S * S * 4];
        double c = S / 2.0, r = (size - thickness) / 2 * sc, half = thickness / 2 * sc;
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                double dx = x + 0.5 - c, dy = y + 0.5 - c;
                double edge = half - Math.Abs(Math.Sqrt(dx * dx + dy * dy) - r);
                if (edge <= -0.5) continue;
                double ang = Math.Atan2(dx, -dy) * 180 / Math.PI;
                if (ang < 0) ang += 360;
                if (ang > sweepDeg) continue;
                double k = ang / sweepDeg;
                double a = Math.Clamp(edge + 0.5, 0, 1) * Math.Pow(k, 1.5);
                int i = (y * S + x) * 4;
                px[i] = (byte)((tail.B + (head.B - tail.B) * k) * a);
                px[i + 1] = (byte)((tail.G + (head.G - tail.G) * k) * a);
                px[i + 2] = (byte)((tail.R + (head.R - tail.R) * k) * a);
                px[i + 3] = (byte)(255 * a);
            }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(S, S, 96 * sc, 96 * sc, PixelFormats.Pbgra32, null, px, S * 4);
        bmp.Freeze();
        host.Children.Add(new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.Fill });

        // Cabeza: punto brillante con halo.
        double rr = (size - thickness) / 2, cc = size / 2, rad = (sweepDeg - 90) * Math.PI / 180;
        var hp = new Point(cc + rr * Math.Cos(rad), cc + rr * Math.Sin(rad));
        host.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = thickness * 2.2, Height = thickness * 2.2, Fill = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(hp.X - thickness * 1.1, hp.Y - thickness * 1.1, 0, 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = head, BlurRadius = 10, ShadowDepth = 0, Opacity = 1 }
        });
    }

    /// <summary>Animaciones del indicador. Solo corren mientras la pantalla de trabajo está visible.</summary>
    void BusyAnimations(bool on, bool keepBars = false)
    {
        if (on && !_cometsBuilt)
        {
            BuildComet(CometOuter, 88, 3.5, 250, Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x22, 0xD3, 0xEE));
            BuildComet(CometInner, 66, 2.5, 160, Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0xA7, 0x8B, 0xFA), 20);
            _cometsBuilt = true;
        }
        var forever = RepeatBehavior.Forever;
        if (!on)
        {
            foreach (var (o, p) in new (Animatable, DependencyProperty)[]
            {
                (RotOuter, RotateTransform.AngleProperty), (RotInner, RotateTransform.AngleProperty),
                (Rip1S, ScaleTransform.ScaleXProperty), (Rip1S, ScaleTransform.ScaleYProperty),
                (Rip2S, ScaleTransform.ScaleXProperty), (Rip2S, ScaleTransform.ScaleYProperty),
                (BreathS, ScaleTransform.ScaleXProperty), (BreathS, ScaleTransform.ScaleYProperty)
            }) o.BeginAnimation(p, null);
            Ripple1.BeginAnimation(OpacityProperty, null);
            Ripple2.BeginAnimation(OpacityProperty, null);
            if (!keepBars)
            {
                Seg1X.BeginAnimation(TranslateTransform.XProperty, null);
                Seg2X.BeginAnimation(TranslateTransform.XProperty, null);
                SheenX.BeginAnimation(TranslateTransform.XProperty, null);
            }
            return;
        }

        // Cometas: el exterior acelera y frena en cada vuelta; el interior gira al revés a ritmo constante.
        var outer = new DoubleAnimationUsingKeyFrames { RepeatBehavior = forever };
        outer.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        outer.KeyFrames.Add(new EasingDoubleKeyFrame(360, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1150)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        RotOuter.BeginAnimation(RotateTransform.AngleProperty, outer);
        RotInner.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(360, 0, TimeSpan.FromMilliseconds(1700)) { RepeatBehavior = forever });

        // Ondas que salen del centro, una tras otra.
        void Ripple(System.Windows.Shapes.Ellipse e, ScaleTransform st, int delay)
        {
            var begin = TimeSpan.FromMilliseconds(delay);
            var dur = TimeSpan.FromMilliseconds(2000);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.55, 1.45, dur) { BeginTime = begin, RepeatBehavior = forever, EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.55, 1.45, dur) { BeginTime = begin, RepeatBehavior = forever, EasingFunction = ease });
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 0, dur) { BeginTime = begin, RepeatBehavior = forever });
        }
        Ripple(Ripple1, Rip1S, 0);
        Ripple(Ripple2, Rip2S, 1000);

        // El raptor «respira».
        var breath = new DoubleAnimation(0.93, 1.05, TimeSpan.FromMilliseconds(1000))
        { AutoReverse = true, RepeatBehavior = forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        BreathS.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
        BreathS.BeginAnimation(ScaleTransform.ScaleYProperty, breath);

        // Barra indeterminada: dos segmentos que se persiguen.
        double w = 384;
        var s1 = new DoubleAnimationUsingKeyFrames { RepeatBehavior = forever };
        s1.KeyFrames.Add(new EasingDoubleKeyFrame(-150, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        s1.KeyFrames.Add(new EasingDoubleKeyFrame(w, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        s1.KeyFrames.Add(new EasingDoubleKeyFrame(w, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
        Seg1X.BeginAnimation(TranslateTransform.XProperty, s1);
        var s2 = new DoubleAnimationUsingKeyFrames { RepeatBehavior = forever };
        s2.KeyFrames.Add(new EasingDoubleKeyFrame(-60, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        s2.KeyFrames.Add(new EasingDoubleKeyFrame(-60, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
        s2.KeyFrames.Add(new EasingDoubleKeyFrame(w, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1900)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        s2.KeyFrames.Add(new EasingDoubleKeyFrame(w, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
        Seg2X.BeginAnimation(TranslateTransform.XProperty, s2);

        // Brillo que recorre la barra de progreso.
        var sheen = new DoubleAnimationUsingKeyFrames { RepeatBehavior = forever };
        sheen.KeyFrames.Add(new EasingDoubleKeyFrame(-70, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sheen.KeyFrames.Add(new EasingDoubleKeyFrame(w, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        sheen.KeyFrames.Add(new EasingDoubleKeyFrame(w, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1900))));
        SheenX.BeginAnimation(TranslateTransform.XProperty, sheen);
    }

    /// <summary>Cierre de la pantalla de trabajo: icono de hecho (o aviso) y resumen un instante.</summary>
    async Task BusyFinish(string title, string detail, bool ok)
    {
        BusyText.Text = title;
        BusyDetail.Text = detail;
        BusyAnimations(false, keepBars: true);
        BusySpinner.Visibility = Visibility.Collapsed;
        BusyDoneGlyph.Text = ok ? "✓" : "!";
        BusyDoneGlyph.FontFamily = new FontFamily("Segoe UI");
        BusyDoneGlyph.FontWeight = FontWeights.Bold;
        BusyDoneGlyph.Foreground = ok ? Palette.BasicFg : Palette.MediumFg;
        BusyDoneCircle.Stroke = ok ? Palette.BasicFg : Palette.MediumFg;
        BusyDoneCircle.Fill = ok ? Palette.BasicBg : Palette.MediumBg;
        BusyDone.Visibility = Visibility.Visible;
        Motion.Pop(BusyDone, 450);
        await Task.Delay(1150);
    }

    /// <summary>Aplica o deshace una lista de ajustes mostrando el progreso real.</summary>
    async Task<List<TweakVM>> RunTweakSteps(IList<(TweakVM V, bool Apply)> steps, Action<string> report)
    {
        var ok = new List<TweakVM>();
        BusyProgress(0, steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            var (v, apply) = steps[i];
            report((apply ? "Aplicando: " : "Deshaciendo: ") + v.Title);
            bool done = await Task.Run(() => apply ? _engine.Apply(v.T) : _engine.Revert(v.T));
            if (done) ok.Add(v);
            BusyLogAdd(v, done);
            BusyProgress(i + 1, steps.Count);
        }
        return ok;
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var todo = _all.Where(v => v.IsSelected && !v.CanUndo).ToList();
        if (todo.Count == 0)
        {
            ShowToast("No hay ajustes seleccionados pendientes de aplicar.");
            return;
        }
        var (res, items) = await ShowPreviewAsync("Revisa los cambios antes de aplicarlos",
            "Estos son los ajustes que has seleccionado. Quita lo que no quieras y abre «Ver qué cambia exactamente» para ver cada valor del sistema que se va a modificar.",
            todo, 0, allowMark: false);
        if (res != PreviewResult.Apply || items.Count == 0) return;
        foreach (var v in todo.Except(items)) v.IsSelected = false;
        await ApplyTweaksAsync(items);
    }

    async Task ApplyTweaksAsync(List<TweakVM> todo)
    {

        bool restorePoint = RestorePointCheck.IsChecked == true;
        int failed = 0;
        var okList = new List<TweakVM>();
        int activeBefore = _all.Count(v => v.IsApplied);
        await RunBusy("Aplicando ajustes…", async report =>
        {
            report("Guardando una medición del sistema para «Antes y después»…");
            try { await Task.Run(() => Snapshots.TakeIfOlder(TimeSpan.FromMinutes(10), "Antes de aplicar", activeBefore)); } catch { }
            if (restorePoint)
            {
                report("Comprobando el punto de restauración (si hay que crear uno puede tardar unos minutos)…");
                try { Log.Write(await Task.Run(SystemTools.EnsureRecentRestorePoint)); }
                catch (Exception ex)
                {
                    Log.Write("No se pudo crear el punto de restauración: " + ex.Message);
                    var go = MessageBox.Show(this, "No se pudo crear el punto de restauración:\n" + ex.Message +
                        "\n\nLos cambios se podrán deshacer igualmente desde WinRaptor. ¿Continuar?",
                        "Punto de restauración", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (go != MessageBoxResult.Yes) return;
                }
            }
            BusyText.Text = $"Aplicando {todo.Count} ajustes";
            okList = await RunTweakSteps(todo.Select(v => (v, true)).ToList(), report);
            failed = todo.Count - okList.Count;
            await BusyFinish(failed == 0 ? "Cambios aplicados" : $"Aplicados con {failed} {(failed == 1 ? "error" : "errores")}",
                $"{okList.Count} de {todo.Count} ajustes en {_busyClock.Elapsed.TotalSeconds:0} s", failed == 0);
        });

        foreach (var v in todo) v.IsSelected = false;
        await RefreshStatusAsync();
        await RecordHistoryAsync($"Aplicados {todo.Count} ajustes", Names(todo));
        FinishMessage(todo, failed, "aplicados");
        NoteReboot(okList);
    }

    async void UndoSelected_Click(object sender, RoutedEventArgs e)
    {
        var todo = _all.Where(v => v.IsSelected && v.CanUndo).ToList();
        if (todo.Count == 0)
        {
            ShowToast("Ninguno de los seleccionados se aplicó con WinRaptor: no hay nada que deshacer.");
            return;
        }
        await RevertMany(todo);
    }

    async void UndoOne_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is TweakVM v) await RevertMany(new() { v });
    }

    async void UndoAll_Click(object sender, RoutedEventArgs e)
    {
        var todo = _all.Where(v => v.CanUndo).ToList();
        if (todo.Count == 0) { ShowToast("No hay cambios de WinRaptor que deshacer."); return; }
        if (MessageBox.Show(this, $"¿Deshacer los {todo.Count} ajustes aplicados con WinRaptor?", "Deshacer todo",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RevertMany(todo);
    }

    async Task RevertMany(List<TweakVM> todo)
    {
        int failed = 0;
        var okList = new List<TweakVM>();
        await RunBusy($"Deshaciendo {todo.Count} ajustes", async report =>
        {
            okList = await RunTweakSteps(todo.Select(v => (v, false)).ToList(), report);
            failed = todo.Count - okList.Count;
            await BusyFinish(failed == 0 ? "Cambios deshechos" : $"Deshechos con {failed} {(failed == 1 ? "error" : "errores")}",
                $"{okList.Count} de {todo.Count} ajustes vuelven a su valor original", failed == 0);
        });
        foreach (var v in todo) v.IsSelected = false;
        await RefreshStatusAsync();
        await RecordHistoryAsync($"Deshechos {todo.Count} ajustes", Names(todo));
        FinishMessage(todo, failed, "deshechos");
        NoteReboot(okList);
    }

    void FinishMessage(List<TweakVM> done, int failed, string verb)
    {
        var text = $"{done.Count - failed} de {done.Count} ajustes {verb} correctamente.";
        if (failed > 0) text += $" {failed} con errores (ver Actividad).";
        // Los que necesitan reiniciar los avisa el diálogo de reinicio.
        if (!done.Any(v => v.T.Restart == "reboot") && done.Any(v => v.T.Restart == "session"))
            text += " Reinicia el Explorador (Herramientas) para verlos.";
        ShowToast(text);
    }

    int _busyGen;

    async Task RunBusy(string title, Func<Action<string>, Task> work)
    {
        int gen = ++_busyGen;
        BusyReset();
        BusyText.Text = title;
        BusyDetail.Text = "";
        ShowBusy(true);
        Root.IsEnabled = false;
        try { await work(detail => Dispatcher.Invoke(() => SetBusyDetail(detail))); }
        catch (Exception ex) { Log.Write("Error: " + ex.Message); MessageBox.Show(this, ex.Message, "WinRaptor", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            Root.IsEnabled = true;
            if (gen == _busyGen) ShowBusy(false);
        }
    }

    /// <summary>Muestra u oculta la pantalla de trabajo con fundido, desenfoque del fondo y animaciones
    /// que solo corren mientras está visible.</summary>
    void ShowBusy(bool show)
    {
        int gen = _busyGen;
        if (show)
        {
            bool wasVisible = BusyOverlay.Visibility == Visibility.Visible;
            if (!wasVisible)
            {
                BusyOverlay.BeginAnimation(OpacityProperty, null);
                BusyOverlay.Opacity = 0;
                Motion.Pop(BusyCard, 380);
            }
            BusyOverlay.Visibility = Visibility.Visible;
            Motion.Fade(BusyOverlay, 1, 200);
            BusyAnimations(true);
            var blur = Root.Effect as System.Windows.Media.Effects.BlurEffect ?? new System.Windows.Media.Effects.BlurEffect { Radius = 0, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
            Root.Effect = blur;
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(6, TimeSpan.FromMilliseconds(260)));
        }
        else
        {
            if (Root.Effect is System.Windows.Media.Effects.BlurEffect blur)
            {
                var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
                a.Completed += (_, _) => { if (gen == _busyGen && BusyOverlay.Visibility != Visibility.Visible) Root.Effect = null; };
                blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, a);
            }
            Motion.Fade(BusyOverlay, 0, 200, () =>
            {
                if (gen != _busyGen) return; // empezó otro trabajo mientras se ocultaba
                BusyOverlay.Visibility = Visibility.Collapsed;
                BusyAnimations(false);
                if (Root.Effect is not null) Root.Effect = null;
            });
        }
    }

    // ---------- Apps ----------

    readonly List<AppVM> _appVms = new();
    readonly List<RemovedApp> _removedApps = Apps.LoadRemoved();

    async void ScanApps_Click(object sender, RoutedEventArgs e)
    {
        Dictionary<string, Apps.Installed>? installed = null;
        await RunBusy("Buscando apps innecesarias…", async report =>
        {
            report("Revisando las apps instaladas para tu usuario");
            installed = await Task.Run(Apps.ListInstalled);
        });
        if (installed is null) return;
        _appsScanned = true;
        _appVms.Clear();
        foreach (var c in Apps.Catalog)
            if (installed.TryGetValue(c.Package, out var i) && !Apps.IsProtected(c.Package))
                _appVms.Add(new AppVM { C = c, Version = i.Version, Pfn = i.Pfn });
        foreach (var vm in _appVms)
            vm.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(AppVM.IsSelected)) UpdateAppsButtons(); };

        var order = Apps.Catalog.Select(c => c.Category).Distinct().ToList();
        AppsGroups.ItemsSource = _appVms.GroupBy(a => a.Category).OrderBy(g => order.IndexOf(g.Key))
            .Select(g => new AppGroupVM { Name = g.Key, Glyph = Apps.CategoryGlyph(g.Key), Items = g.OrderBy(a => a.C.Advice).ThenBy(a => a.Name).ToList() })
            .ToList();
        AppsEmpty.Visibility = _appVms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var prot = installed.Keys.Select(Apps.ProtectedLabel).OfType<string>().Distinct().OrderBy(s => s).ToList();
        AppsProtected.ItemsSource = prot;
        AppsProtectedBox.Visibility = prot.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Si alguna app quitada se ha vuelto a instalar, deja de salir en «Quitadas».
        _removedApps.RemoveAll(r => installed.ContainsKey(r.Package));
        Apps.SaveRemoved(_removedApps);
        ShowRemovedApps();
        UpdateAppsButtons();

        int rec = _appVms.Count(a => a.C.Advice == AppAdvice.Recommended);
        _insights.AppsAt = DateTime.Now;
        _insights.AppsRecommended = rec;
        _insights.Save();
        Log.Write($"Apps: {_appVms.Count} se pueden quitar ({rec} recomendadas), {prot.Count} protegidas presentes.");
    }

    void UpdateAppsButtons()
    {
        int n = _appVms.Count(a => a.IsSelected), rec = _appVms.Count(a => a.C.Advice == AppAdvice.Recommended);
        AppsRemoveBtn.IsEnabled = n > 0;
        AppsRemoveText.Text = n > 0 ? $"Desinstalar {n} {(n == 1 ? "app" : "apps")}" : "Desinstalar seleccionadas";
        AppsMarkRec.IsEnabled = rec > 0;
        AppsSummary.Text = !_appsScanned ? "Pulsa «Buscar apps» para ver qué apps de Microsoft y de serie puedes quitar."
            : _appVms.Count == 0 ? "" : $"{_appVms.Count} apps que puedes quitar · {rec} recomendadas";
    }

    void AppsMarkRecommended_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _appVms) a.IsSelected = a.C.Advice == AppAdvice.Recommended;
        ShowToast("Marcadas solo las recomendadas. Revisa la lista y pulsa «Desinstalar».");
    }

    async void RemoveApps_Click(object sender, RoutedEventArgs e)
    {
        var todo = _appVms.Where(a => a.IsSelected).ToList();
        if (todo.Count == 0) { ShowToast("Activa las apps que quieras desinstalar."); return; }
        bool deprov = AppsDeprovision.IsChecked == true;
        var careful = todo.Where(a => a.C.Advice == AppAdvice.IfUnused).Select(a => a.Name).ToList();
        var msg = $"¿Desinstalar {todo.Count} {(todo.Count == 1 ? "app" : "apps")}?\n\n• " + string.Join("\n• ", todo.Select(a => a.Name));
        if (careful.Count > 0) msg += $"\n\nOjo: {string.Join(", ", careful)} solo conviene quitarlas si no las usas.";
        msg += "\n\nSe pueden volver a instalar desde la Microsoft Store.";
        if (MessageBox.Show(this, msg, "Desinstalar apps", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var done = new List<AppVM>();
        await RunBusy($"Desinstalando {todo.Count} {(todo.Count == 1 ? "app" : "apps")}", async report =>
        {
            for (int i = 0; i < todo.Count; i++)
            {
                var a = todo[i];
                report("Desinstalando " + a.Name);
                BusyProgress(i, todo.Count, "apps");
                var (ok, m) = await Task.Run(() => Apps.Remove(a.Package, deprov));
                Log.Write(m);
                if (ok) done.Add(a);
                BusyLogAdd(a.Name, ok, ok ? "quitada" : "");
                BusyProgress(i + 1, todo.Count, "apps");
            }
            await BusyFinish(done.Count == todo.Count ? "Apps desinstaladas" : $"{done.Count} de {todo.Count} desinstaladas",
                deprov ? "Tampoco volverán con las actualizaciones de Windows" : "Puedes reinstalarlas desde la Microsoft Store", done.Count == todo.Count);
        });
        foreach (var a in done) _removedApps.Insert(0, new RemovedApp { Package = a.Package, Name = a.Name, Pfn = a.Pfn, At = DateTime.Now });
        Apps.SaveRemoved(_removedApps);
        ShowToast(done.Count == todo.Count
            ? $"{done.Count} {(done.Count == 1 ? "app desinstalada" : "apps desinstaladas")}. Si echas alguna de menos, tienes «Reinstalar» abajo."
            : $"{done.Count} de {todo.Count} apps desinstaladas. Mira Actividad para ver qué falló.");
        ScanApps_Click(sender, e);
    }

    void ShowRemovedApps()
    {
        AppsRemoved.ItemsSource = _removedApps.ToList();
        AppsRemovedBox.Visibility = _removedApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ReinstallApp_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not RemovedApp a) return;
        try
        {
            Process.Start(new ProcessStartInfo(Apps.StoreUri(a)) { UseShellExecute = true });
            ShowToast($"Se abre la Microsoft Store: pulsa «Obtener» para volver a instalar {a.Name}.");
        }
        catch (Exception ex) { ShowToast("No se pudo abrir la Microsoft Store: " + ex.Message); }
    }

    // ---------- Herramientas ----------

    void BuildTools()
    {
        var tools = new (string Glyph, string Color, string Title, string Desc, string Button, Func<string> Action, bool Confirm)[]
        {
            ("\uE74D", "#A78BFA", "Limpiar temporales", "Borra los temporales de tu usuario y de Windows con más de 24 horas.", "Limpiar", SystemTools.CleanTemp, false),
            ("\uE74D", "#F87171", "Vaciar la papelera", "Elimina definitivamente lo que hay en la papelera de reciclaje.", "Vaciar", SystemTools.EmptyRecycleBin, true),
            ("\uE777", "#34D399", "Punto de restauración", "Guarda el estado actual para volver a él desde Restaurar sistema.", "Crear", SystemTools.CreateRestorePoint, false),
            ("\uE774", "#67E8F9", "Vaciar caché DNS", "Soluciona webs que no cargan tras un cambio de DNS.", "Vaciar", SystemTools.FlushDns, false),
            ("\uE701", "#67E8F9", "Restablecer la red", "Reinicia Winsock y TCP/IP si la conexión falla sin motivo. Requiere reiniciar.", "Restablecer", SystemTools.ResetNetwork, true),
            ("\uE72C", "#A78BFA", "Reiniciar Explorador", "Aplica los cambios visuales sin cerrar sesión.", "Reiniciar", SystemTools.RestartExplorer, false),
            ("\uE90F", "#FBBF24", "Reparar sistema (SFC)", "Comprueba y repara archivos de Windows dañados. Tarda de 5 a 20 minutos.", "Ejecutar", SystemTools.RunSfc, true),
            ("\uE90F", "#FBBF24", "Reparar imagen (DISM)", "Repara el almacén de componentes. Úsalo si SFC no pudo reparar algo.", "Ejecutar", SystemTools.RunDism, true),
            ("\uEDA2", "#34D399", "Optimizar unidades", "TRIM en SSD y desfragmentación en discos duros.", "Optimizar", SystemTools.OptimizeDrives, false),
            ("\uE896", "#A78BFA", "Actualizar programas", "Actualiza con winget todos los programas que tengan versión nueva.", "Actualizar", SystemTools.WingetUpgradeAll, true),
        };

        foreach (var t in tools)
        {
            var color = Palette.B(t.Color);
            var bg = Palette.B("#22" + t.Color.TrimStart('#'));
            var card = new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(18, 16, 18, 16) };
            var dock = new DockPanel();

            var btn = new Button { Content = t.Button, MinWidth = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            DockPanel.SetDock(btn, Dock.Right);
            btn.Click += async (_, _) =>
            {
                if (t.Confirm && MessageBox.Show(this, t.Desc + "\n\n¿Continuar?", t.Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                string result = "";
                await RunBusy(t.Title + "…", async _ => result = await Task.Run(t.Action));
                if (result.Length > 0)
                {
                    Log.Write(result);
                    if (result.Contains('\n')) MessageBox.Show(this, result, t.Title);
                    else ShowToast(result);
                }
                if (t.Action == SystemTools.CleanTemp || t.Action == SystemTools.EmptyRecycleBin) LoadSystemInfo();
            };

            var icon = new Border { Style = (Style)FindResource("IconBox"), Background = bg, VerticalAlignment = VerticalAlignment.Center };
            icon.Child = new TextBlock { Text = t.Glyph, Style = (Style)FindResource("Icon"), Foreground = color };
            DockPanel.SetDock(icon, Dock.Left);

            var sp = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            sp.Children.Add(new TextBlock { Text = t.Desc, Foreground = (Brush)FindResource("Sub"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), FontSize = 12 });

            dock.Children.Add(btn);
            dock.Children.Add(icon);
            dock.Children.Add(sp);
            card.Child = dock;
            ToolsGrid.Children.Add(card);
        }
    }

    void BuildShortcuts()
    {
        foreach (var s in Shortcuts.All)
        {
            var btn = new Button { Padding = new Thickness(14, 14, 14, 14), Margin = new Thickness(0, 0, 10, 10), HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = s.Desc };
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(new TextBlock { Text = s.Glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 18, Foreground = Palette.B("#A78BFA"), HorizontalAlignment = HorizontalAlignment.Left });
            sp.Children.Add(new TextBlock { Text = s.Title, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
            btn.Content = sp;
            btn.Click += (_, _) =>
            {
                try { Shortcuts.Open(s.Target); }
                catch (Exception ex) { ShowToast($"No se pudo abrir {s.Title}: {ex.Message}"); }
            };
            ShortcutsGrid.Children.Add(btn);
        }
    }

    // ---------- Diagnóstico ----------

    async void RunDiag_Click(object sender, RoutedEventArgs e)
    {
        List<DiagResult> results = new();
        await RunBusy("Analizando el equipo…", async report =>
        {
            report("Revisando disco, memoria, registros de eventos, drivers y seguridad");
            results = await Task.Run(Diagnostics.Run);
        });
        ApplyDiagResults(results);
    }

    // ---------- Programas de inicio ----------

    async void LoadStartup_Click(object sender, RoutedEventArgs e)
    {
        var list = await Task.Run(Startup.List);
        _startupLoaded = true;
        StartupList.ItemsSource = list;
        StartupSummary.Text = $"{list.Count(x => x.Enabled)} de {list.Count} programas arrancan con Windows";
    }

    void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (((CheckBox)sender).Tag is not StartupEntry entry) return;
        try
        {
            Startup.SetEnabled(entry, entry.Enabled);
            _ = RecordHistoryAsync($"Inicio: {entry.Name} {(entry.Enabled ? "activado" : "desactivado")}", entry.Location);
            ShowToast($"{entry.Name} {(entry.Enabled ? "arrancará" : "ya no arrancará")} con Windows.");
        }
        catch (Exception ex)
        {
            entry.Enabled = !entry.Enabled;
            ShowToast("No se pudo cambiar: " + ex.Message);
        }
        if (StartupList.ItemsSource is List<StartupEntry> l)
            StartupSummary.Text = $"{l.Count(x => x.Enabled)} de {l.Count} programas arrancan con Windows";
    }

    // ---------- Actualizar programas ----------

    async void ScanUpgrades_Click(object sender, RoutedEventArgs e)
    {
        List<UpgradeItem> items = new();
        await RunBusy("Buscando actualizaciones…", async report =>
        {
            report("Consultando winget (puede tardar un poco la primera vez)");
            items = await Task.Run(Winget.ListUpgrades);
        });
        _upgradesScanned = true;
        UpgradesList.ItemsSource = items;
        UpgradesSummary.Text = items.Count == 0 ? "Todos tus programas están al día" : $"{items.Count} programas con versión nueva";
        _insights.UpgradesAt = DateTime.Now;
        _insights.Upgrades = items.Count;
        _insights.Save();
        Log.Write("winget: " + UpgradesSummary.Text);
    }

    async void RunUpgrades_Click(object sender, RoutedEventArgs e)
    {
        var todo = (UpgradesList.ItemsSource as List<UpgradeItem>)?.Where(i => i.IsSelected).ToList() ?? new();
        if (todo.Count == 0) { ShowToast("No hay programas seleccionados."); return; }
        int ok = 0;
        await RunBusy("Actualizando programas…", async report =>
        {
            foreach (var (item, i) in todo.Select((x, i) => (x, i)))
            {
                report($"Actualizando {item.Name}");
                BusyProgress(i, todo.Count, "programas");
                var msg = await Task.Run(() => Winget.Upgrade(item));
                Log.Write(msg);
                bool done = msg.StartsWith("Actualizado");
                if (done) ok++;
                BusyLogAdd(item.Name, done, done ? "actualizado" : "");
                BusyProgress(i + 1, todo.Count, "programas");
            }
        });
        ShowToast($"{ok} de {todo.Count} programas actualizados.");
        ScanUpgrades_Click(sender, e);
    }

    // ---------- Perfiles propios ----------

    void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameBox.Text.Trim();
        var ids = _all.Where(v => v.IsSelected).Select(v => v.T.Id).ToList();
        if (name.Length == 0) { ShowToast("Escribe un nombre para el perfil."); return; }
        if (ids.Count == 0) { ShowToast("Marca primero los ajustes que quieres guardar en el perfil."); return; }
        var existing = _profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) _profiles.Remove(existing);
        _profiles.Add(new UserProfile { Name = name, Tweaks = ids });
        Profiles.Save(_profiles.ToList());
        ProfileNameBox.Text = "";
        ShowToast($"Perfil «{name}» guardado con {ids.Count} ajustes.");
    }

    async void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not UserProfile p) return;
        _settings.LastProfile = p.Name;
        _settings.Save();
        var ids = p.Tweaks.ToHashSet();
        var set = _all.Where(v => ids.Contains(v.T.Id) && v.IsAvailable).ToList();
        var todo = set.Where(v => !v.IsApplied).ToList();
        await PreviewAndRunAsync($"Perfil «{p.Name}»", todo, set.Count - todo.Count);
    }

    async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not UserProfile p) return;
        var applied = _all.Where(v => p.Tweaks.Contains(v.T.Id) && v.CanUndo).ToList();
        var msg = $"¿Eliminar el perfil «{p.Name}»?";
        if (applied.Count > 0) msg += $"\n\nSí = eliminar y deshacer sus {applied.Count} ajustes aplicados.\nNo = eliminar solo el perfil.";
        var res = MessageBox.Show(this, msg, "Eliminar perfil",
            applied.Count > 0 ? MessageBoxButton.YesNoCancel : MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (res is MessageBoxResult.Cancel or MessageBoxResult.None) return;
        _profiles.Remove(p);
        Profiles.Save(_profiles.ToList());
        if (res == MessageBoxResult.Yes && applied.Count > 0) await RevertMany(applied);
        else ShowToast($"Perfil «{p.Name}» eliminado.");
    }

    // ---------- Historial de cambios ----------

    static string Names(List<TweakVM> list) =>
        string.Join(", ", list.Take(4).Select(v => v.Title)) + (list.Count > 4 ? $" y {list.Count - 4} más" : "");

    /// <summary>Primer arranque: crea «Antes de usar WinRaptor» y, si ya había cambios, una foto del estado actual.</summary>
    void InitHistory()
    {
        if (_history.Count > 0) return;
        var startup = History.StartupSnapshot();
        _history.Add(new HistoryEntry
        {
            Title = "Antes de usar WinRaptor", Detail = "Ningún ajuste aplicado por WinRaptor.",
            At = _engine.State.Applied.Values.Select(a => a.At).DefaultIfEmpty(DateTime.Now).Min().AddSeconds(-1),
            Startup = startup
        });
        var applied = _engine.AppliedIds();
        if (applied.Count > 0)
        {
            _history.Add(new HistoryEntry
            {
                Title = "Ajustes ya aplicados", Detail = $"{applied.Count} ajustes de sesiones anteriores.",
                Applied = applied, Startup = startup, UndoTarget = _history[0].Id
            });
        }
        History.Save(_history);
    }

    async Task RecordHistoryAsync(string title, string detail, string? undoTarget = null)
    {
        var applied = _engine.AppliedIds();
        var startup = await Task.Run(History.StartupSnapshot);
        var prev = _history.LastOrDefault();
        if (prev is not null)
        {
            int added = applied.Except(prev.Applied).Count(), removed = prev.Applied.Except(applied).Count();
            int sChanged = startup.Count(kv => prev.Startup.TryGetValue(kv.Key, out var was) && was != kv.Value);
            if (added == 0 && removed == 0 && sChanged == 0) return; // nada cambió de verdad
            var parts = new List<string>();
            if (added > 0) parts.Add($"+{added} ajustes");
            if (removed > 0) parts.Add($"−{removed} ajustes");
            if (sChanged > 0) parts.Add($"{sChanged} programas de inicio");
            detail = $"{string.Join(" · ", parts)} · {applied.Count} activos con WinRaptor" + (detail.Length > 0 ? $"\n{detail}" : "");
        }
        _history.Add(new HistoryEntry
        {
            Title = title, Detail = detail, Applied = applied, Startup = startup,
            UndoTarget = undoTarget ?? prev?.Id
        });
        History.Save(_history);
        ShowHistory();
    }

    void ShowHistory()
    {
        var list = _history.Select((h, i) => new HistoryVM(h, i == _history.Count - 1)).Reverse().ToList();
        HistoryList.ItemsSource = list;
        HistorySummary.Text = _history.Count <= 1
            ? "Aún no hay cambios. Cada vez que apliques o deshagas algo aparecerá aquí."
            : $"{_history.Count} puntos guardados · el más reciente arriba";
    }

    void RestoreHistory_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is HistoryVM vm) _ = RestoreToAsync(vm.E);
    }

    void UndoLast_Click(object sender, RoutedEventArgs e)
    {
        var current = _history.LastOrDefault();
        var target = current?.UndoTarget is { } id ? _history.FirstOrDefault(h => h.Id == id) : null;
        if (target is null) { ShowToast("No hay ningún cambio que deshacer."); return; }
        _ = RestoreToAsync(target);
    }

    /// <summary>Deja los ajustes y los programas de inicio exactamente como estaban en <paramref name="target"/>.</summary>
    async Task RestoreToAsync(HistoryEntry target)
    {
        var current = _engine.AppliedIds();
        var wanted = target.Applied.ToHashSet();
        var byId = _all.ToDictionary(v => v.T.Id);
        // Se deshace de lo más reciente a lo más antiguo, igual que un historial real.
        var toRevert = current.Where(id => !wanted.Contains(id) && byId.ContainsKey(id)).Reverse().Select(id => byId[id]).ToList();
        var toApply = target.Applied.Where(id => !current.Contains(id) && byId.ContainsKey(id)).Select(id => byId[id]).ToList();

        int startupChanges = 0, failed = 0;
        var okList = new List<TweakVM>();
        await RunBusy($"Volviendo a «{target.Title}»", async report =>
        {
            var steps = toRevert.Select(v => (v, false)).Concat(toApply.Select(v => (v, true))).ToList();
            if (steps.Count > 0) okList = await RunTweakSteps(steps, report);
            failed = steps.Count - okList.Count;
            report("Programas de inicio");
            startupChanges = await Task.Run(() =>
            {
                int n = 0;
                foreach (var s in Startup.List())
                    if (target.Startup.TryGetValue(History.StartupKey(s), out var en) && en != s.Enabled) { Startup.SetEnabled(s, en); n++; }
                return n;
            });
            if (steps.Count + startupChanges > 0)
                await BusyFinish("Listo", $"El equipo está como en «{target.Title}»", failed == 0);
        });

        await RefreshStatusAsync();
        _startupLoaded = false;
        if (_section == "arranque") LoadStartup_Click(this, new RoutedEventArgs());

        if (toRevert.Count + toApply.Count + startupChanges == 0)
        {
            ShowToast("Ya estabas en ese punto: no había nada que cambiar.");
            return;
        }
        // Desde la vuelta atrás, «Deshacer último» sigue retrocediendo desde el punto de destino.
        await RecordHistoryAsync($"Vuelta a «{target.Title}» ({target.At:HH:mm})",
            $"{toRevert.Count} deshechos, {toApply.Count} reaplicados, {startupChanges} programas de inicio", target.UndoTarget);
        Log.Write($"Historial: vuelta a «{target.Title}» ({toRevert.Count} deshechos, {toApply.Count} reaplicados, {startupChanges} inicio).");
        ShowToast($"Listo: el equipo está como en «{target.Title}». " +
                  (failed > 0 ? $"{failed} con errores (ver Actividad). " : ""));
        NoteReboot(okList);
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Log.FilePath)) Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    // ---------- Biblioteca de juegos ----------

    bool _libLoaded;

    List<GameCardVM> _libCards = new();
    string _libFilter = "Todos";
    GameCardVM? _featured;

    async Task LoadLibraryAsync()
    {
        GameLibHint.Text = "Buscando tus juegos…";
        List<GameEntry> games;
        try { games = await Task.Run(GameLibrary.Scan); }
        catch (Exception ex) { GameLibHint.Text = "No se pudo leer tu biblioteca: " + ex.Message; return; }
        _libLoaded = true;
        _libCards = games.Select(g => new GameCardVM(g)).ToList();
        GameLibHint.Text = games.Count == 0
            ? "No he encontrado juegos de Steam, Epic ni de la Microsoft Store. Usa «Añadir juego» para los demás."
            : $"{games.Count} {(games.Count == 1 ? "juego instalado" : "juegos instalados")} · pasa el ratón por uno para verlo arriba y púlsalo para jugar";
        if (_libFilter != "Todos" && !_libCards.Any(c => c.Source == _libFilter)) _libFilter = "Todos";
        BuildFilterBar();
        ApplyLibraryFilter();
        SetFeatured(_libCards.FirstOrDefault(), animate: false);
    }

    void BuildFilterBar()
    {
        GameFilterBar.Children.Clear();
        var groups = new List<(string Name, int Count)> { ("Todos", _libCards.Count) };
        groups.AddRange(_libCards.GroupBy(c => c.Source).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count())));
        foreach (var (name, count) in groups)
        {
            bool on = name == _libFilter;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new Border
            {
                Background = on ? Palette.B("#33FFFFFF") : Palette.B("#1AFFFFFF"), CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(8, 0, 0, 0), Child = new TextBlock { Text = count.ToString(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
            });
            var b = new Button
            {
                Content = sp, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), FontSize = 12, Tag = name,
                Background = on ? (Brush)FindResource("AccentSoft") : Palette.B("#141821"),
                BorderBrush = on ? (Brush)FindResource("Accent") : (Brush)FindResource("Line"),
                Foreground = on ? Palette.B("#E9E3FF") : (Brush)FindResource("Sub")
            };
            b.Click += (s, _) => { _libFilter = (string)((Button)s!).Tag; BuildFilterBar(); ApplyLibraryFilter(); };
            GameFilterBar.Children.Add(b);
        }
        GameFilterBar.Visibility = _libCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ApplyLibraryFilter()
    {
        var q = GameSearch.Text.Trim();
        var list = _libCards.Where(c => (_libFilter == "Todos" || c.Source == _libFilter)
                                        && (q.Length == 0 || c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase))).ToList();
        GameLibraryList.ItemsSource = list;
        GameNoResults.Visibility = list.Count == 0 && _libCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void GameSearch_Changed(object sender, TextChangedEventArgs e)
    {
        GameSearchHint.Visibility = GameSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyLibraryFilter();
    }

    /// <summary>Pone un juego en el destacado de arriba, con su imagen de fondo y su logo.</summary>
    void SetFeatured(GameCardVM? c, bool animate = true)
    {
        if (c is null)
        {
            FeatLabel.Text = "TU BIBLIOTECA";
            FeatTitle.Text = "Añade tus juegos";
            FeatTitle.Visibility = Visibility.Visible;
            FeatLogo.Visibility = Visibility.Collapsed;
            FeatMeta.Text = "Aparecen solos los de Steam, Epic y la Microsoft Store.";
            FeatPlay.Visibility = FeatOpen.Visibility = Visibility.Collapsed;
            FeatBg.Background = new LinearGradientBrush(Color.FromRgb(0x25, 0x1B, 0x4D), Color.FromRgb(0x0A, 0x2E, 0x3A), 45);
            return;
        }
        if (c == _featured && animate) return;
        _featured = c;
        bool recent = c == _libCards.FirstOrDefault() && c.G.LastPlayed is not null;
        FeatLabel.Text = recent ? "SEGUIR JUGANDO" : c.Source.ToUpperInvariant();
        FeatTitle.Text = c.Title;
        FeatTitle.Visibility = c.TitleVisibility;
        FeatLogo.Source = c.LogoImage;
        FeatLogo.Visibility = c.LogoVisibility;
        FeatMeta.Text = c.MetaText;
        FeatPlay.Visibility = FeatOpen.Visibility = Visibility.Visible;
        FeatPlay.Tag = FeatOpen.Tag = c;
        FeatBg.Background = c.HeroBrush;
        if (animate)
        {
            FeatBg.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Motion.Enter(FeatInfo, 8, 0, 320);
        }
    }

    void GameCard_Hover(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is GameCardVM c) SetFeatured(c);
    }

    async void GameCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is GameCardVM c) await PlayGameAsync(c, GameAutoMode.IsChecked == true);
    }

    async void FeatPlay_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is GameCardVM c) await PlayGameAsync(c, GameAutoMode.IsChecked == true);
    }

    async void FeatOpen_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is GameCardVM c) await PlayGameAsync(c, false);
    }

    async Task PlayGameAsync(GameCardVM c, bool withGameMode)
    {
        if (withGameMode && _game?.Active != true && !await ActivateGameModeAsync()) return;
        try
        {
            GameLibrary.Launch(c.G);
            Log.Write($"Jugar: {c.Title} ({c.Source})");
            ShowToast($"Abriendo {c.Title}…" + (_game?.Active == true ? " El modo juego está activo: al terminar, pulsa «Terminar y restaurar»." : ""));
        }
        catch (Exception ex) { ShowToast("No se pudo abrir el juego: " + ex.Message); }
    }

    void GameOptToggle_Click(object sender, RoutedEventArgs e)
    {
        bool open = GameOptBox.Visibility != Visibility.Visible;
        GameOptBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        GameOptToggleText.Text = open ? "Ocultar opciones" : "Personalizar";
        GameOptToggleIcon.Text = open ? "" : "";
        if (open) Motion.StaggerChildren(GameOptBox, 60, 12);
    }

    void GameOpt_Changed(object sender, RoutedEventArgs e) => UpdateGameOptSummary();

    void UpdateGameOptSummary()
    {
        int apps = _gameApps.Count(a => a.IsSelected), svcs = _gameSvcs.Count(s => s.IsSelected && s.Running);
        var parts = new List<string>
        {
            apps == 0 ? "no cerrará apps" : $"cerrará {apps} {(apps == 1 ? "app" : "apps")}",
            svcs == 0 ? "no hay tareas de fondo que pausar" : $"pausará {svcs} {(svcs == 1 ? "tarea" : "tareas")} de fondo"
        };
        if (GamePower.IsChecked == true && GamePower.IsEnabled && !GamePowerSub.Text.StartsWith("Ya usas")) parts.Add("máximo rendimiento");
        if (GameMem.IsChecked == true) parts.Add("liberará memoria");
        if (GameSilence.IsChecked == true && !GameSilenceSub.Text.StartsWith("Ya tienes")) parts.Add("silenciará notificaciones");
        if (GameTimer.IsChecked == true && GameTimer.IsEnabled) parts.Add("temporizador de 0,5 ms");
        var s = string.Join(" · ", parts);
        GameOptSummary.Text = char.ToUpper(s[0]) + s[1..] + ".";
    }

    async void GameAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Elige el juego (su .exe o su acceso directo)",
            Filter = "Juegos (*.exe;*.lnk)|*.exe;*.lnk",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dlg.ShowDialog(this) != true) return;
        var exe = GameLibrary.ResolveShortcut(dlg.FileName);
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) { ShowToast("Ese acceso directo no lleva a un programa (.exe)."); return; }
        string title = Path.GetFileNameWithoutExtension(dlg.FileName).Replace(" - Acceso directo", "");
        try
        {
            var d = FileVersionInfo.GetVersionInfo(exe).FileDescription;
            if (!string.IsNullOrWhiteSpace(d) && dlg.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) title = d!.Trim();
        }
        catch { }
        GameLibrary.AddCustom(title, exe);
        ShowToast($"«{title}» añadido a tus juegos.");
        await LoadLibraryAsync();
    }

    async void GameRemove_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not GameCardVM c || !c.Custom) return;
        GameLibrary.RemoveCustom(c.G.Launch);
        ShowToast($"«{c.Title}» quitado de la lista (el juego no se desinstala).");
        await LoadLibraryAsync();
    }

    // ---------- Modo juego ----------

    GameSession? _game = GameSession.Load();
    bool _gameDetected, _closingAfterGame;
    List<GameAppVM> _gameApps = new();
    List<GameServiceVM> _gameSvcs = new();

    /// <summary>Lee qué hay abierto y en marcha (no cambia nada).</summary>
    async Task GameDetectAsync()
    {
        GameAppsEmpty.Text = "Detectando apps abiertas…";
        GameAppsEmpty.Visibility = Visibility.Visible;
        var (apps, svcs, plans, timer, toastOn) = await Task.Run(() =>
        {
            bool toast = true;
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
                toast = k?.GetValue("ToastEnabled") is not int v || v != 0;
            }
            catch { }
            return (GameMode.DetectApps(), GameMode.DetectServices(), GameMode.PowerPlans(), GameMode.GlobalTimerEnabled(), toast);
        });
        _gameApps = apps;
        _gameSvcs = svcs;
        _gameDetected = true;
        GameApps.ItemsSource = apps;
        GameServices.ItemsSource = svcs;
        GameAppsEmpty.Text = "No hay apps innecesarias abiertas ahora mismo.";
        GameAppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (plans.Best is null) { GamePower.IsChecked = false; GamePower.IsEnabled = false; GamePowerSub.Text = "No hay un plan de alto rendimiento en este equipo."; }
        else if (plans.Best == plans.Active) GamePowerSub.Text = $"Ya usas «{plans.ActiveName}»: no hace falta cambiarlo.";
        else GamePowerSub.Text = $"Cambia de «{plans.ActiveName}» a «{plans.BestName}» mientras juegas.";

        GameTimer.IsEnabled = timer;
        if (!timer) GameTimer.IsChecked = false;
        GameTimerSub.Text = timer
            ? "Pide a Windows un temporizador de 0,5 ms: entradas y frames más regulares."
            : "Necesita el ajuste «Resolución de temporizador global» (sección Rendimiento) y reiniciar.";
        GameSilenceSub.Text = toastOn ? "Nada de avisos a mitad de partida." : "Ya tienes las notificaciones desactivadas.";
        foreach (var a in apps) a.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(GameAppVM.IsSelected)) UpdateGameOptSummary(); };
        foreach (var sv in svcs) sv.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(GameServiceVM.IsSelected)) UpdateGameOptSummary(); };
        UpdateGameOptSummary();
    }

    async void GameDetect_Click(object sender, RoutedEventArgs e) => await GameDetectAsync();

    async void GameStart_Click(object sender, RoutedEventArgs e) => await ActivateGameModeAsync();

    /// <summary>Activa el modo juego con las opciones elegidas. Devuelve false si el usuario lo cancela.</summary>
    async Task<bool> ActivateGameModeAsync()
    {
        if (_game?.Active == true) return true;
        if (!_gameDetected) await GameDetectAsync();
        var close = _gameApps.Where(a => a.IsSelected).ToList();
        var svcs = _gameSvcs.Where(s => s.IsSelected && s.Running).ToList();
        var windows = close.Where(a => !a.Background).Select(a => a.Name).ToList();
        if (windows.Count > 0 && MessageBox.Show(this,
                $"Se cerrarán estas ventanas: {string.Join(", ", windows)}.\n\nGuarda lo que tengas abierto en ellas. ¿Continuar?",
                "Modo juego", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;

        var opts = new GameOptions(close, svcs, GamePower.IsChecked == true, GameMem.IsChecked == true,
            GameSilence.IsChecked == true, GameTimer.IsChecked == true && GameTimer.IsEnabled, GameReopen.IsChecked == true);
        int total = close.Count + svcs.Count + (opts.PowerPlan ? 1 : 0) + (opts.FreeMemory ? 1 : 0) + (opts.Silence ? 1 : 0) + (opts.Timer ? 1 : 0);
        GameSession? s = null;
        await RunBusy("Preparando tu PC para jugar", async report =>
        {
            int done = 0;
            BusyProgress(0, Math.Max(1, total), "pasos");
            s = await Task.Run(() => GameMode.Start(opts, (text, ok) => Dispatcher.Invoke(() =>
            {
                report(text);
                BusyLogAdd(text, ok);
                BusyProgress(++done, Math.Max(1, total), "pasos");
            })));
            await BusyFinish("¡Listo para jugar!", $"{s.ClosedNames.Count} apps cerradas · {s.StoppedServices.Count} tareas pausadas · {s.FreedMb:0} MB liberados", true);
        });
        if (s is null) return false;
        _game = s;
        Log.Write($"Modo juego activado: {s.ClosedNames.Count} apps cerradas, {s.StoppedServices.Count} servicios pausados, {s.FreedMb:0} MB liberados.");
        UpdateGameUI();
        ShowToast("Modo juego activo. Cuando termines de jugar, vuelve aquí y pulsa «Terminar y restaurar».");
        return true;
    }

    async void GameEnd_Click(object sender, RoutedEventArgs e) => await EndGameAsync();

    async Task EndGameAsync()
    {
        if (_game is not { } s) return;
        var played = DateTime.Now - s.StartedAt;
        int total = (s.Timer ? 1 : 0) + (s.PrevPowerScheme is null ? 0 : 1) + (s.ToastsChanged ? 1 : 0) + s.StoppedServices.Count + (s.Reopen ? s.ClosedPaths.Distinct().Count() : 0);
        await RunBusy("Dejando tu PC como estaba", async report =>
        {
            int done = 0;
            BusyProgress(0, Math.Max(1, total), "pasos");
            await Task.Run(() => GameMode.End(s, (text, ok) => Dispatcher.Invoke(() =>
            {
                report(text);
                BusyLogAdd(text, ok);
                BusyProgress(++done, Math.Max(1, total), "pasos");
            })));
            await BusyFinish("Todo como estaba", $"Sesión de juego de {(played.TotalHours >= 1 ? $"{(int)played.TotalHours} h {played.Minutes} min" : $"{Math.Max(1, (int)played.TotalMinutes)} min")}", true);
        });
        _game = null;
        Log.Write($"Modo juego terminado tras {played.TotalMinutes:0} min: todo restaurado.");
        UpdateGameUI();
        _gameDetected = false;
        if (_section == "juego") _ = GameDetectAsync();
    }

    void UpdateGameUI()
    {
        bool on = _game?.Active == true;
        GameStartBtn.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        GameEndBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        GameRedetect.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        GameOptions.IsEnabled = !on;
        GameOptions.Opacity = on ? 0.5 : 1;
        GameDot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        GameStats.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        GameChip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        GameHero.BorderBrush = Palette.B(on ? "#8034D399" : "#33FFFFFF");
        GameHero.Background = Palette.B(on ? "#D90C2A21" : "#B30E1220");
        if (on && _game is { } s)
        {
            GameState.Text = "MODO JUEGO ACTIVO";
            GameState.Foreground = Palette.BasicFg;
            GameTitle.Text = $"Activo desde las {s.StartedAt:HH:mm}";
            GameSub.Text = "Al terminar, pulsa «Terminar y restaurar». Si cierras WinRaptor, lo restaura solo.";
            GameStatApps.Text = $"{s.ClosedNames.Count} apps cerradas";
            GameStatSvc.Text = $"{s.StoppedServices.Count} tareas pausadas";
            GameStatRam.Text = $"{s.FreedMb:0} MB liberados";
            GameStatPower.Text = s.PowerSchemeName is { } p ? p : "Máximo rendimiento";
        }
        else
        {
            GameState.Text = "MODO JUEGO";
            GameState.Foreground = Palette.B("#A5B4FC");
            GameTitle.Text = "Listo para activar";
            GameSub.Text = "Cierra lo que sobra, pausa tareas de fondo y pone tu PC al máximo. Al terminar, todo vuelve a como estaba.";
        }
    }

    void GameChip_Click(object sender, RoutedEventArgs e) => NavList.SelectedItem = _nav.FirstOrDefault(n => n.Key == "juego");

    // ---------- Consejos ----------

    readonly Insights _insights = Insights.Load();

    public static readonly DependencyProperty TipScoreProperty = DependencyProperty.Register(nameof(TipScore), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, e) => ((MainWindow)d).OnTipScore((double)e.NewValue)));
    public double TipScore { get => (double)GetValue(TipScoreProperty); set => SetValue(TipScoreProperty, value); }

    void OnTipScore(double v)
    {
        TipsScore.Text = $"{v:0}";
        double frac = Math.Clamp(v / 100, 0, 0.9999);
        const double r = 55, c = 60;
        double a = frac * 2 * Math.PI;
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true));
        TipsArc.Data = new PathGeometry(new[] { fig });
    }

    async Task<AdvisorInput> GatherAdvisorInputAsync()
    {
        int startup = await Task.Run(() => { try { return Startup.List().Count(x => x.Enabled); } catch { return 0; } });
        double freePct = 100, freeGb = 0;
        try
        {
            var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            freePct = 100.0 * sys.AvailableFreeSpace / sys.TotalSize;
            freeGb = sys.AvailableFreeSpace / 1073741824.0;
        }
        catch { }
        var (ram, _, _) = LiveStats.Ram();
        var (pname, pkey, cov) = DetectProfile();
        int lvl = pkey switch { "basic" => 1, "medium" => 2, "high" => 3, _ => 0 };
        int missing = lvl > 0 ? _all.Count(v => v.T.Rank <= lvl && v.IsAvailable && !v.IsApplied) : 0;
        int next = pkey == "none" ? 1 : lvl < 3 && lvl > 0 ? lvl + 1 : 0;
        int nextMissing = next > 0 ? _all.Count(v => v.T.Rank <= next && v.IsAvailable && !v.IsApplied) : 0;
        string nextName = next switch { 1 => "Básico", 2 => "Medio", 3 => "Avanzado", _ => "" };
        return new AdvisorInput(Environment.TickCount64 / 86_400_000.0, freePct, freeGb, ram, startup,
            _settings.RebootPendingSince is not null, _settings.RebootReasons.Count, pname, pkey, cov,
            nextMissing, nextName, missing, _all.Count, _all.Count(v => v.IsApplied));
    }

    /// <summary>Recalcula los consejos con lo ya guardado y lecturas rápidas (no lanza ningún análisis).</summary>
    async Task ShowTipsAsync()
    {
        var input = await GatherAdvisorInputAsync();
        var tips = Advisor.Build(input, _insights);
        TipsList.ItemsSource = tips;
        HabitsList.ItemsSource = Advisor.Habits(input, _insights);

        int bad = tips.Count(x => x.Level == Severity.Bad), warn = tips.Count(x => x.Level == Severity.Warn), info = tips.Count(x => x.Level == Severity.Info);
        int score = Advisor.Score(tips);
        TipsBad.Text = $"{bad} {(bad == 1 ? "importante" : "importantes")}";
        TipsWarn.Text = $"{warn} {(warn == 1 ? "recomendado" : "recomendados")}";
        TipsInfo.Text = $"{info} {(info == 1 ? "sugerencia" : "sugerencias")}";
        TipsTitle.Text = bad > 0 ? "Hay cosas importantes que arreglar"
            : warn > 0 ? "Tu PC va bien, pero puede ir mejor"
            : score >= 95 ? "Tu PC está en plena forma" : "Tu PC está en buena forma";
        TipsSummary.Text = bad + warn == 0
            ? "No hay nada urgente. Revisa las sugerencias cuando quieras."
            : $"Empieza por {(bad > 0 ? "lo importante" : "lo recomendado")}: cada consejo tiene un botón que te lleva directamente a resolverlo.";
        TipsAllGood.Visibility = bad + warn == 0 ? Visibility.Visible : Visibility.Collapsed;
        TipsCheckedAt.Text = _insights.CheckedAt is { } c ? $"Última revisión: {c:dd/MM HH:mm}" : "Aún sin revisión completa";
        int hidden = _insights.Dismissed.Count;
        TipsRestoreHidden.Content = hidden > 0 ? $"Mostrar {hidden} {(hidden == 1 ? "oculto" : "ocultos")}" : "";
        TipsRestoreHidden.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;

        var (col, glow) = score >= 80 ? (Palette.BasicFg, Color.FromRgb(0x34, 0xD3, 0x99))
                        : score >= 60 ? (Palette.MediumFg, Color.FromRgb(0xFB, 0xBF, 0x24))
                        : (Palette.HighFg, Color.FromRgb(0xF8, 0x71, 0x71));
        TipsArc.Stroke = col;
        TipsArcGlow.Color = glow;
        BeginAnimation(TipScoreProperty, new DoubleAnimation(0, score, TimeSpan.FromMilliseconds(1300)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        Motion.StaggerChildren(TipsStack, 50, 16);
    }

    async void TipsCheck_Click(object sender, RoutedEventArgs e)
    {
        List<DiagResult> diag = new();
        await RunBusy("Revisando tu PC…", async report =>
        {
            BusyProgress(0, 2, "comprobaciones");
            report("Diagnóstico: disco, memoria, errores del sistema, drivers y seguridad");
            diag = await Task.Run(Diagnostics.Run);
            BusyLogAdd("Diagnóstico del sistema", true);
            BusyProgress(1, 2, "comprobaciones");
            report("Buscando tu último punto de restauración");
            var rp = await Task.Run(SystemTools.LastRestorePoint);
            _insights.RestorePointAt = rp;
            _insights.RestoreCheckedAt = DateTime.Now;
            BusyLogAdd("Punto de restauración", true);
            BusyProgress(2, 2, "comprobaciones");
        });
        ApplyDiagResults(diag);
        _insights.CheckedAt = DateTime.Now;
        _insights.Save();
        await ShowTipsAsync();
        ShowToast("Revisión terminada. Los consejos están actualizados.");
    }

    async void TipAction_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Tip tip) return;
        var a = tip.Action;
        if (a.StartsWith("nav:")) { NavList.SelectedItem = _nav.FirstOrDefault(n => n.Key == a[4..]); return; }
        if (a.StartsWith("analysis:"))
        {
            var k = a[9..]; var sec = SectionOfAnalysis(k);
            NavList.SelectedItem = _nav.FirstOrDefault(n => n.Key == sec);
            OpenAnalysis(k, sec);
            return;
        }
        switch (a)
        {
            case "reboot":
                ShowRebootDialog();
                break;
            case "tool:clean":
            {
                string msg = "";
                await RunBusy("Limpiando temporales…", async _ => msg = await Task.Run(SystemTools.CleanTemp));
                Log.Write(msg);
                ShowToast(msg.Contains('\n') ? msg.Split('\n')[0] : msg);
                LoadSystemInfo();
                await ShowTipsAsync();
                break;
            }
            case "tool:restore":
            {
                string msg = "";
                await RunBusy("Creando punto de restauración…", async _ => msg = await Task.Run(SystemTools.CreateRestorePoint));
                Log.Write(msg);
                ShowToast(msg);
                _insights.RestorePointAt = await Task.Run(SystemTools.LastRestorePoint);
                _insights.RestoreCheckedAt = DateTime.Now;
                _insights.Save();
                await ShowTipsAsync();
                break;
            }
        }
    }

    async void TipDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Tip tip) return;
        if (!_insights.Dismissed.Contains(tip.Id)) _insights.Dismissed.Add(tip.Id);
        _insights.Save();
        await ShowTipsAsync();
    }

    async void TipsShowHidden_Click(object sender, RoutedEventArgs e)
    {
        _insights.Dismissed.Clear();
        _insights.Save();
        await ShowTipsAsync();
    }

    void ApplyDiagResults(List<DiagResult> results)
    {
        _diagRan = true;
        DiagList.ItemsSource = results.Select(r => new DiagVM(r)).ToList();
        int bad = results.Count(r => r.Level == Severity.Bad), warn = results.Count(r => r.Level == Severity.Warn);
        DiagSummary.Text = bad + warn == 0
            ? $"Todo en orden · {results.Count} comprobaciones"
            : $"{bad} problemas · {warn} avisos · {results.Count} comprobaciones";
        Log.Write("Diagnóstico: " + DiagSummary.Text);
        _insights.DiagAt = DateTime.Now;
        _insights.Diag = results;
        _insights.Save();
    }

    // ---------- Drivers ----------

    bool _driversLoaded, _drvSearching;
    List<DriverUpdate> _drvUpdates = new();
    List<DriverInfo> _drvThird = new();
    List<DeviceProblem> _drvProblems = new();

    async void DriverScan_Click(object sender, RoutedEventArgs e) => await LoadDriversAsync(searchUpdates: true);

    async Task LoadDriversAsync(bool searchUpdates)
    {
        (List<DriverInfo> Third, int Total, List<DeviceProblem> Problems)? inv = null;
        await RunBusy("Revisando tus drivers…", async report =>
        {
            report("Leyendo los drivers instalados y el estado de cada dispositivo");
            inv = await Task.Run(Drivers.Inventory);
        });
        if (inv is not { } r) return;
        _driversLoaded = true;
        _drvThird = r.Third;
        _drvProblems = r.Problems;
        _insights.DriversAt = DateTime.Now;
        _insights.DriverProblems = r.Problems.Count;
        _insights.DriversOld = r.Third.Count(d => d.IsOld);
        _insights.Save();
        DrvList.ItemsSource = r.Third;
        DrvProblems.ItemsSource = r.Problems;
        DrvProblemsBox.Visibility = r.Problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        int old = r.Third.Count(d => d.IsOld);
        DrvKpiTotal.Text = r.Third.Count.ToString();
        DrvKpiTotalSub.Text = $"de fabricantes · {r.Total} drivers en total";
        DrvKpiProblems.Text = r.Problems.Count.ToString();
        DrvKpiProblems.Foreground = r.Problems.Count > 0 ? Palette.HighFg : Palette.BasicFg;
        DrvKpiOld.Text = old.ToString();
        DrvKpiOld.Foreground = old > 0 ? Palette.MediumFg : Palette.BasicFg;
        DrvListHint.Text = old > 0 ? $"{old} con más de 3 años: si Windows Update no los actualiza, búscalos en la web del fabricante" : "Los de Microsoft se actualizan solos con Windows";
        Log.Write($"Drivers: {r.Third.Count} de fabricantes, {old} antiguos, {r.Problems.Count} dispositivos con problemas.");
        UpdateDriverSummary();
        if (searchUpdates) _ = SearchDriverUpdatesAsync();
    }

    async Task SearchDriverUpdatesAsync()
    {
        if (_drvSearching) return;
        _drvSearching = true;
        DrvSearching.Visibility = Visibility.Visible;
        Motion.Enter(DrvSearching, 10, 0, 300);
        DrvSpinRot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever });
        DrvNoUpdates.Visibility = DrvError.Visibility = Visibility.Collapsed;
        DrvUpdates.ItemsSource = null;
        DriverInstallBtn.IsEnabled = false;
        DrvKpiUpdates.Text = "…";
        DrvKpiUpdates.Foreground = (Brush)FindResource("Text");
        var sw = Stopwatch.StartNew();
        try
        {
            var list = await Task.Run(Drivers.SearchUpdates);
            _drvUpdates = list;
            _insights.DriverUpdates = list.Count(u => !u.Older);
            _insights.Save();
            DrvUpdates.ItemsSource = list;
            DrvNoUpdates.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DrvKpiUpdates.Text = list.Count.ToString();
            DrvKpiUpdates.Foreground = list.Count > 0 ? Palette.MediumFg : Palette.BasicFg;
            foreach (var u in list) u.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(DriverUpdate.IsSelected)) UpdateDriverInstallButton(); };
            UpdateDriverInstallButton();
            Log.Write($"Windows Update: {list.Count} drivers nuevos ({sw.Elapsed.TotalSeconds:0} s).");
            if (list.Count > 0 && _section == "drivers")
                ShowToast($"Hay {list.Count} {(list.Count == 1 ? "driver nuevo" : "drivers nuevos")}. Revísalos y pulsa «Instalar seleccionados».");
        }
        catch (Exception ex)
        {
            DrvError.Visibility = Visibility.Visible;
            DrvErrorText.Text = ex.Message;
            DrvKpiUpdates.Text = "?";
            Log.Write("Búsqueda de drivers: " + ex.Message);
        }
        finally
        {
            _drvSearching = false;
            DrvSpinRot.BeginAnimation(RotateTransform.AngleProperty, null);
            DrvSearching.Visibility = Visibility.Collapsed;
            UpdateDriverSummary();
        }
    }

    void UpdateDriverInstallButton()
    {
        int n = _drvUpdates.Count(u => u.IsSelected && u.Status != "Instalado");
        DriverInstallBtn.IsEnabled = n > 0 && !_drvSearching;
        DriverInstallText.Text = n > 0 ? $"Instalar {n} {(n == 1 ? "driver" : "drivers")}" : "Instalar seleccionados";
    }

    void UpdateDriverSummary()
    {
        int upd = _drvUpdates.Count(u => u.Status != "Instalado"), probs = _drvProblems.Count;
        DriverSummary.Text = _drvSearching ? "Consultando Windows Update…"
            : upd + probs == 0 ? "Todo en orden: sin drivers pendientes ni dispositivos con errores."
            : string.Join(" · ", new[] { upd > 0 ? $"{upd} por actualizar" : "", probs > 0 ? $"{probs} con problemas" : "" }.Where(s => s.Length > 0));
    }

    async void DriverInstall_Click(object sender, RoutedEventArgs e)
    {
        var todo = _drvUpdates.Where(u => u.IsSelected && u.Status != "Instalado").ToList();
        if (todo.Count == 0) { ShowToast("Marca los drivers que quieres instalar."); return; }
        var msg = $"Se van a descargar e instalar {todo.Count} {(todo.Count == 1 ? "driver oficial" : "drivers oficiales")} desde Windows Update.\n\n" +
                  "Mientras se instalan, la pantalla puede parpadear o la red cortarse unos segundos. ¿Continuar?";
        int olderSel = todo.Count(u => u.Older);
        if (olderSel > 0) msg = $"⚠ {olderSel} de los marcados son MÁS ANTIGUOS que los que ya tienes y podrían empeorar ese dispositivo.\n\n" + msg;
        if (MessageBox.Show(this, msg, "Instalar drivers", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        bool restorePoint = RestorePointCheck.IsChecked == true;
        var byId = todo.ToDictionary(u => u.Id);
        Dictionary<string, bool> results = new();
        bool reboot = false;
        await RunBusy($"Instalando {todo.Count} {(todo.Count == 1 ? "driver" : "drivers")}", async report =>
        {
            if (restorePoint)
            {
                report("Comprobando el punto de restauración…");
                try { Log.Write(await Task.Run(SystemTools.EnsureRecentRestorePoint)); }
                catch (Exception ex) { Log.Write("Punto de restauración: " + ex.Message); }
            }
            int done = 0;
            BusyProgress(0, todo.Count, "drivers");
            foreach (var u in todo) u.Status = "En cola";
            (results, reboot) = await Task.Run(() => Drivers.Install(todo.Select(u => u.Id).ToList(), (step, id) => Dispatcher.Invoke(() =>
            {
                byId.TryGetValue(id ?? "", out var u);
                switch (step)
                {
                    case Drivers.Step.Searching: report("Preparando la descarga desde Windows Update…"); break;
                    case Drivers.Step.Downloading:
                        report("Descargando: " + u?.Title);
                        if (u is not null) { u.Status = "Descargando…"; u.StatusFg = Palette.Native; }
                        break;
                    case Drivers.Step.Installing:
                        report("Instalando: " + u?.Title);
                        if (u is not null) { u.Status = "Instalando…"; u.StatusFg = Palette.MediumFg; }
                        break;
                    case Drivers.Step.Done:
                        BusyProgress(++done, todo.Count, "drivers");
                        break;
                }
            })));
            foreach (var u in todo)
            {
                bool ok = results.TryGetValue(u.Id, out var r) && r;
                u.Status = ok ? "Instalado" : "No se instaló";
                u.StatusFg = ok ? Palette.BasicFg : Palette.HighFg;
                if (ok) u.IsSelected = false;
                BusyLogAdd(u.Title, ok, ok ? "instalado" : "");
            }
            int okCount = results.Count(kv => kv.Value);
            await BusyFinish(okCount == todo.Count ? "Drivers instalados" : $"{okCount} de {todo.Count} instalados",
                reboot ? "Windows necesita reiniciar para terminar" : $"en {_busyClock.Elapsed.TotalSeconds:0} s", okCount == todo.Count);
        });

        int installed = results.Count(kv => kv.Value);
        UpdateDriverInstallButton();
        UpdateDriverSummary();
        DrvKpiUpdates.Text = _drvUpdates.Count(u => u.Status != "Instalado").ToString();
        _insights.DriverUpdates = _drvUpdates.Count(u => !u.Older && u.Status != "Instalado");
        _insights.Save();
        if (installed > 0)
            await RecordHistoryAsync($"Instalados {installed} drivers", string.Join(", ", todo.Where(u => results.GetValueOrDefault(u.Id)).Take(4).Select(u => u.Title)));
        ShowToast(installed == todo.Count ? $"{installed} {(installed == 1 ? "driver instalado" : "drivers instalados")} correctamente."
            : $"{installed} de {todo.Count} drivers instalados. Los que fallaron puedes reintentarlos o instalarlos desde Windows Update (ver Actividad).");
        if (reboot || installed > 0)
            NoteRebootItems(todo.Where(u => results.GetValueOrDefault(u.Id)).Select(u => ("Driver: " + u.Title, ImpactKind.Kernel)), ask: reboot);
    }

    async void DriverRescan_Click(object sender, RoutedEventArgs e)
    {
        string msg = "";
        await RunBusy("Detectando hardware…", async report =>
        {
            report("Buscando dispositivos nuevos o sin driver");
            msg = await Task.Run(Drivers.Rescan);
        });
        ShowToast(msg);
        await LoadDriversAsync(searchUpdates: false);
    }

    void DriverVendor_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DriverInfo d) return;
        try { Process.Start(new ProcessStartInfo(Drivers.VendorUrl(d)) { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast("No se pudo abrir el navegador: " + ex.Message); }
    }

    // ---------- Antes y después ----------

    List<BootSample>? _boots;

    async void MeasureNow_Click(object sender, RoutedEventArgs e)
    {
        Snapshot? s = null;
        int active = _all.Count(v => v.IsApplied);
        await RunBusy("Midiendo el sistema…", async report =>
        {
            report("Procesos, servicios, memoria y programas de inicio");
            s = await Task.Run(() => Snapshots.Take("Medición manual", active));
        });
        if (s is null) return;
        ShowToast($"Medición guardada: {s.Processes} procesos, {s.Services} servicios, {s.RamGb:0.0} GB de RAM en uso.");
        await ShowBeforeAfterAsync();
    }

    async Task ShowBeforeAfterAsync()
    {
        int total = _all.Count;
        int nowActive = _all.Count(v => v.IsApplied);
        int mine = _all.Count(v => v.CanUndo);
        int beforeActive = nowActive - mine; // lo que Windows ya traía: los aplicados por WinRaptor guardan su valor original
        var first = _engine.State.Applied.Values.Select(a => a.At).DefaultIfEmpty(DateTime.Now).Min();
        if (_history.Count > 0 && _history[0].At < first) first = _history[0].At;

        int startupBefore = _history.Count > 0 ? _history[0].Startup.Count(kv => kv.Value) : -1;
        var (startupNow, snaps) = await Task.Run(() =>
        {
            int n = 0;
            try { n = Startup.List().Count(x => x.Enabled); } catch { }
            return (n, Snapshots.Load());
        });
        _boots ??= await Task.Run(BootTimes.Read);

        // --- Resumen ---
        int days = Math.Max(0, (int)(DateTime.Now - first).TotalDays);
        BASince.Text = days == 0 ? $"Hoy has aplicado {mine} optimizaciones" : $"{days} {(days == 1 ? "día" : "días")} optimizando · {mine} cambios activos";
        BASummary.Text = $"Primer cambio el {first:dd/MM/yyyy} a las {first:HH:mm}. El «antes» sale de los valores originales que WinRaptor guardó antes de tocar cada ajuste.";

        // --- Tarjetas ---
        string Pct(int n) => $"{(total == 0 ? 0 : 100.0 * n / total):0}%";
        var svc = _all.Where(v => v.T.Sec == "servicios" && v.IsAvailable).ToList();
        int svcBefore = svc.Count(v => !v.IsApplied || v.CanUndo), svcNow = svc.Count(v => !v.IsApplied);
        var priv = _all.Where(v => v.T.Sec == "privacidad").ToList();
        int privBefore = priv.Count(v => v.IsApplied && !v.CanUndo), privNow = priv.Count(v => v.IsApplied);

        var kpis = new List<CompareVM>
        {
            new() { Glyph = "", Label = "OPTIMIZACIÓN", Before = Pct(beforeActive), After = Pct(nowActive),
                    Delta = $"+{(total == 0 ? 0 : 100.0 * mine / total):0} pts", Better = mine > 0 ? true : null,
                    Note = $"{beforeActive} → {nowActive} de {total} optimizaciones activas" },
            new() { Glyph = "", Label = "PROGRAMAS DE INICIO",
                    Before = startupBefore < 0 ? "—" : startupBefore.ToString(), After = startupNow.ToString(),
                    Delta = startupBefore < 0 || startupBefore == startupNow ? "" : $"{startupNow - startupBefore:+0;−0}",
                    Better = startupBefore < 0 || startupBefore == startupNow ? null : startupNow < startupBefore,
                    Note = "Programas que arrancan con Windows. Menos = arranque más rápido." },
            new() { Glyph = "", Label = "SERVICIOS INNECESARIOS", Before = svcBefore.ToString(), After = svcNow.ToString(),
                    Delta = svcBefore == svcNow ? "" : $"−{svcBefore - svcNow}", Better = svcBefore == svcNow ? null : true,
                    Note = "Servicios de la lista de WinRaptor que siguen funcionando en segundo plano." },
            new() { Glyph = "", Label = "PRIVACIDAD", Before = $"{privBefore}/{priv.Count}", After = $"{privNow}/{priv.Count}",
                    Delta = privNow == privBefore ? "" : $"+{privNow - privBefore}", Better = privNow == privBefore ? null : true,
                    Note = "Protecciones de privacidad activas (telemetría, anuncios, IA…)." },
        };

        // Arranque de Windows: media antes y después del primer cambio.
        var boots = _boots ?? new();
        var bootBefore = boots.Where(b => b.At < first).ToList();
        var bootAfter = boots.Where(b => b.At >= first).ToList();
        if (bootBefore.Count > 0 && bootAfter.Count > 0)
        {
            double a = bootBefore.TakeLast(10).Average(b => b.Seconds), b2 = bootAfter.Average(b => b.Seconds);
            kpis.Add(new CompareVM
            {
                Glyph = "\uE916", Label = "ARRANQUE DE WINDOWS", Before = $"{a:0.0} s", After = $"{b2:0.0} s",
                Delta = $"{(b2 - a) / a * 100:+0;−0} %", Better = b2 < a,
                Note = $"Media de {Math.Min(10, bootBefore.Count)} arranques antes y {bootAfter.Count} después."
            });
        }
        BAKpis.ItemsSource = kpis;

        // --- Por categoría ---
        var rows = _nav.Where(n => n.Count > 0).Select(n =>
        {
            var list = _all.Where(v => v.T.Sec == n.Key).ToList();
            int b = list.Count(v => v.IsApplied && !v.CanUndo), a = list.Count(v => v.IsApplied);
            return new CompareRow(n.Name, b, a, list.Count, $"{b}", $"{a} de {list.Count}");
        }).ToList();
        BACompare.SetData(rows);

        // --- Arranque (gráfica) ---
        BABootCard.Visibility = boots.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (boots.Count >= 2)
        {
            int split = boots.FindIndex(b => b.At >= first);
            BABoot.SplitAt = split;
            BABoot.SplitLeft = "ANTES";
            BABoot.SplitRight = "CON WINRAPTOR";
            BABoot.Threshold = 0;
            BABoot.SetData(boots.Select(b => b.Seconds).ToList(), true);
            BABootHint.Text = split > 0
                ? $"Cada punto es un arranque registrado por Windows. La zona marcada empieza con tu primer cambio ({first:dd/MM})."
                : "Cada punto es un arranque registrado por Windows. Aún no hay arranques anteriores a WinRaptor en el registro.";
        }

        // --- Mediciones ---
        if (snaps.Count == 0)
        {
            BAMeasureHint.Text = "Aún no hay mediciones. Pulsa «Medir ahora»: WinRaptor también mide solo al abrirse y antes de cada cambio.";
            BAMeasures.ItemsSource = null;
            BAColFirst.Text = BAColLast.Text = "";
        }
        else
        {
            var f = snaps[0];
            var l = snaps[^1];
            BAColFirst.Text = $"PRIMERA · {f.At:dd/MM HH:mm}";
            BAColLast.Text = snaps.Count > 1 ? $"ÚLTIMA · {l.At:dd/MM HH:mm}" : "ÚLTIMA";
            BAMeasureHint.Text = snaps.Count == 1
                ? "Solo hay una medición. Vuelve tras reiniciar o después de aplicar cambios para ver la diferencia."
                : $"{snaps.Count} mediciones guardadas. WinRaptor mide al abrirse y antes de cada cambio; los efectos de muchos ajustes se notan tras reiniciar.";
            static MeasureRowVM R(string label, double a, double b, string fmt, string unit) => new()
            {
                Label = label, First = a.ToString(fmt) + unit, Last = b.ToString(fmt) + unit,
                Delta = Math.Abs(b - a) < 0.005 ? "=" : (b - a).ToString("+" + fmt + ";−" + fmt) + unit,
                Better = Math.Abs(b - a) < 0.005 ? null : b < a
            };
            BAMeasures.ItemsSource = new List<MeasureRowVM>
            {
                R("Procesos en ejecución", f.Processes, l.Processes, "0", ""),
                R("Servicios en ejecución", f.Services, l.Services, "0", ""),
                R("RAM en uso", f.RamGb, l.RamGb, "0.0", " GB"),
                R("Programas de inicio activos", f.Startup, l.Startup, "0", ""),
            };
        }
        Motion.StaggerChildren(BAStack, 50, 16);
    }

    // ---------- Análisis dentro de cada sección ----------

    static readonly Dictionary<string, (string Kind, string Name, string Desc, string Glyph, string Color)[]> SectionAnalyses = new()
    {
        ["red"] = new[] { ("ping", "Latencia de red", "Ping, jitter, pérdida y bufferbloat", "\uE701", "#67E8F9"), ("dns", "DNS más rápido", "Compara 7 DNS y aplica el mejor", "\uE774", "#34D399") },
        ["rendimiento"] = new[] { ("stutter", "Microtirones", "Latencia del sistema y de los drivers", "\uE9D9", "#FBBF24"), ("slow", "¿Por qué va lento?", "Cuellos de botella y qué los causa", "\uEC4A", "#F87171") },
        ["graficos"] = new[] { ("display", "Pantalla y gráficos", "Hz, resolución, monitores y GPU", "\uE7F4", "#A78BFA") },
    };

    static string SectionOfAnalysis(string kind) => kind switch { "ping" or "dns" => "red", "display" => "graficos", _ => "rendimiento" };

    string _analysisSection = "rendimiento";

    void BuildAnalysisStrip(string key)
    {
        AnalysisStripButtons.Children.Clear();
        if (!SectionAnalyses.TryGetValue(key, out var list)) { AnalysisStrip.Visibility = Visibility.Collapsed; return; }
        AnalysisStrip.Visibility = Visibility.Visible;
        AnalysisStripHint.Text = key switch
        {
            "red" => "Mide tu conexión y elige el DNS más rápido antes de tocar nada.",
            "graficos" => "Comprueba tus monitores, los Hz y tu tarjeta gráfica.",
            _ => "Descubre qué frena tu PC y si tiene microtirones."
        };
        AnalysisStripButtons.Columns = Math.Max(2, list.Length);
        foreach (var a in list) AnalysisStripButtons.Children.Add(AnalysisCard(a, key));
        Motion.StaggerChildren(AnalysisStripButtons, 60, 10);
    }

    /// <summary>Mezcla el color con el fondo de las tarjetas (0 = fondo, 1 = color puro), sin transparencias.</summary>
    static Color Mix(Color c, double k)
    {
        Color b = Color.FromRgb(0x13, 0x17, 0x21);
        return Color.FromRgb((byte)(b.R + (c.R - b.R) * k), (byte)(b.G + (c.G - b.G) * k), (byte)(b.B + (c.B - b.B) * k));
    }

    static Color WithAlpha(string hex, byte alpha)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    /// <summary>Tarjeta de un análisis: icono con brillo, descripción, último resultado y botón «Analizar».</summary>
    FrameworkElement AnalysisCard((string Kind, string Name, string Desc, string Glyph, string Color) a, string section)
    {
        var col = (Color)ColorConverter.ConvertFromString(a.Color);
        var accent = new SolidColorBrush(col);
        var idle = new SolidColorBrush(WithAlpha(a.Color, 0x38));
        var hover = new SolidColorBrush(WithAlpha(a.Color, 0xB0));

        var card = new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 0, 12, 0), Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1), BorderBrush = idle,
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Mix(col, 0.17), 0), new(Mix(col, 0.08), 0.35), new(Mix(col, 0.03), 0.7), new(Mix(col, 0), 1)
            }, new Point(0, 0), new Point(1, 1)),
            ToolTip = "Ejecuta el análisis (tarda unos segundos)"
        };
        Motion.SetHover(card, true);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 50, Height = 50, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(WithAlpha(a.Color, 0x40), WithAlpha(a.Color, 0x14), 45),
            BorderBrush = new SolidColorBrush(WithAlpha(a.Color, 0x60)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = col, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.35 },
            Child = new TextBlock { Text = a.Glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 21, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        grid.Children.Add(icon);

        var texts = new StackPanel { Margin = new Thickness(16, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = a.Name, FontFamily = (FontFamily)FindResource("Display"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        texts.Children.Add(new TextBlock { Text = a.Desc, FontSize = 12, Foreground = (Brush)FindResource("Sub"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        // Último resultado guardado
        var (dotBrush, status) = _insights.Analyses.TryGetValue(a.Kind, out var last)
            ? (last.Level switch { Severity.Bad => Palette.HighFg, Severity.Warn => Palette.MediumFg, _ => Palette.BasicFg },
               (last.Level switch { Severity.Bad => "Hay problemas", Severity.Warn => "Se puede mejorar", _ => "Todo bien" }) + " · " + AgoShort(last.At))
            : ((Brush)FindResource("Muted"), "Aún sin analizar");
        var st = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        st.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = dotBrush, VerticalAlignment = VerticalAlignment.Center });
        st.Children.Add(new TextBlock { Text = status, FontSize = 11, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(7, 0, 0, 0) });
        texts.Children.Add(st);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var pill = new Border
        {
            CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 7, 14, 7), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(WithAlpha(a.Color, 0x1F)), BorderBrush = new SolidColorBrush(WithAlpha(a.Color, 0x55)), BorderThickness = new Thickness(1)
        };
        var pillContent = new StackPanel { Orientation = Orientation.Horizontal };
        pillContent.Children.Add(new TextBlock { Text = "\uE768", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 11, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
        pillContent.Children.Add(new TextBlock { Text = "Analizar", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = accent, Margin = new Thickness(7, 0, 0, 0) });
        pill.Child = pillContent;
        Grid.SetColumn(pill, 2);
        grid.Children.Add(pill);

        card.Child = grid;
        card.MouseEnter += (_, _) =>
        {
            card.BorderBrush = hover;
            pill.Background = new LinearGradientBrush(WithAlpha(a.Color, 0x55), WithAlpha(a.Color, 0x30), 0);
            ((System.Windows.Media.Effects.DropShadowEffect)icon.Effect).Opacity = 0.7;
        };
        card.MouseLeave += (_, _) =>
        {
            card.BorderBrush = idle;
            pill.Background = new SolidColorBrush(WithAlpha(a.Color, 0x1F));
            ((System.Windows.Media.Effects.DropShadowEffect)icon.Effect).Opacity = 0.35;
        };
        var kind = a.Kind;
        card.MouseLeftButtonUp += (_, _) => OpenAnalysis(kind, section);
        return card;
    }

    static string AgoShort(DateTime at)
    {
        var d = DateTime.Now - at;
        return d.TotalMinutes < 2 ? "ahora" : d.TotalHours < 1 ? $"hace {(int)d.TotalMinutes} min" : d.TotalDays < 1 ? $"hace {(int)d.TotalHours} h"
             : d.TotalDays < 2 ? "ayer" : $"hace {(int)d.TotalDays} días";
    }

    /// <summary>Abre los resultados de un análisis dentro de su sección (Red, Rendimiento o Gráficos) y lo ejecuta.</summary>
    void OpenAnalysis(string kind, string section)
    {
        _analysisSection = section;
        var s = _nav.First(n => n.Key == section);
        foreach (var p in AllPanels) p.Visibility = p == AnalysisPanel ? Visibility.Visible : Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Collapsed;
        SectionTitle.Text = s.Name;
        SectionHint.Text = "Análisis · " + SectionAnalyses[section].First(x => x.Kind == kind).Name;
        AnalysisBackText.Text = "Volver a " + s.Name;
        var allowed = SectionAnalyses[section].Select(x => x.Kind).ToHashSet();
        int n = 0;
        foreach (var b in AnalysisTabs.Children.OfType<Button>())
        {
            bool on = allowed.Contains((string)b.Tag);
            b.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) n++;
        }
        AnalysisTabs.Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
        AnalysisTabs.Columns = Math.Max(1, n);
        AnalysisIntro.Visibility = Visibility.Visible;
        AnalysisResult.Visibility = Visibility.Collapsed;
        Motion.Enter(AnalysisPanel, 14, 0, 380);
        Motion.Enter(HeaderStack, 8, 0, 300);
        _ = RunAnalysisAsync(kind);
    }

    void AnalysisBack_Click(object sender, RoutedEventArgs e) => ShowSection(_analysisSection);

    // ---------- Frecuencia de los monitores ----------

    System.Windows.Threading.DispatcherTimer? _hzTimer;
    (string Device, int Previous, int Hz)? _hzPending;

    async void BoostHz_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not MonitorInfo m) return;
        var (ok, prev, msg) = await Task.Run(() => Display.SetRefresh(m.Device, m.MaxHz));
        if (!ok) { ShowToast(msg); return; }
        Log.Write($"Pantalla {m.Name}: {prev} Hz → {m.MaxHz} Hz (pendiente de confirmar).");
        _hzPending = (m.Device, prev, m.MaxHz);
        HzTitle.Text = $"¿Se ve bien a {m.MaxHz} Hz?";
        int left = 15;
        void Tick() => HzText.Text = $"{m.Name} ya está a {m.MaxHz} Hz. Si la pantalla parpadea o se queda en negro, no toques nada: vuelve sola a {prev} Hz en {left} s.";
        Tick();
        HzBar.BeginAnimation(WidthProperty, new DoubleAnimation(400, 0, TimeSpan.FromSeconds(15)));
        HzOverlay.BeginAnimation(OpacityProperty, null);
        HzOverlay.Opacity = 0;
        HzOverlay.Visibility = Visibility.Visible;
        Motion.Fade(HzOverlay, 1, 200);
        Motion.Pop(HzCard, 380);
        _hzTimer?.Stop();
        _hzTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hzTimer.Tick += (_, _) => { left--; Tick(); if (left <= 0) HzRevert_Click(this, new RoutedEventArgs()); };
        _hzTimer.Start();
    }

    void CloseHz()
    {
        _hzTimer?.Stop();
        HzBar.BeginAnimation(WidthProperty, null);
        Motion.Fade(HzOverlay, 0, 200, () => HzOverlay.Visibility = Visibility.Collapsed);
    }

    async void HzKeep_Click(object sender, RoutedEventArgs e)
    {
        if (_hzPending is not { } p) return;
        _hzPending = null;
        CloseHz();
        Log.Write($"Pantalla {p.Device}: se mantiene a {p.Hz} Hz.");
        ShowToast($"Listo: tu monitor ya va a {p.Hz} Hz.");
        await RunAnalysisAsync("display");
    }

    async void HzRevert_Click(object sender, RoutedEventArgs e)
    {
        if (_hzPending is not { } p) return;
        _hzPending = null;
        CloseHz();
        var (ok, _, msg) = await Task.Run(() => Display.SetRefresh(p.Device, p.Previous));
        Log.Write($"Pantalla {p.Device}: vuelta a {p.Previous} Hz ({msg}).");
        ShowToast(ok ? $"Vuelta a {p.Previous} Hz. No se ha cambiado nada más." : "No se pudo volver atrás: " + msg);
    }

    // ---------- Análisis ----------

    string _lastAnalysis = "";
    AnalysisReport? _report;

    void AnalysisTab_Click(object sender, RoutedEventArgs e) => _ = RunAnalysisAsync((string)((Button)sender).Tag);
    void RerunAnalysis_Click(object sender, RoutedEventArgs e) { if (_lastAnalysis.Length > 0) _ = RunAnalysisAsync(_lastAnalysis); }

    async Task RunAnalysisAsync(string kind)
    {
        _lastAnalysis = kind;
        foreach (var b in AnalysisTabs.Children.OfType<Button>())
        {
            bool on = (string)b.Tag == kind;
            b.BorderBrush = on ? (Brush)FindResource("Accent") : (Brush)FindResource("LineStrong");
            b.Background = on ? new LinearGradientBrush(Color.FromRgb(0x24, 0x1C, 0x48), Color.FromRgb(0x12, 0x1B, 0x2A), 45) : Palette.B("#1A1F2A");
        }

        AnalysisReport? rep = null;
        string title = kind switch
        {
            "ping" => "Analizando la latencia de red…", "dns" => "Comparando servidores DNS…",
            "stutter" => "Buscando microtirones…", "display" => "Revisando pantallas y gráficos…", _ => "Analizando el rendimiento…"
        };
        await RunBusy(title, async report =>
        {
            void P(string s) => report(s);
            rep = kind switch
            {
                "ping" => await NetworkLatency.Run(P),
                "dns" => await DnsBench.Run(P),
                "stutter" => await Stutter.Run(P),
                "display" => await Display.Run(P),
                _ => await Slowness.Run(P)
            };
        });
        if (rep is null) return;
        _report = rep;
        _insights.StoreAnalysis(kind, rep);
        ShowReport(rep);
        Log.Write($"Análisis «{rep.Title}»: {rep.Verdict}");
    }

    void ShowReport(AnalysisReport rep)
    {
        AnalysisIntro.Visibility = Visibility.Collapsed;
        AnalysisResult.Visibility = Visibility.Visible;

        var (fg, bg, glyph, border) = rep.Level switch
        {
            Severity.Ok => (Palette.BasicFg, Palette.BasicBg, "", "#1F5C45"),
            Severity.Warn => (Palette.MediumFg, Palette.MediumBg, "", "#6B5216"),
            Severity.Bad => (Palette.HighFg, Palette.HighBg, "", "#6E2A2E"),
            _ => (Palette.Native, Palette.NativeBg, "", "#1E4A5A")
        };
        VerdictIcon.Text = glyph;
        VerdictIcon.Foreground = fg;
        VerdictIconBox.Background = bg;
        VerdictCard.Background = new LinearGradientBrush(((SolidColorBrush)bg).Color, (Color)ColorConverter.ConvertFromString("#141821"), 0);
        VerdictCard.BorderBrush = Palette.B(border);
        VerdictTitle.Text = rep.Title;
        VerdictText.Text = rep.Verdict;

        MetricsList.ItemsSource = rep.Metrics.Select(m => new MetricVM(m)).ToList();
        FindingsList.ItemsSource = rep.Findings.Select(f => new DiagVM(f)).ToList();

        Motion.Enter(VerdictCard, 14, 0, 420);
        ChartCard.Visibility = rep.Samples is { Count: > 1 } ? Visibility.Visible : Visibility.Collapsed;
        ChartLabel.Text = rep.ChartLabel.ToUpperInvariant();
        if (rep.Samples is { Count: > 1 } samples)
        {
            Chart.Threshold = rep.Threshold;
            Chart.SplitAt = rep.SplitAt;
            Chart.SplitLeft = "EN REPOSO";
            Chart.SplitRight = "DESCARGANDO";
            Chart.SetData(samples, true);
            Motion.Enter(ChartCard, 16, 120, 450);
        }
        BarsCard.Visibility = rep.Bars is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (rep.Bars is { Count: > 0 } bars)
        {
            BarsLabel.Text = rep.BarsLabel.ToUpperInvariant();
            Bars.SetData(bars);
            Motion.Enter(BarsCard, 16, 200, 450);
        }

        MonitorsBox.Visibility = rep.Monitors is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        MonitorsList.ItemsSource = rep.Monitors;

        if (rep.Dns is { } dns)
        {
            var ok = dns.Where(d => d.Median < double.MaxValue).Select(d => d.Median).ToList();
            double bestMs = ok.DefaultIfEmpty(0).Min(), worstMs = ok.DefaultIfEmpty(0).Max();
            DnsList.ItemsSource = dns.Select(d => new DnsRowVM(d, d.Median == bestMs, worstMs)).ToList();
            DnsBox.Visibility = Visibility.Visible;
            RestoreDnsButton.Visibility = DnsTool.HasBackup ? Visibility.Visible : Visibility.Collapsed;
        }
        else DnsBox.Visibility = Visibility.Collapsed;
    }

    async void UseDns_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DnsRowVM row) return;
        string msg = "";
        await RunBusy($"Cambiando el DNS a {row.Name}…", async _ => msg = await Task.Run(() => DnsTool.Apply(row.R.Provider)));
        ShowToast(msg);
        RestoreDnsButton.Visibility = DnsTool.HasBackup ? Visibility.Visible : Visibility.Collapsed;
        await RunAnalysisAsync("dns"); // refresca la tabla para marcar el nuevo «En uso»
    }

    async void RestoreDns_Click(object sender, RoutedEventArgs e)
    {
        string msg = "";
        await RunBusy("Restaurando el DNS original…", async _ => msg = await Task.Run(DnsTool.Restore));
        ShowToast(msg);
        await RunAnalysisAsync("dns");
    }
}
