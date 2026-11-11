using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WinPulse;

/// <summary>Un juego instalado, venga del lanzador que venga.</summary>
public sealed class GameEntry
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    /// <summary>Lo que se abre para jugar: una URI del lanzador o la ruta del ejecutable.</summary>
    public string Launch { get; init; } = "";
    /// <summary>Portada vertical (archivo local o URL), si la hay.</summary>
    public string? Cover { get; init; }
    /// <summary>Imagen cuadrada o ejecutable del que sacar el icono cuando no hay portada.</summary>
    public string? Icon { get; init; }
    public DateTime? LastPlayed { get; init; }
    public bool Custom { get; init; }
    /// <summary>Imagen panorámica de fondo y logo transparente, para el destacado.</summary>
    public string? Hero { get; init; }
    public string? Logo { get; init; }
}

public sealed class GameCardVM
{
    public GameEntry G { get; }
    public GameCardVM(GameEntry g) { G = g; }
    public string Title => G.Title;
    public string Source => G.Source;
    public bool Custom => G.Custom;
    public Visibility RemoveVisibility => G.Custom ? Visibility.Visible : Visibility.Collapsed;
    public string Initials => string.Concat(G.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public string LastPlayedText => G.LastPlayed is { } d ? Ago(d) : G.Source;
    public Brush SourceBg => G.Source switch
    {
        "Steam" => Palette.B("#CC1B2838"), "Epic Games" => Palette.B("#CC2A2A2A"), "Microsoft Store" => Palette.B("#CC0F3D2E"), _ => Palette.B("#CC2A2350")
    };
    public Brush Placeholder
    {
        get
        {
            // Color estable por juego para que la tarjeta sin portada no sea siempre igual.
            int h = Math.Abs(G.Title.GetHashCode()) % 6;
            var (a, b) = h switch
            {
                0 => ("#4C1D95", "#0E7490"), 1 => ("#7C2D12", "#4C1D95"), 2 => ("#064E3B", "#1E3A8A"),
                3 => ("#831843", "#312E81"), 4 => ("#1E3A8A", "#0F766E"), _ => ("#3F3F46", "#4338CA")
            };
            return new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), 60);
        }
    }

    ImageBrush? _cover; bool _coverDone;
    public ImageBrush? CoverBrush
    {
        get
        {
            if (_coverDone) return _cover;
            _coverDone = true;
            if (G.Cover is not { } c) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(c, UriKind.Absolute);
                bi.DecodePixelWidth = 300;
                if (!c.StartsWith("http")) bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                _cover = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
            }
            catch { }
            return _cover;
        }
    }

    ImageSource? _icon; bool _iconDone;
    public ImageSource? IconImage
    {
        get
        {
            if (_iconDone) return _icon;
            _iconDone = true;
            if (G.Cover is not null || G.Icon is not { } i) return null;
            _icon = GameLibrary.LoadIcon(i);
            return _icon;
        }
    }
    public Visibility InitialsVisibility => G.Cover is null && IconImage is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BlurVisibility => G.Cover is null && IconImage is not null ? Visibility.Visible : Visibility.Collapsed;
    public string MetaText => G.LastPlayed is not null ? $"{LastPlayedText} · {G.Source}" : G.Source;

    static BitmapImage? Img(string? uri, int width)
    {
        if (uri is null) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(uri, UriKind.Absolute);
            bi.DecodePixelWidth = width;
            if (!uri.StartsWith("http")) bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            return bi;
        }
        catch { return null; }
    }

    /// <summary>Fondo del destacado: imagen panorámica, o la portada si no hay.</summary>
    public Brush HeroBrush
    {
        get
        {
            var img = Img(G.Hero, 1600) ?? Img(G.Cover, 600);
            if (img is not null) return new ImageBrush(img) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center };
            if (IconImage is { } ic) return new ImageBrush(ic) { Stretch = Stretch.UniformToFill, Opacity = 0.5 };
            return Placeholder;
        }
    }
    public ImageSource? LogoImage => Img(G.Logo, 760);
    public Visibility LogoVisibility => G.Logo is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TitleVisibility => G.Logo is null ? Visibility.Visible : Visibility.Collapsed;

    static string Ago(DateTime d)
    {
        var t = DateTime.Now - d;
        if (t.TotalHours < 1) return "Jugado hace un momento";
        if (t.TotalDays < 1) return $"Jugado hace {(int)t.TotalHours} h";
        if (t.TotalDays < 2) return "Jugado ayer";
        if (t.TotalDays < 60) return $"Jugado hace {(int)t.TotalDays} días";
        return $"Jugado el {d:dd/MM/yyyy}";
    }
}

public static class GameLibrary
{
    sealed class CustomGame { public string Title { get; set; } = ""; public string Exe { get; set; } = ""; }
    sealed class Store { public List<CustomGame> Custom { get; set; } = new(); }
    static string StorePath => Path.Combine(TweakEngine.DataDir, "games.json");

    static Store LoadStore()
    {
        try { return File.Exists(StorePath) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(StorePath)) ?? new() : new(); }
        catch { return new(); }
    }
    static void SaveStore(Store s) { try { File.WriteAllText(StorePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true })); } catch { } }

    public static void AddCustom(string title, string exe)
    {
        var s = LoadStore();
        s.Custom.RemoveAll(c => c.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
        s.Custom.Add(new CustomGame { Title = title, Exe = exe });
        SaveStore(s);
    }

    public static void RemoveCustom(string exe)
    {
        var s = LoadStore();
        s.Custom.RemoveAll(c => c.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
        SaveStore(s);
    }

    /// <summary>Todos los juegos detectados, del jugado más recientemente al más antiguo.</summary>
    public static List<GameEntry> Scan()
    {
        var list = new List<GameEntry>();
        try { list.AddRange(Steam()); } catch (Exception ex) { Log.Write("Biblioteca Steam: " + ex.Message); }
        try { list.AddRange(Epic()); } catch (Exception ex) { Log.Write("Biblioteca Epic: " + ex.Message); }
        try { list.AddRange(StoreGames()); } catch (Exception ex) { Log.Write("Juegos de la Store: " + ex.Message); }
        foreach (var c in LoadStore().Custom.Where(c => File.Exists(c.Exe)))
            list.Add(new GameEntry { Id = "custom:" + c.Exe, Title = c.Title, Source = "Añadido por ti", Launch = c.Exe, Icon = c.Exe, Custom = true });
        return list.GroupBy(g => g.Id).Select(g => g.First())
                   .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Title).ToList();
    }

    // ---------- Steam ----------

    static readonly Regex Kv = new("\"(\\w+)\"\\s+\"([^\"]*)\"");
    static readonly string[] SteamSkip = { "Redistributable", "Steamworks", "SteamVR", "Proton", "Soundtrack", "Dedicated Server", "Steam Linux Runtime", "Wallpaper Engine" };

    static IEnumerable<GameEntry> Steam()
    {
        var root = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (root is null) yield break;
        root = root.Replace('/', '\\');
        var libs = new List<string> { root };
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        var cache = Path.Combine(root, "appcache", "librarycache");
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Kv.Matches(File.ReadAllText(acf))) kv.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
                if (!kv.TryGetValue("appid", out var id) || !kv.TryGetValue("name", out var name)) continue;
                if (id == "228980" || SteamSkip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                DateTime? last = kv.TryGetValue("LastPlayed", out var lp) && long.TryParse(lp, out var sec) && sec > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime : null;
                string? cover = SteamAsset(cache, id, "library_600x900.jpg", "library_capsule.jpg") ?? $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/library_600x900.jpg";
                yield return new GameEntry
                {
                    Id = "steam:" + id, Title = name, Source = "Steam", Launch = "steam://rungameid/" + id, Cover = cover, LastPlayed = last,
                    Hero = SteamAsset(cache, id, "library_hero.jpg") ?? $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/library_hero.jpg",
                    Logo = SteamAsset(cache, id, "logo.png")
                };
            }
        }
    }

    /// <summary>Portada vertical de la caché de Steam (según la versión, en la carpeta del juego o en subcarpetas).</summary>
    static string? SteamAsset(string cache, string id, params string[] names)
    {
        foreach (var n in names)
        {
            var flat = Path.Combine(cache, id + "_" + n);
            if (File.Exists(flat)) return flat;
        }
        var dir = Path.Combine(cache, id);
        if (!Directory.Exists(dir)) return null;
        try
        {
            foreach (var name in names)
            {
                var hit = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        }
        catch { }
        return null;
    }

    // ---------- Epic Games ----------

    static IEnumerable<GameEntry> Epic()
    {
        const string data = @"C:\ProgramData\Epic\EpicGamesLauncher\Data";
        var dir = Path.Combine(data, "Manifests");
        if (!Directory.Exists(dir)) yield break;
        Dictionary<string, string>? art = null;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
            bool isGame = r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                          && cats.EnumerateArray().Any(c => c.GetString() == "games");
            if (!isGame) continue;
            string ns = S("CatalogNamespace"), item = S("CatalogItemId"), app = S("AppName");
            art ??= EpicArt(Path.Combine(data, "Catalog", "catcache.bin"));
            art.TryGetValue(item, out var cover);
            art.TryGetValue(item + "|hero", out var hero);
            art.TryGetValue(item + "|logo", out var logo);
            var exe = Path.Combine(S("InstallLocation"), S("LaunchExecutable"));
            yield return new GameEntry
            {
                Id = "epic:" + app, Title = S("DisplayName"), Source = "Epic Games",
                Launch = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(item)}%3A{Uri.EscapeDataString(app)}?action=launch&silent=true",
                Cover = cover, Hero = hero, Logo = logo, Icon = File.Exists(exe) ? exe : null
            };
        }
    }

    /// <summary>Portadas de la caché del catálogo de Epic (base64 con JSON): id del juego → URL de la imagen vertical.</summary>
    static Dictionary<string, string> EpicArt(string path)
    {
        var map = new Dictionary<string, string>();
        if (!File.Exists(path)) return map;
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(path).Trim()));
            using var doc = JsonDocument.Parse(json);
            string[] order = { "DieselGameBoxTall", "OfferImageTall", "DieselStoreFrontTall", "DieselGameBox", "Thumbnail" };
            foreach (var it in doc.RootElement.EnumerateArray())
            {
                if (!it.TryGetProperty("id", out var id) || !it.TryGetProperty("keyImages", out var imgs)) continue;
                var byType = imgs.EnumerateArray().Where(i => i.TryGetProperty("type", out _) && i.TryGetProperty("url", out _))
                                 .GroupBy(i => i.GetProperty("type").GetString() ?? "").ToDictionary(g => g.Key, g => g.First().GetProperty("url").GetString() ?? "");
                var url = order.Select(o => byType.GetValueOrDefault(o)).FirstOrDefault(u => !string.IsNullOrEmpty(u));
                if (url is not null) map[id.GetString() ?? ""] = url;
                var hero = new[] { "DieselGameBox", "OfferImageWide", "DieselStoreFrontWide" }.Select(o => byType.GetValueOrDefault(o)).FirstOrDefault(u => !string.IsNullOrEmpty(u));
                if (hero is not null) map[(id.GetString() ?? "") + "|hero"] = hero;
                if (byType.GetValueOrDefault("DieselGameBoxLogo") is { Length: > 0 } lg) map[(id.GetString() ?? "") + "|logo"] = lg;
            }
        }
        catch { }
        return map;
    }

    // ---------- Microsoft Store / Xbox (juegos GDK) ----------

    static IEnumerable<GameEntry> StoreGames()
    {
        var (_, outp) = Shell.PowerShell(
            "ConvertTo-Json -Compress -InputObject @(Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.InstallLocation -and (Test-Path (Join-Path $_.InstallLocation 'MicrosoftGame.config')) } | " +
            "ForEach-Object { $m = Get-AppxPackageManifest $_; [pscustomobject]@{ f=$_.PackageFamilyName; l=$_.InstallLocation; a=[string]@($m.Package.Applications.Application)[0].Id; n=[string]$m.Package.Properties.DisplayName } })", 120_000);
        int start = outp.IndexOf('[');
        if (start < 0) yield break;
        using var doc = JsonDocument.Parse(outp[start..]);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string pfn = e.GetProperty("f").GetString() ?? "", loc = e.GetProperty("l").GetString() ?? "", appId = e.GetProperty("a").GetString() ?? "";
            string title = e.GetProperty("n").GetString() ?? pfn;
            string? logo = null;
            try
            {
                var cfg = File.ReadAllText(Path.Combine(loc, "MicrosoftGame.config"));
                var t = Regex.Match(cfg, "<ShellVisuals[^>]*DefaultDisplayName=\"([^\"]+)\"");
                if (t.Success && !t.Groups[1].Value.StartsWith("ms-resource")) title = t.Groups[1].Value;
                var lg = Regex.Match(cfg, "Square480x480Logo=\"([^\"]+)\"");
                if (lg.Success && File.Exists(Path.Combine(loc, lg.Groups[1].Value))) logo = Path.Combine(loc, lg.Groups[1].Value);
            }
            catch { }
            if (title.StartsWith("ms-resource")) title = pfn.Split('_')[0].Split('.').Last();
            if (title.EndsWith("GDK")) title = title[..^3];
            yield return new GameEntry { Id = "store:" + pfn, Title = title, Source = "Microsoft Store", Launch = $@"shell:AppsFolder\{pfn}!{appId}", Icon = logo };
        }
    }

    // ---------- Iconos y lanzamiento ----------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHDefExtractIcon(string path, int index, uint flags, out IntPtr large, out IntPtr small, uint size);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    /// <summary>Icono grande de un ejecutable, o la imagen si ya es un PNG/JPG.</summary>
    public static ImageSource? LoadIcon(string path)
    {
        try
        {
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.UriSource = new Uri(path); bi.DecodePixelWidth = 160; bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                return bi;
            }
            if (SHDefExtractIcon(path, 0, 0, out var large, out var small, (16u << 16) | 256u) != 0 || large == IntPtr.Zero) return null;
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(large); if (small != IntPtr.Zero) DestroyIcon(small); }
        }
        catch { return null; }
    }

    /// <summary>Abre el juego a través del Explorador, para que se ejecute como usuario normal y no como administrador.</summary>
    public static void Launch(GameEntry g) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{g.Launch}\"") { UseShellExecute = false });

    /// <summary>Resuelve un acceso directo .lnk a su ejecutable.</summary>
    public static string ResolveShortcut(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            string target = sh.CreateShortcut(path).TargetPath;
            return string.IsNullOrEmpty(target) ? path : target;
        }
        catch { return path; }
    }
}
