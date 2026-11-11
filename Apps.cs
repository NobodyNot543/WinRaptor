using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace WinPulse;

public enum AppAdvice { Recommended, Optional, IfUnused }

public sealed record CatalogApp(string Package, string Name, string Category, string Desc, AppAdvice Advice);

/// <summary>App que se puede quitar, tal como está en este equipo.</summary>
public sealed class AppVM : Observable
{
    public CatalogApp C { get; init; } = null!;
    public string Package => C.Package;
    public string Name => C.Name;
    public string Desc => C.Desc;
    public string Category => C.Category;
    public string Version { get; init; } = "";
    public string Pfn { get; init; } = "";
    public string AdviceText => C.Advice switch { AppAdvice.Recommended => "Recomendado quitar", AppAdvice.Optional => "Opcional", _ => "Solo si no la usas" };
    public Brush AdviceFg => C.Advice switch { AppAdvice.Recommended => Palette.BasicFg, AppAdvice.Optional => Palette.Native, _ => Palette.MediumFg };
    public Brush AdviceBg => C.Advice switch { AppAdvice.Recommended => Palette.BasicBg, AppAdvice.Optional => Palette.NativeBg, _ => Palette.MediumBg };
    public string Glyph => Apps.CategoryGlyph(C.Category);
    bool _selected;
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; On(); } }
}

public sealed class AppGroupVM
{
    public string Name { get; init; } = "";
    public string Glyph { get; init; } = "";
    public List<AppVM> Items { get; init; } = new();
    public string CountText => $"{Items.Count} {(Items.Count == 1 ? "app" : "apps")}";
}

public sealed class RemovedApp
{
    public string Package { get; set; } = "";
    public string Name { get; set; } = "";
    public string Pfn { get; set; } = "";
    public DateTime At { get; set; }
    public string When => At.ToString("dd/MM/yyyy HH:mm");
}

public static class Apps
{
    const AppAdvice Rec = AppAdvice.Recommended, Opt = AppAdvice.Optional, Use = AppAdvice.IfUnused;

    /// <summary>Únicas apps que WinRaptor deja quitar. Nada fuera de esta lista se puede desinstalar desde aquí.</summary>
    public static readonly CatalogApp[] Catalog =
    {
        // Publicidad y apps de terceros que vienen de serie
        new("king.com.CandyCrushSaga", "Candy Crush Saga", "Publicidad y juegos de serie", "Juego instalado de fábrica como promoción.", Rec),
        new("king.com.CandyCrushSodaSaga", "Candy Crush Soda Saga", "Publicidad y juegos de serie", "Juego instalado de fábrica como promoción.", Rec),
        new("king.com.BubbleWitch3Saga", "Bubble Witch 3 Saga", "Publicidad y juegos de serie", "Juego instalado de fábrica como promoción.", Rec),
        new("king.com.FarmHeroesSaga", "Farm Heroes Saga", "Publicidad y juegos de serie", "Juego instalado de fábrica como promoción.", Rec),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "Publicidad y juegos de serie", "Juegos de cartas con anuncios y suscripción.", Opt),
        new("5A894077.McAfeeSecurity", "McAfee (prueba)", "Publicidad y juegos de serie", "Antivirus de prueba de pago. Windows ya trae Seguridad de Windows.", Rec),
        new("Facebook.Facebook", "Facebook", "Publicidad y juegos de serie", "Acceso directo preinstalado a Facebook.", Opt),
        new("Facebook.InstagramBeta", "Instagram", "Publicidad y juegos de serie", "Acceso directo preinstalado a Instagram.", Opt),
        new("BytedancePte.Ltd.TikTok", "TikTok", "Publicidad y juegos de serie", "Preinstalada como promoción.", Opt),
        new("AmazonVideo.PrimeVideo", "Prime Video", "Publicidad y juegos de serie", "Preinstalada como promoción.", Opt),
        new("Disney.37853FC22B2CE", "Disney+", "Publicidad y juegos de serie", "Preinstalada como promoción.", Opt),
        new("4DF9E0F8.Netflix", "Netflix", "Publicidad y juegos de serie", "Preinstalada como promoción.", Opt),
        new("SpotifyAB.SpotifyMusic", "Spotify", "Publicidad y juegos de serie", "Preinstalada como promoción.", Use),
        new("7EE7776C.LinkedInforWindows", "LinkedIn", "Publicidad y juegos de serie", "Preinstalada como promoción.", Opt),
        new("AdobeSystemsIncorporated.AdobePhotoshopExpress", "Photoshop Express", "Publicidad y juegos de serie", "Editor de fotos de prueba.", Opt),
        new("Duolingo-LearnLanguagesforFree", "Duolingo", "Publicidad y juegos de serie", "Preinstalada como promoción.", Opt),

        // Noticias y contenido
        new("Microsoft.BingNews", "Noticias", "Noticias e información", "Noticias de MSN con anuncios.", Rec),
        new("Microsoft.BingWeather", "El Tiempo", "Noticias e información", "Previsión del tiempo de MSN.", Opt),
        new("Microsoft.BingFinance", "Finanzas", "Noticias e información", "Información de bolsa de MSN.", Rec),
        new("Microsoft.BingSports", "Deportes", "Noticias e información", "Resultados deportivos de MSN.", Rec),
        new("Microsoft.BingSearch", "Búsqueda de Bing", "Noticias e información", "Resultados web de Bing en la búsqueda del menú Inicio.", Opt),
        new("Microsoft.Start", "Microsoft Start", "Noticias e información", "Feed de noticias y contenido patrocinado.", Rec),
        new("MicrosoftWindows.Client.WebExperience", "Widgets", "Noticias e información", "Panel de noticias y tiempo de la barra de tareas. Consume memoria en segundo plano.", Opt),

        // Ayuda, bienvenida y extras de Microsoft
        new("Microsoft.Getstarted", "Recomendaciones / Introducción", "Ayuda y extras de Microsoft", "Consejos de bienvenida de Windows.", Rec),
        new("Microsoft.GetHelp", "Obtener ayuda", "Ayuda y extras de Microsoft", "Chat de soporte de Microsoft.", Opt),
        new("Microsoft.WindowsFeedbackHub", "Centro de opiniones", "Ayuda y extras de Microsoft", "Envío de comentarios a Microsoft.", Rec),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office)", "Ayuda y extras de Microsoft", "Lanzador y publicidad de la suscripción de Office. No borra Word ni Excel.", Rec),
        new("Microsoft.Windows.DevHome", "Dev Home", "Ayuda y extras de Microsoft", "Panel para programadores.", Rec),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", "Ayuda y extras de Microsoft", "Automatización de tareas.", Opt),
        new("Microsoft.WindowsMaps", "Mapas", "Ayuda y extras de Microsoft", "Mapas sin conexión de Microsoft.", Rec),
        new("Microsoft.People", "Contactos", "Ayuda y extras de Microsoft", "Agenda de contactos antigua.", Rec),
        new("Microsoft.MicrosoftFamily", "Family Safety", "Ayuda y extras de Microsoft", "Control parental de Microsoft.", Use),
        new("Microsoft.Todos", "Microsoft To Do", "Ayuda y extras de Microsoft", "Lista de tareas.", Use),
        new("Microsoft.MicrosoftStickyNotes", "Notas rápidas", "Ayuda y extras de Microsoft", "Notas adhesivas en el escritorio.", Use),
        new("Microsoft.Office.OneNote", "OneNote (app)", "Ayuda y extras de Microsoft", "Cuaderno de notas.", Use),
        new("Microsoft.MicrosoftJournal", "Journal", "Ayuda y extras de Microsoft", "Notas a mano para pantallas táctiles.", Opt),

        // Comunicación
        new("Microsoft.549981C3F5F10", "Cortana", "Comunicación", "Asistente antiguo, ya sin uso en Windows 11.", Rec),
        new("MSTeams", "Microsoft Teams", "Comunicación", "Chat y videollamadas.", Use),
        new("MicrosoftTeams", "Microsoft Teams (clásico)", "Comunicación", "Versión antigua de Teams.", Rec),
        new("Microsoft.SkypeApp", "Skype", "Comunicación", "Servicio retirado por Microsoft.", Rec),
        new("microsoft.windowscommunicationsapps", "Correo y Calendario (antiguos)", "Comunicación", "Apps retiradas, sustituidas por el nuevo Outlook.", Rec),
        new("Microsoft.OutlookForWindows", "Outlook (nuevo)", "Comunicación", "Correo y calendario.", Use),
        new("Microsoft.YourPhone", "Vínculo móvil", "Comunicación", "Conecta tu móvil al PC.", Use),

        // Multimedia y 3D
        new("Microsoft.ZuneVideo", "Películas y TV", "Multimedia", "Reproductor antiguo de vídeos comprados.", Rec),
        new("Microsoft.ZuneMusic", "Reproductor multimedia", "Multimedia", "Reproductor de música y vídeo de Windows.", Use),
        new("Clipchamp.Clipchamp", "Clipchamp", "Multimedia", "Editor de vídeo online.", Opt),
        new("Microsoft.WindowsSoundRecorder", "Grabadora de sonido", "Multimedia", "Graba audio con el micrófono.", Use),
        new("Microsoft.WindowsCamera", "Cámara", "Multimedia", "App de cámara. Las videollamadas no la necesitan.", Use),
        new("Microsoft.MSPaint", "Paint 3D", "Multimedia", "Editor 3D retirado (no es el Paint normal, que está protegido).", Rec),
        new("Microsoft.Microsoft3DViewer", "Visor 3D", "Multimedia", "Visor de modelos 3D.", Rec),
        new("Microsoft.Print3D", "Print 3D", "Multimedia", "Impresión 3D.", Rec),
        new("Microsoft.3DBuilder", "3D Builder", "Multimedia", "Editor 3D antiguo.", Rec),
        new("Microsoft.MixedReality.Portal", "Portal de realidad mixta", "Multimedia", "Solo para gafas de realidad mixta de Windows (retiradas).", Rec),

        // IA
        new("Microsoft.Copilot", "Copilot", "Inteligencia artificial", "Asistente de IA de Microsoft.", Opt),

        // Xbox (cuidado si juegas)
        new("Microsoft.GamingApp", "App de Xbox", "Xbox", "Necesaria para Game Pass y juegos de la Microsoft Store.", Use),
        new("Microsoft.XboxGamingOverlay", "Xbox Game Bar", "Xbox", "Barra de juego (Win+G): grabación de pantalla y rendimiento.", Use),
        new("Microsoft.XboxApp", "Xbox Console Companion", "Xbox", "App antigua de Xbox, ya retirada.", Rec),
        new("Microsoft.XboxSpeechToTextOverlay", "Xbox voz a texto", "Xbox", "Subtítulos de voz para el chat de Xbox.", Opt),
        new("Microsoft.XboxGameOverlay", "Xbox Game Overlay", "Xbox", "Superposición antigua de Xbox.", Opt),
    };

    /// <summary>Nunca se quitan: tienda, instalador, seguridad, herramientas básicas, motores y componentes del sistema.</summary>
    static readonly string[] ProtectedExact =
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.Winget.Source",
        "Microsoft.SecHealthUI", "Microsoft.WindowsTerminal", "Microsoft.WindowsNotepad", "Microsoft.WindowsCalculator",
        "Microsoft.Windows.Photos", "Microsoft.ScreenSketch", "Microsoft.Paint", "Microsoft.MicrosoftEdge.Stable", "Microsoft.MicrosoftEdge",
        "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
        "Microsoft.LockApp", "Microsoft.Services.Store.Engagement", "Microsoft.WindowsAlarms", "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost", "MicrosoftWindows.Client.CBS", "MicrosoftWindows.Client.Core",
        "Microsoft.HEIFImageExtension", "Microsoft.HEVCVideoExtension", "Microsoft.VP9VideoExtensions", "Microsoft.WebpImageExtension",
        "Microsoft.WebMediaExtensions", "Microsoft.RawImageExtension", "Microsoft.AV1VideoExtension", "Microsoft.MPEG2VideoExtension",
        "Microsoft.AVCEncoderVideoExtension", "Microsoft.DolbyAudioExtensions", "Microsoft.ApplicationCompatibilityEnhancements",
    };
    static readonly string[] ProtectedPrefixes =
        { "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.", "Microsoft.WindowsAppRuntime", "Windows.", "MicrosoftWindows.", "Microsoft.Windows.Ai" };

    public static bool IsProtected(string pkg) =>
        ProtectedExact.Contains(pkg, StringComparer.OrdinalIgnoreCase) ||
        ProtectedPrefixes.Any(p => pkg.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && !Catalog.Any(c => c.Package.Equals(pkg, StringComparison.OrdinalIgnoreCase));

    /// <summary>Nombre legible de las apps protegidas que conviene enseñar (las librerías no se listan).</summary>
    public static string? ProtectedLabel(string pkg) => pkg switch
    {
        "Microsoft.WindowsStore" => "Microsoft Store", "Microsoft.DesktopAppInstaller" => "Instalador de apps (winget)",
        "Microsoft.SecHealthUI" => "Seguridad de Windows", "Microsoft.WindowsTerminal" => "Terminal", "Microsoft.WindowsNotepad" => "Bloc de notas",
        "Microsoft.WindowsCalculator" => "Calculadora", "Microsoft.Windows.Photos" => "Fotos", "Microsoft.ScreenSketch" => "Recortes",
        "Microsoft.Paint" => "Paint", "Microsoft.MicrosoftEdge.Stable" => "Microsoft Edge", "Microsoft.XboxIdentityProvider" => "Cuenta de Xbox (juegos)",
        "Microsoft.StorePurchaseApp" => "Compras de la Store", "Microsoft.WindowsAlarms" => "Reloj y alarmas",
        _ when pkg.Contains("VideoExtension", StringComparison.OrdinalIgnoreCase) || pkg.Contains("ImageExtension", StringComparison.OrdinalIgnoreCase)
            || pkg.Contains("MediaExtensions", StringComparison.OrdinalIgnoreCase) => "Códecs de vídeo e imagen",
        _ => null
    };

    public static string CategoryGlyph(string cat) => cat switch
    {
        "Publicidad y juegos de serie" => "", "Noticias e información" => "", "Ayuda y extras de Microsoft" => "",
        "Comunicación" => "", "Multimedia" => "", "Inteligencia artificial" => "", "Xbox" => "", _ => ""
    };

    public sealed record Installed(string Name, string Pfn, string Version);

    public static Dictionary<string, Installed> ListInstalled()
    {
        var (_, outp) = Shell.PowerShell(
            "ConvertTo-Json -Compress -InputObject @(Get-AppxPackage | ForEach-Object { [pscustomobject]@{ n=$_.Name; f=$_.PackageFamilyName; v=[string]$_.Version } })", 180_000);
        var map = new Dictionary<string, Installed>(StringComparer.OrdinalIgnoreCase);
        int start = outp.IndexOf('[');
        if (start < 0) return map;
        using var doc = JsonDocument.Parse(outp[start..]);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var n = e.GetProperty("n").GetString() ?? "";
            if (n.Length > 0) map[n] = new Installed(n, e.GetProperty("f").GetString() ?? "", e.GetProperty("v").GetString() ?? "");
        }
        return map;
    }

    /// <summary>Desinstala una app del catálogo. Con <paramref name="deprovision"/> también se quita de la imagen de Windows
    /// para que no vuelva a aparecer en usuarios nuevos ni tras grandes actualizaciones.</summary>
    public static (bool Ok, string Message) Remove(string package, bool deprovision)
    {
        if (!Catalog.Any(c => c.Package.Equals(package, StringComparison.OrdinalIgnoreCase)) || IsProtected(package))
            throw new InvalidOperationException("WinRaptor no permite quitar esta app.");
        var safe = package.Replace("'", "");
        string script =
            $"$ok=$true; try {{ Get-AppxPackage -Name '{safe}' | Remove-AppxPackage -ErrorAction Stop }} catch {{ $ok=$false; 'ERR|' + $_.Exception.Message }};" +
            (deprovision
                ? $"try {{ Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq '{safe}' | Remove-AppxProvisionedPackage -Online -ErrorAction Stop | Out-Null }} catch {{ }};"
                : "") +
            "if ($ok) { 'OK' }";
        var (_, outp) = Shell.PowerShell(script, 300_000);
        bool ok = outp.Contains("OK") && !outp.Contains("ERR|");
        var err = outp.Split('\n').FirstOrDefault(l => l.StartsWith("ERR|"))?[4..].Trim();
        return (ok, ok ? $"Desinstalada: {package}" : $"No se pudo desinstalar {package}: {err ?? outp.Trim()}");
    }

    static string RemovedPath => Path.Combine(TweakEngine.DataDir, "removed_apps.json");

    public static List<RemovedApp> LoadRemoved()
    {
        try { return File.Exists(RemovedPath) ? JsonSerializer.Deserialize<List<RemovedApp>>(File.ReadAllText(RemovedPath)) ?? new() : new(); }
        catch { return new(); }
    }

    public static void SaveRemoved(List<RemovedApp> list)
    {
        try { File.WriteAllText(RemovedPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    /// <summary>Abre la ficha de la app en la Microsoft Store para volver a instalarla.</summary>
    public static string StoreUri(RemovedApp a) => a.Pfn.Length > 0
        ? "ms-windows-store://pdp/?PFN=" + Uri.EscapeDataString(a.Pfn)
        : "ms-windows-store://search/?query=" + Uri.EscapeDataString(a.Name);
}
