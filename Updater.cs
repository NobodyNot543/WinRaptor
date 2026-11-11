using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinPulse;

/// <summary>
/// Busca versiones nuevas en las Releases de GitHub y se actualiza sin reinstalar.
/// Solo sustituye el ejecutable: los datos (ajustes aplicados, historial, perfiles…) viven en
/// %LOCALAPPDATA%\WinPulse y no se tocan.
/// </summary>
public static class Updater
{
    const string Repo = "NobodyNot543/WinRaptor";
    /// <summary>Nombre del archivo que debe adjuntarse a cada Release.</summary>
    const string AssetName = "WinRaptor.exe";

    public sealed record Release(Version Version, string Tag, string Notes, string DownloadUrl, long Size, string? Sha256, string PageUrl);

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AssetName);
    static string OldPath => ExePath + ".old";

    static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WinRaptor/{Current.ToString(3)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Devuelve la última versión publicada si es más nueva que la actual; si no, null.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromSeconds(15));
        // Se revisan todas las versiones publicadas (también las marcadas como beta / pre-release,
        // que «releases/latest» no devuelve) y se elige la más nueva que tenga un ejecutable de WinRaptor.
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=20", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // repositorio no accesible o sin versiones
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        Release? best = null;
        foreach (var root in doc.RootElement.EnumerateArray())
        {
            if (root.TryGetProperty("draft", out var dr) && dr.GetBoolean()) continue;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseVersion(tag, out var ver) || ver <= Normalize(Current)) continue;
            if (best is not null && ver <= best.Version) continue;
            if (FindExe(root.GetProperty("assets")) is not { } a)
            {
                Log.Write($"Actualizaciones: la versión {tag} no incluye ningún WinRaptor*.exe.");
                continue;
            }
            string? sha = null;
            if (a.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:"))
                sha = dg["sha256:".Length..];
            best = new Release(ver, tag,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                a.GetProperty("browser_download_url").GetString() ?? "",
                a.GetProperty("size").GetInt64(), sha,
                root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "");
        }
        return best;
    }

    /// <summary>El ejecutable adjunto: vale cualquier .exe que empiece por «WinRaptor»
    /// (WinRaptor.exe, WinRaptor-V2.7.0.exe, WinRaptor Portable.exe…). Si hay varios, se prefiere WinRaptor.exe.</summary>
    static JsonElement? FindExe(JsonElement assets)
    {
        JsonElement? found = null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !name.StartsWith("WinRaptor", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) return a;
            found ??= a;
        }
        return found;
    }

    /// <summary>
    /// Descarga la nueva versión, la coloca en lugar del ejecutable actual y abre la nueva.
    /// Quien llama debe cerrar la app después (Application.Current.Shutdown()).
    /// </summary>
    public static async Task InstallAsync(Release r, IProgress<double>? progress, CancellationToken ct = default)
    {
        var exe = ExePath;
        var dir = Path.GetDirectoryName(exe)!;
        var tmp = Path.Combine(dir, AssetName + ".download");

        using (var http = NewClient(TimeSpan.FromMinutes(10)))
        using (var resp = await http.GetAsync(r.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? r.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        // Comprobaciones antes de tocar el ejecutable que funciona.
        var size = new FileInfo(tmp).Length;
        if (r.Size > 0 && size != r.Size)
        {
            File.Delete(tmp);
            throw new InvalidOperationException("La descarga llegó incompleta. Inténtalo de nuevo.");
        }
        if (r.Sha256 is { } expected)
        {
            await using var fs = File.OpenRead(tmp);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                File.Delete(tmp);
                throw new InvalidOperationException("La descarga no coincide con la publicada (huella SHA-256 distinta).");
            }
        }

        // Windows permite renombrar un .exe en uso: el actual pasa a .old y el nuevo ocupa su sitio.
        try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { }
        File.Move(exe, OldPath);
        try { File.Move(tmp, exe); }
        catch
        {
            File.Move(OldPath, exe); // deja todo como estaba
            throw;
        }
        Log.Write($"Actualizado a {r.Tag}. Reiniciando WinRaptor…");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = "--updated" });
    }

    /// <summary>Borra el ejecutable antiguo que queda tras actualizar. Se llama al arrancar.</summary>
    public static void CleanupOld()
    {
        for (int i = 0; i < 10; i++)
        {
            try { if (File.Exists(OldPath)) File.Delete(OldPath); return; }
            catch { Thread.Sleep(300); } // la versión anterior puede tardar un instante en cerrarse
        }
    }

    static bool TryParseVersion(string tag, out Version v)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        if (Version.TryParse(s, out var parsed)) { v = Normalize(parsed); return true; }
        v = new Version(0, 0, 0);
        return false;
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
