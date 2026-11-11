using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace WinPulse;

public sealed record Metric(string Label, string Value, Severity Level = Severity.Info);

public sealed class AnalysisReport
{
    public Severity Level { get; set; } = Severity.Ok;
    public string Title { get; set; } = "";
    public string Verdict { get; set; } = "";
    public List<Metric> Metrics { get; } = new();
    public List<DiagResult> Findings { get; } = new();
    /// <summary>Serie para la gráfica (ms) y la línea de referencia.</summary>
    public List<double>? Samples { get; set; }
    public double Threshold { get; set; }
    public string ChartLabel { get; set; } = "";
    /// <summary>Índice a partir del cual las muestras son de una segunda fase (por ejemplo, con la red cargada).</summary>
    public int SplitAt { get; set; } = -1;
    public List<ChartBar>? Bars { get; set; }
    public string BarsLabel { get; set; } = "";
    public List<DnsResult>? Dns { get; set; }
    public List<MonitorInfo>? Monitors { get; set; }

    public void Worst(Severity s) { if (s != Severity.Info && s > Level) Level = s; }
}

static class Stats
{
    public static double Percentile(List<double> v, double p)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Clamp((int)Math.Ceiling(p / 100 * s.Count) - 1, 0, s.Count - 1)];
    }

    public static double Jitter(List<double> v) =>
        v.Count < 2 ? 0 : v.Zip(v.Skip(1), (a, b) => Math.Abs(b - a)).Average();

    /// <summary>Reduce una serie larga a n puntos conservando los picos (máximo por tramo).</summary>
    public static List<double> Downsample(List<double> v, int n)
    {
        if (v.Count <= n) return v.ToList();
        var r = new List<double>(n);
        double step = (double)v.Count / n;
        for (int i = 0; i < n; i++)
        {
            int a = (int)(i * step), b = Math.Min(v.Count, (int)((i + 1) * step));
            r.Add(v.Skip(a).Take(Math.Max(1, b - a)).Max());
        }
        return r;
    }
}

// =================================================================
//  Latencia de red
// =================================================================

public static class NetworkLatency
{
    sealed record PingStats(List<double> Rtt, int Lost, int Sent)
    {
        public double Avg => Rtt.Count == 0 ? 0 : Rtt.Average();
        public double Min => Rtt.Count == 0 ? 0 : Rtt.Min();
        public double Max => Rtt.Count == 0 ? 0 : Rtt.Max();
        public double Jitter => Stats.Jitter(Rtt);
        public double Loss => Sent == 0 ? 0 : 100.0 * Lost / Sent;
    }

    static async Task<PingStats> PingMany(IPAddress ip, int count, int intervalMs, CancellationToken ct = default)
    {
        using var p = new Ping();
        var rtt = new List<double>();
        int lost = 0, sent = 0;
        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            sent++;
            try
            {
                var r = await p.SendPingAsync(ip, 1000);
                if (r.Status == IPStatus.Success) rtt.Add(r.RoundtripTime); else lost++;
            }
            catch { lost++; }
            try { await Task.Delay(intervalMs, ct); } catch (TaskCanceledException) { }
        }
        return new PingStats(rtt, lost, sent);
    }

    public sealed record Link(string Name, string Kind, IPAddress? Gateway, long SpeedMbps, List<IPAddress> Dns, int Index, string Id);

    /// <summary>El adaptador con puerta de enlace por el que sale el tráfico.</summary>
    public static Link? ActiveLink()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            var gw = props.GatewayAddresses.Select(g => g.Address)
                          .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gw is null) continue;
            string kind = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi"
                        : ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet ? "Cable (Ethernet)"
                        : ni.NetworkInterfaceType.ToString();
            int idx = 0;
            try { idx = props.GetIPv4Properties()?.Index ?? 0; } catch { }
            return new Link(ni.Description, kind, gw, ni.Speed / 1_000_000,
                props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList(), idx, ni.Id);
        }
        return null;
    }

    public static async Task<AnalysisReport> Run(Action<string> progress)
    {
        var rep = new AnalysisReport { Title = "Latencia de red" };
        var link = ActiveLink();
        if (link is null)
        {
            rep.Level = Severity.Bad;
            rep.Verdict = "No hay ninguna conexión de red activa con salida a Internet.";
            return rep;
        }
        rep.Metrics.Add(new("Conexión", $"{link.Kind} · {link.SpeedMbps} Mb/s"));

        progress("Midiendo el ping al router y a Internet (unos 12 s)…");
        var targets = new (string name, IPAddress ip)[]
        {
            ("Router", link.Gateway!),
            ("Cloudflare 1.1.1.1", IPAddress.Parse("1.1.1.1")),
            ("Google 8.8.8.8", IPAddress.Parse("8.8.8.8")),
            ("Quad9 9.9.9.9", IPAddress.Parse("9.9.9.9")),
        };
        var results = await Task.WhenAll(targets.Select(t => PingMany(t.ip, 40, 250)));
        var router = results[0];
        var internet = results.Skip(1).Where(r => r.Rtt.Count > 0).OrderBy(r => r.Avg).FirstOrDefault() ?? results[1];

        Severity Lv(double v, double warn, double bad) => v >= bad ? Severity.Bad : v >= warn ? Severity.Warn : Severity.Ok;
        rep.Bars = new();
        rep.BarsLabel = "Ping medio por destino (más corto es mejor)";
        for (int i = 0; i < targets.Length; i++)
        {
            var r = results[i];
            var lvl = r.Rtt.Count == 0 ? Severity.Bad : i == 0 ? Lv(r.Avg, 5, 20) : Lv(r.Avg, 60, 120);
            rep.Metrics.Add(new(targets[i].name, r.Rtt.Count == 0 ? "sin respuesta" : $"{r.Avg:0} ms (±{r.Jitter:0.0})", lvl));
            rep.Bars.Add(new(targets[i].name, r.Rtt.Count == 0 ? 0 : r.Avg, r.Rtt.Count == 0 ? "sin respuesta" : $"{r.Avg:0} ms", lvl));
        }
        rep.Metrics.Add(new("Jitter Internet", $"{internet.Jitter:0.0} ms", Lv(internet.Jitter, 10, 25)));
        rep.Metrics.Add(new("Pérdida", $"{results.Skip(1).Average(r => r.Loss):0.#} %", Lv(results.Skip(1).Average(r => r.Loss), 1, 3)));
        rep.Samples = internet.Rtt;
        rep.Threshold = internet.Min + 30;
        rep.ChartLabel = "Ping a Internet (ms) en reposo";

        // ---- Diagnóstico ----
        if (router.Loss > 0 || router.Jitter > 5 || router.Avg > 10)
        {
            rep.Worst(link.Kind == "Wi-Fi" ? Severity.Warn : Severity.Bad);
            rep.Findings.Add(new(link.Kind == "Wi-Fi" ? Severity.Warn : Severity.Bad,
                "La red local es inestable",
                $"Al router: {router.Avg:0} ms de media, picos de {router.Max:0} ms y {router.Loss:0.#} % de pérdida. Un router cercano debería responder en 1–3 ms.",
                link.Kind == "Wi-Fi"
                    ? "Conéctate por cable, acércate al router o usa la banda de 5 GHz. Aplica «Wi-Fi a máximo rendimiento» en CPU y energía."
                    : "Revisa el cable y el puerto del router, y actualiza el driver de red."));
        }
        else rep.Findings.Add(new(Severity.Ok, "Red local estable", $"El router responde en {router.Avg:0} ms sin pérdidas."));

        double loss = results.Skip(1).Average(r => r.Loss);
        if (loss >= 1 && router.Loss == 0)
        {
            rep.Worst(loss >= 3 ? Severity.Bad : Severity.Warn);
            rep.Findings.Add(new(loss >= 3 ? Severity.Bad : Severity.Warn, "Se pierden paquetes fuera de tu casa",
                $"{loss:0.#} % de pérdida hacia Internet, pero ninguna hacia el router.",
                "El problema está en tu proveedor de Internet. Si se repite, llámales con este dato."));
        }
        if (internet.Jitter >= 10)
        {
            rep.Worst(internet.Jitter >= 25 ? Severity.Bad : Severity.Warn);
            rep.Findings.Add(new(internet.Jitter >= 25 ? Severity.Bad : Severity.Warn, "Ping muy variable (jitter alto)",
                $"El ping varía {internet.Jitter:0} ms entre paquetes. En juegos se nota como saltos o «rubber-banding».",
                "Suele deberse al Wi-Fi o a otros dispositivos usando la conexión."));
        }

        // ---- Latencia con carga (bufferbloat) ----
        progress("Midiendo el ping mientras se descarga (unos 10 s)…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var loadedPing = PingMany(IPAddress.Parse("1.1.1.1"), 60, 150, cts.Token);
            var (bytes, secs) = await Download("https://speed.cloudflare.com/__down?bytes=50000000", cts.Token);
            cts.Cancel();
            var loaded = await loadedPing;
            double idle = results[1].Rtt.Count > 0 ? results[1].Avg : internet.Avg;
            double extra = Math.Max(0, (loaded.Rtt.Count > 0 ? Stats.Percentile(loaded.Rtt, 90) : 0) - idle);
            double mbps = secs > 0 ? bytes * 8 / secs / 1_000_000 : 0;
            string grade = extra < 30 ? "A" : extra < 60 ? "B" : extra < 200 ? "C" : "D";
            var lvl = extra < 30 ? Severity.Ok : extra < 100 ? Severity.Warn : Severity.Bad;
            rep.Metrics.Add(new("Descarga", $"{mbps:0} Mb/s"));
            rep.Metrics.Add(new("Ping con carga", $"+{extra:0} ms (nota {grade})", lvl));
            rep.Worst(lvl);
            rep.Findings.Add(lvl == Severity.Ok
                ? new(Severity.Ok, "Sin bufferbloat", $"Mientras se descarga a {mbps:0} Mb/s el ping solo sube {extra:0} ms.")
                : new(lvl, "Bufferbloat: el ping se dispara al descargar",
                    $"Mientras se descarga a {mbps:0} Mb/s el ping sube {extra:0} ms. Por eso hay lag cuando alguien ve vídeos o descarga.",
                    "Activa SQM o QoS en el router (a menudo llamado «Smart Queue» o «QoS adaptativo»), o limita las descargas mientras juegas."));
            rep.Samples = internet.Rtt.Concat(loaded.Rtt).ToList();
            rep.SplitAt = internet.Rtt.Count;
            rep.ChartLabel = "Ping a Internet (ms): en reposo y después durante la descarga";
        }
        catch (Exception ex)
        {
            rep.Findings.Add(new(Severity.Info, "Prueba con carga no disponible", "No se pudo descargar el archivo de prueba: " + ex.Message));
        }

        rep.Verdict = rep.Level switch
        {
            Severity.Ok => $"Tu conexión está bien: {internet.Avg:0} ms a Internet, estable y sin pérdidas.",
            Severity.Warn => "Tu conexión funciona, pero hay cosas que mejorar (detalles abajo).",
            _ => "Hay problemas de conexión que afectan a juegos y videollamadas (detalles abajo)."
        };
        return rep;
    }

    static async Task<(long bytes, double secs)> Download(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinRaptor/1.0");
        var sw = Stopwatch.StartNew();
        long total = 0;
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0) total += n;
        }
        catch (OperationCanceledException) { }
        return (total, sw.Elapsed.TotalSeconds);
    }
}

// =================================================================
//  DNS
// =================================================================

public sealed record DnsProvider(string Name, string Primary, string Secondary, string Note);

public sealed class DnsResult
{
    public DnsProvider Provider { get; init; } = null!;
    public bool IsCurrent { get; init; }
    public double Median { get; init; }
    public double Avg { get; init; }
    public int Failed { get; init; }
    public int Total { get; init; }
}

public static class DnsBench
{
    public static readonly DnsProvider[] Providers =
    {
        new("Cloudflare", "1.1.1.1", "1.0.0.1", "Muy rápido y privado"),
        new("Google", "8.8.8.8", "8.8.4.4", "Muy fiable"),
        new("Quad9", "9.9.9.9", "149.112.112.112", "Bloquea webs maliciosas"),
        new("OpenDNS", "208.67.222.222", "208.67.220.220", "De Cisco"),
        new("AdGuard", "94.140.14.14", "94.140.15.15", "Bloquea anuncios y rastreadores"),
        new("Control D", "76.76.2.0", "76.76.10.0", "Sin filtros"),
    };

    static readonly string[] Domains =
    {
        "google.com", "youtube.com", "wikipedia.org", "amazon.com", "microsoft.com", "netflix.com",
        "twitch.tv", "steampowered.com", "reddit.com", "instagram.com", "whatsapp.net", "cloudflare.com"
    };

    /// <summary>Consulta DNS tipo A por UDP. Devuelve los ms o null si falla.</summary>
    static double? Query(IPAddress server, string domain, int timeoutMs = 2000)
    {
        try
        {
            ushort id = (ushort)Random.Shared.Next(ushort.MaxValue);
            var q = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in domain.Split('.'))
            {
                q.Add((byte)label.Length);
                q.AddRange(Encoding.ASCII.GetBytes(label));
            }
            q.AddRange(new byte[] { 0, 0, 1, 0, 1 });

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Connect(server, 53);
            var sw = Stopwatch.StartNew();
            udp.Send(q.ToArray(), q.Count);
            IPEndPoint? ep = null;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var resp = udp.Receive(ref ep);
                if (resp.Length >= 12 && resp[0] == (byte)(id >> 8) && resp[1] == (byte)id)
                {
                    double ms = sw.Elapsed.TotalMilliseconds;
                    int rcode = resp[3] & 0x0F;
                    return rcode is 0 or 3 ? ms : null;
                }
            }
        }
        catch { }
        return null;
    }

    static DnsResult Bench(DnsProvider p, bool current)
    {
        var ip = IPAddress.Parse(p.Primary);
        var times = new List<double>();
        int failed = 0, total = 0;
        for (int round = 0; round < 2; round++)
            foreach (var d in Domains)
            {
                total++;
                if (Query(ip, d) is { } ms) times.Add(ms); else failed++;
            }
        return new DnsResult
        {
            Provider = p, IsCurrent = current, Total = total, Failed = failed,
            Median = times.Count > 0 ? Stats.Percentile(times, 50) : double.MaxValue,
            Avg = times.Count > 0 ? times.Average() : double.MaxValue
        };
    }

    public static async Task<AnalysisReport> Run(Action<string> progress)
    {
        var rep = new AnalysisReport { Title = "DNS más rápido" };
        var link = NetworkLatency.ActiveLink();
        var currentIps = link?.Dns.Select(a => a.ToString()).ToList() ?? new();

        var list = Providers.ToList();
        var current = currentIps.FirstOrDefault();
        DnsProvider? currentProvider = null;
        if (current is not null)
        {
            currentProvider = list.FirstOrDefault(p => p.Primary == current || p.Secondary == current);
            if (currentProvider is null)
            {
                currentProvider = new DnsProvider(current == link?.Gateway?.ToString() ? "Tu router (DNS actual)" : "Tu DNS actual",
                    current, currentIps.Skip(1).FirstOrDefault() ?? "", "El que usa tu equipo ahora");
                list.Insert(0, currentProvider);
            }
        }

        progress($"Probando {list.Count} servidores DNS con {Domains.Length} dominios cada uno…");
        var results = (await Task.WhenAll(list.Select(p => Task.Run(() => Bench(p, p == currentProvider)))))
                      .OrderBy(r => r.Failed > r.Total / 4 ? 1 : 0).ThenBy(r => r.Median).ToList();
        rep.Dns = results;

        var best = results.First(r => r.Median < double.MaxValue);
        var cur = results.FirstOrDefault(r => r.IsCurrent);
        rep.Metrics.Add(new("Más rápido", $"{best.Provider.Name} · {best.Median:0} ms", Severity.Ok));
        if (cur is not null)
            rep.Metrics.Add(new("Tu DNS actual", cur.Median < double.MaxValue ? $"{cur.Provider.Name} · {cur.Median:0} ms" : "no responde",
                cur.Median - best.Median > 15 ? Severity.Warn : Severity.Ok));
        rep.Metrics.Add(new("Dominios probados", $"{Domains.Length} × 2 rondas"));
        if (DnsTool.HasBackup) rep.Metrics.Add(new("DNS cambiado por WinRaptor", "sí · puedes restaurarlo"));

        if (cur is not null && cur.Provider.Primary == best.Provider.Primary)
        {
            rep.Verdict = $"Ya usas el DNS más rápido para tu conexión ({best.Provider.Name}, {best.Median:0} ms).";
        }
        else if (cur is not null && cur.Median - best.Median > 15)
        {
            rep.Level = Severity.Warn;
            rep.Verdict = $"{best.Provider.Name} responde en {best.Median:0} ms frente a {cur.Median:0} ms de tu DNS actual. Cambiarlo acelera la carga de webs.";
        }
        else
        {
            rep.Verdict = $"{best.Provider.Name} es el más rápido ({best.Median:0} ms), pero la diferencia con tu DNS actual es pequeña.";
        }
        rep.Findings.Add(new(Severity.Info, "Qué mejora y qué no",
            "El DNS acelera la apertura de webs y la conexión inicial a servidores. No cambia el ping dentro de una partida: ese depende de tu conexión y de la distancia al servidor del juego."));
        return rep;
    }
}

/// <summary>Cambia el DNS de los adaptadores activos guardando la configuración original para restaurarla.</summary>
public static class DnsTool
{
    sealed record Saved(string Id, int Index, string Name, string NameServer);
    static string FilePath => Path.Combine(TweakEngine.DataDir, "dns_backup.json");

    public static bool HasBackup => File.Exists(FilePath);

    static List<NetworkInterface> Active() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
        .ToList();

    static bool ValidIp(string s) => IPAddress.TryParse(s, out var a) && a.AddressFamily == AddressFamily.InterNetwork;

    public static string Apply(DnsProvider p)
    {
        if (!ValidIp(p.Primary) || (p.Secondary.Length > 0 && !ValidIp(p.Secondary))) return "Dirección DNS no válida.";
        var saved = HasBackup ? JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(FilePath)) ?? new() : new();
        var done = new List<string>();
        foreach (var ni in Active())
        {
            int idx = ni.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
            if (idx == 0) continue;
            if (!saved.Any(s => s.Id == ni.Id))
            {
                using var k = Reg.Base("HKLM").OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{ni.Id}");
                saved.Add(new Saved(ni.Id, idx, ni.Name, k?.GetValue("NameServer") as string ?? ""));
            }
            var servers = p.Secondary.Length > 0 ? $"'{p.Primary}','{p.Secondary}'" : $"'{p.Primary}'";
            var (code, output) = Shell.PowerShell($"Set-DnsClientServerAddress -InterfaceIndex {idx} -ServerAddresses ({servers}) -ErrorAction Stop; 'OK'");
            if (code == 0 && output.Contains("OK")) done.Add(ni.Name);
            else Log.Write($"DNS: no se pudo cambiar en {ni.Name}: {output.Trim()}");
        }
        File.WriteAllText(FilePath, JsonSerializer.Serialize(saved));
        Shell.Run("ipconfig /flushdns");
        var msg = done.Count > 0 ? $"DNS cambiado a {p.Name} ({p.Primary}) en: {string.Join(", ", done)}." : "No se pudo cambiar el DNS en ningún adaptador.";
        Log.Write(msg);
        return msg;
    }

    public static string Restore()
    {
        if (!HasBackup) return "El DNS no se cambió con WinRaptor.";
        var saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(FilePath)) ?? new();
        var done = new List<string>();
        foreach (var s in saved)
        {
            var servers = s.NameServer.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(ValidIp).ToList();
            var cmd = servers.Count == 0
                ? $"Set-DnsClientServerAddress -InterfaceIndex {s.Index} -ResetServerAddresses -ErrorAction Stop; 'OK'"
                : $"Set-DnsClientServerAddress -InterfaceIndex {s.Index} -ServerAddresses ({string.Join(",", servers.Select(x => $"'{x}'"))}) -ErrorAction Stop; 'OK'";
            var (code, output) = Shell.PowerShell(cmd);
            if (code == 0 && output.Contains("OK")) done.Add(s.Name);
            else Log.Write($"DNS: no se pudo restaurar {s.Name}: {output.Trim()}");
        }
        File.Delete(FilePath);
        Shell.Run("ipconfig /flushdns");
        var msg = $"DNS original restaurado en: {string.Join(", ", done)}.";
        Log.Write(msg);
        return msg;
    }
}

// =================================================================
//  Microtirones (latencia del sistema)
// =================================================================

public static class Stutter
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint p);

    // Temporizador de alta resolución (Windows 10 1803+): despierta a ~1 ms aunque Windows 11
    // ignore timeBeginPeriod en procesos sin ventana visible.
    const uint HighResolution = 0x2, TimerAllAccess = 0x1F0003;
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr arg, IntPtr wake, uint tolerableDelay);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    public static async Task<AnalysisReport> Run(Action<string> progress, int seconds = 15)
    {
        var rep = new AnalysisReport { Title = "Microtirones" };
        progress($"Midiendo la latencia del sistema durante {seconds} s. No uses el equipo mientras tanto…");

        // Tiempo de CPU de los drivers (DPC/ISR) en paralelo, sin depender del idioma de Windows.
        var dpcTask = Task.Run(() => Shell.PowerShell(
            $"1..{seconds} | ForEach-Object {{ $p = Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation; " +
            "$t = $p | Where-Object Name -eq '_Total'; $c = $p | Where-Object { $_.Name -notmatch '_Total' }; " +
            "\"$($t.PercentDPCTime)|$($t.PercentInterruptTime)|$(($c | Measure-Object PercentDPCTime -Maximum).Maximum)|$(($c | Measure-Object PercentInterruptTime -Maximum).Maximum)\"; Start-Sleep -Milliseconds 700 }",
            (seconds + 30) * 1000));

        // Un hilo de prioridad alta pide despertar cada 1 ms y mide cuánto tarda realmente.
        var gaps = await Task.Run(() =>
        {
            var list = new List<double>(seconds * 1100);
            var t = new Thread(() =>
            {
                timeBeginPeriod(1);
                var timer = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, TimerAllAccess);
                try
                {
                    var sw = Stopwatch.StartNew();
                    double last = 0;
                    while (sw.Elapsed.TotalSeconds < seconds)
                    {
                        if (timer != IntPtr.Zero)
                        {
                            long due = -10_000; // 1 ms relativo, en unidades de 100 ns
                            SetWaitableTimerEx(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);
                            WaitForSingleObject(timer, 1000);
                        }
                        else Thread.Sleep(1);
                        double now = sw.Elapsed.TotalMilliseconds;
                        list.Add(now - last);
                        last = now;
                    }
                }
                finally
                {
                    if (timer != IntPtr.Zero) CloseHandle(timer);
                    timeEndPeriod(1);
                }
            }) { Priority = ThreadPriority.Highest, IsBackground = true };
            t.Start();
            t.Join();
            return list.Skip(50).ToList(); // descarta el arranque
        });

        var dev = gaps.Select(g => Math.Max(0, g - 1.0)).ToList(); // retraso sobre lo pedido
        double p50 = Stats.Percentile(dev, 50), p99 = Stats.Percentile(dev, 99), p999 = Stats.Percentile(dev, 99.9), max = dev.DefaultIfEmpty(0).Max();
        int spikes4 = dev.Count(d => d > 4), spikes10 = dev.Count(d => d > 10);

        // Si casi todas las esperas duran ~15 ms no son tirones: Windows no concedió el
        // temporizador de 1 ms y la medida no es fiable. Se dice así en vez de dar una falsa alarma.
        if (p50 > 5)
        {
            rep.Level = Severity.Info;
            rep.Metrics.Add(new("Resolución obtenida", $"{p50 + 1:0.0} ms (se pidió 1 ms)", Severity.Info));
            rep.Findings.Add(new(Severity.Info, "No se pudo medir con precisión",
                "Windows no dio a WinRaptor un temporizador de 1 ms (suele pasar si la ventana está minimizada o el equipo en ahorro de energía).",
                "Deja WinRaptor en primer plano, conecta el cargador si es un portátil y pulsa «Repetir»."));
            rep.Verdict = "Medida no fiable: repite la prueba con WinRaptor en primer plano.";
            return rep;
        }

        var (_, dpcOut) = await dpcTask;
        var dpc = dpcOut.Split('\n').Select(l => l.Trim().Split('|')).Where(p => p.Length == 4)
            .Select(p => p.Select(x => double.TryParse(x, out var v) ? v : 0).ToArray()).Skip(1).ToList();
        double dpcAvg = dpc.Count > 0 ? dpc.Average(x => x[0]) : 0, isrAvg = dpc.Count > 0 ? dpc.Average(x => x[1]) : 0;
        double dpcCore = dpc.Count > 0 ? dpc.Max(x => x[2]) : 0, isrCore = dpc.Count > 0 ? dpc.Max(x => x[3]) : 0;

        Severity Lv(double v, double w, double b) => v >= b ? Severity.Bad : v >= w ? Severity.Warn : Severity.Ok;
        rep.Metrics.Add(new("Retraso típico", $"{p50:0.00} ms", Lv(p50, 1, 3)));
        rep.Metrics.Add(new("Retraso p99", $"{p99:0.0} ms", Lv(p99, 2, 5)));
        rep.Metrics.Add(new("Peor pico", $"{max:0.0} ms", Lv(max, 6, 16)));
        rep.Metrics.Add(new("Picos > 4 ms", $"{spikes4}", Lv(spikes4, 5, 30)));
        rep.Metrics.Add(new("Picos > 10 ms", $"{spikes10}", Lv(spikes10, 1, 5)));
        rep.Metrics.Add(new("CPU en DPC (media)", $"{dpcAvg:0.0} %", Lv(dpcAvg, 1.5, 4)));
        rep.Metrics.Add(new("DPC máx. en un núcleo", $"{dpcCore:0} %", Lv(dpcCore, 20, 40)));
        rep.Metrics.Add(new("Interrupciones máx. en un núcleo", $"{isrCore:0} %", Lv(isrCore, 20, 40)));

        rep.Samples = Stats.Downsample(dev, 400);
        rep.Threshold = 4;
        rep.ChartLabel = $"Retraso del sistema en {seconds} s (ms) · la línea marca 4 ms";
        var buckets = new (string Name, double Lo, double Hi)[]
        {
            ("Menos de 0,5 ms", 0, 0.5), ("0,5 – 1 ms", 0.5, 1), ("1 – 2 ms", 1, 2), ("2 – 4 ms", 2, 4), ("Más de 4 ms (tirón)", 4, double.MaxValue)
        };
        rep.Bars = buckets.Select((b, i) =>
        {
            int c = dev.Count(x => x >= b.Lo && x < b.Hi);
            var lvl = c == 0 ? Severity.Ok : i == 4 ? Severity.Bad : i == 3 ? Severity.Warn : Severity.Ok;
            return new ChartBar(b.Name, c, $"{c * 100.0 / Math.Max(dev.Count, 1):0.##} %", lvl);
        }).ToList();
        rep.BarsLabel = $"Reparto de las {dev.Count} esperas medidas";

        foreach (var m in rep.Metrics) rep.Worst(m.Level);

        // Un pico de 16 ms equivale a perder un fotograma completo a 60 FPS.
        if (max >= 16 || spikes10 >= 5)
            rep.Findings.Add(new(Severity.Bad, "Hay bloqueos capaces de saltar fotogramas",
                $"El sistema se quedó parado hasta {max:0} ms ({spikes10} veces más de 10 ms). A 60 FPS cada fotograma dura 16,7 ms.",
                "Casi siempre es un driver: red (sobre todo Wi-Fi), audio, GPU o almacenamiento. Actualízalos desde la web del fabricante."));
        else if (spikes4 >= 5)
            rep.Findings.Add(new(Severity.Warn, "Algunos picos de latencia",
                $"{spikes4} retrasos de más de 4 ms. No suele notarse en el escritorio, pero sí en juegos competitivos o audio en directo."));
        else
            rep.Findings.Add(new(Severity.Ok, "Latencia del sistema muy buena", $"El peor retraso fue de {max:0.0} ms. No hay causas de microtirones a nivel del sistema."));

        if (dpcCore >= 20 || isrCore >= 20)
            rep.Findings.Add(new(Severity.Warn, "Un driver acapara un núcleo",
                $"Hasta {Math.Max(dpcCore, isrCore):0} % de un núcleo ocupado en DPC/interrupciones.",
                "Para saber qué driver es exactamente, usa LatencyMon (gratuito) o el Analizador de rendimiento de Windows."));

        var link = NetworkLatency.ActiveLink();
        bool wifiUp = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up);
        if (rep.Level >= Severity.Warn && wifiUp)
            rep.Findings.Add(new(Severity.Info, "El Wi-Fi está encendido",
                link?.Kind == "Wi-Fi" ? "Los drivers de Wi-Fi son una de las causas más habituales de picos de DPC." : "Usas cable pero el Wi-Fi sigue activo y buscando redes; sus drivers son una causa habitual de picos.",
                link?.Kind == "Wi-Fi" ? "Prueba con cable y actualiza el driver inalámbrico." : "Desactiva el Wi-Fi mientras juegas y repite la prueba."));

        var (_, scheme) = Shell.Run("powercfg /getactivescheme");
        // Solo «Equilibrado» y «Economizador»: un plan personalizado de alto rendimiento tiene otro GUID.
        bool savingPlan = scheme.Contains("381b4222-f694-41f0-9685-ff5bb260df2e", StringComparison.OrdinalIgnoreCase)
                       || scheme.Contains("a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.OrdinalIgnoreCase);
        if (rep.Level >= Severity.Warn && savingPlan)
            rep.Findings.Add(new(Severity.Info, "El plan de energía ahorra energía",
                "El ahorro de energía de CPU, PCIe y USB añade latencia al despertar los componentes.",
                "Aplica en «CPU y energía»: Alto rendimiento, Sin ahorro de energía en PCIe y Desactivar la suspensión selectiva de USB."));

        rep.Verdict = rep.Level switch
        {
            Severity.Ok => "Sin microtirones causados por el sistema. Si un juego da tirones, el motivo está en el propio juego, la GPU o la temperatura.",
            Severity.Warn => "Hay algunos picos de latencia. Pueden provocar tirones ocasionales en juegos.",
            _ => "Hay bloqueos del sistema que causan microtirones y cortes de audio."
        };
        return rep;
    }
}

// =================================================================
//  ¿Por qué va lento?
// =================================================================

public static class Slowness
{
    public static async Task<AnalysisReport> Run(Action<string> progress)
    {
        var rep = new AnalysisReport { Title = "Rendimiento del PC" };
        progress("Midiendo CPU, memoria y disco durante unos segundos…");
        const string script =
            "$n=[Environment]::ProcessorCount; $s=@();" +
            "for($i=0;$i -lt 7;$i++){" +
            " $c=Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation -Filter \"Name='_Total'\";" +
            " $m=Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory;" +
            " $d=Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter \"Name='_Total'\";" +
            " if($i -gt 0){ $s += [pscustomobject]@{cpu=[double]$c.PercentProcessorTime; perf=[double]$c.PercentProcessorPerformance; avail=[double]$m.AvailableMBytes; hard=[double]$m.PageReadsPersec; commit=[double]$m.PercentCommittedBytesInUse; busy=[double](100-$d.PercentIdleTime); queue=[double]$d.CurrentDiskQueueLength} };" +
            " Start-Sleep -Milliseconds 800 };" +
            "$null = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process; Start-Sleep -Milliseconds 1000;" +
            "$procs = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | Where-Object { $_.Name -notin '_Total','Idle' };" +
            "$topCpu = @($procs | Sort-Object PercentProcessorTime -Descending | Select-Object -First 5 | ForEach-Object { [pscustomobject]@{name=$_.Name; v=[math]::Round($_.PercentProcessorTime/$n,1)} });" +
            "$topIo = @($procs | Sort-Object IODataBytesPersec -Descending | Select-Object -First 3 | ForEach-Object { [pscustomobject]@{name=$_.Name; v=[math]::Round($_.IODataBytesPersec/1MB,1)} });" +
            "$topMem = @(Get-Process | Group-Object ProcessName | ForEach-Object { [pscustomobject]@{name=$_.Name; v=[math]::Round((($_.Group | Measure-Object WorkingSet64 -Sum).Sum)/1MB)} } | Sort-Object v -Descending | Select-Object -First 5);" +
            "@{ samples=$s; topCpu=$topCpu; topIo=$topIo; topMem=$topMem; totalMB=[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1MB) } | ConvertTo-Json -Depth 4 -Compress";

        var (_, output) = await Task.Run(() => Shell.PowerShell(script, 120_000));
        var json = output.Trim();
        int start = json.IndexOf('{');
        if (start < 0) { rep.Level = Severity.Info; rep.Verdict = "No se pudieron leer los contadores de rendimiento."; return rep; }
        using var doc = JsonDocument.Parse(json[start..]);
        var root = doc.RootElement;

        double Avg(string k) => root.GetProperty("samples").EnumerateArray().Select(e => e.GetProperty(k).GetDouble()).DefaultIfEmpty(0).Average();
        double MaxOf(string k) => root.GetProperty("samples").EnumerateArray().Select(e => e.GetProperty(k).GetDouble()).DefaultIfEmpty(0).Max();
        List<(string name, double v)> Top(string k) => root.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(e => (e.GetProperty("name").GetString() ?? "", e.GetProperty("v").GetDouble())).ToList()
            : new();

        double cpu = Avg("cpu"), perf = Avg("perf"), avail = Avg("avail"), hard = Avg("hard"), commit = Avg("commit"), busy = Avg("busy"), queue = MaxOf("queue");
        double total = root.GetProperty("totalMB").GetDouble();
        double ramPct = total > 0 ? (total - avail) / total * 100 : 0;
        var topCpu = Top("topCpu"); var topIo = Top("topIo"); var topMem = Top("topMem");

        Severity Lv(double v, double w, double b) => v >= b ? Severity.Bad : v >= w ? Severity.Warn : Severity.Ok;
        rep.Metrics.Add(new("CPU", $"{cpu:0} %", Lv(cpu, 70, 90)));
        rep.Metrics.Add(new("Velocidad de la CPU", $"{perf:0} % de la nominal", cpu > 40 && perf < 70 ? Severity.Warn : Severity.Ok));
        rep.Metrics.Add(new("RAM en uso", $"{ramPct:0} % ({(total - avail) / 1024:0.0} de {total / 1024:0} GB)", Lv(ramPct, 80, 92)));
        rep.Metrics.Add(new("Memoria comprometida", $"{commit:0} %", Lv(commit, 80, 92)));
        rep.Metrics.Add(new("Lecturas de paginación", $"{hard:0}/s", Lv(hard, 50, 300)));
        rep.Metrics.Add(new("Disco ocupado", $"{busy:0} %", Lv(busy, 60, 90)));
        rep.Metrics.Add(new("Cola de disco (máx.)", $"{queue:0}", Lv(queue, 3, 8)));
        rep.Metrics.Add(new("Programas de inicio", $"{Startup.List().Count(e => e.Enabled)}"));

        var bottlenecks = new List<string>();
        if (cpu >= 70)
        {
            bottlenecks.Add("CPU");
            rep.Findings.Add(new(cpu >= 90 ? Severity.Bad : Severity.Warn, "La CPU va al límite",
                $"{cpu:0} % de uso medio. Lo que más consume: {string.Join(", ", topCpu.Take(3).Select(p => $"{p.name} ({p.v:0} %)"))}.",
                "Cierra lo que no uses. Si es un proceso desconocido que consume mucho, analízalo con el antivirus."));
        }
        if (cpu > 40 && perf < 70)
        {
            bottlenecks.Add("CPU limitada");
            rep.Findings.Add(new(Severity.Warn, "La CPU está bajando su velocidad",
                $"Trabaja al {perf:0} % de su frecuencia nominal estando ocupada. Suele ser por temperatura o por el plan de energía.",
                "Usa el plan Alto rendimiento, limpia el polvo de los ventiladores y, en portátiles, conecta el cargador."));
        }
        if (ramPct >= 80 || commit >= 85 || hard >= 50)
        {
            bottlenecks.Add("RAM");
            rep.Findings.Add(new(ramPct >= 92 || hard >= 300 ? Severity.Bad : Severity.Warn, "Falta memoria RAM",
                $"{ramPct:0} % de RAM en uso y {hard:0} lecturas/s desde el archivo de paginación. Lo que más ocupa: {string.Join(", ", topMem.Take(3).Select(p => $"{p.name} ({p.v / 1024:0.0} GB)"))}.",
                "Cierra pestañas del navegador y programas abiertos, revisa los programas de inicio o amplía la RAM."));
        }
        if (busy >= 60 || queue >= 3)
        {
            bottlenecks.Add("disco");
            rep.Findings.Add(new(busy >= 90 ? Severity.Bad : Severity.Warn, "El disco va saturado",
                $"{busy:0} % del tiempo ocupado. Lo que más lee y escribe: {string.Join(", ", topIo.Select(p => $"{p.name} ({p.v:0.#} MB/s)"))}.",
                "Si es Windows Update, Search o el antivirus, espera a que terminen. Si tu sistema está en un disco duro, pasar a un SSD es la mejora más grande posible."));
        }

        if (topMem.Count > 0)
        {
            rep.Bars = topMem.Select(p => new ChartBar(p.name, p.v, p.v >= 1024 ? $"{p.v / 1024:0.0} GB" : $"{p.v:0} MB",
                p.v / Math.Max(total, 1) >= 0.25 ? Severity.Warn : Severity.Info)).ToList();
            rep.BarsLabel = "Programas que más memoria ocupan";
        }

        rep.Findings.Add(new(Severity.Info, "Lo que más CPU usa ahora",
            topCpu.Count == 0 ? "Sin datos." : string.Join(" · ", topCpu.Select(p => $"{p.name} {p.v:0.#} %"))));
        rep.Findings.Add(new(Severity.Info, "Lo que más memoria ocupa",
            topMem.Count == 0 ? "Sin datos." : string.Join(" · ", topMem.Select(p => $"{p.name} {(p.v >= 1024 ? $"{p.v / 1024:0.0} GB" : $"{p.v:0} MB")}"))));

        // Comprobaciones rápidas que también afectan a la sensación de lentitud.
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (up.TotalDays >= 7)
            rep.Findings.Add(new(Severity.Warn, "Muchos días sin reiniciar", $"{(int)up.TotalDays} días encendido.", "Reinicia: libera memoria y termina actualizaciones pendientes."));
        var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        if ((double)sys.AvailableFreeSpace / sys.TotalSize < 0.1)
            rep.Findings.Add(new(Severity.Warn, "Disco del sistema casi lleno", $"{sys.AvailableFreeSpace / 1073741824.0:0} GB libres.", "Con menos de un 10 % libre Windows se ralentiza. Usa «Limpiar temporales»."));

        foreach (var m in rep.Metrics) rep.Worst(m.Level);
        foreach (var f in rep.Findings) rep.Worst(f.Level);
        rep.Verdict = bottlenecks.Count == 0
            ? "Tu PC no tiene ningún cuello de botella ahora mismo: CPU, memoria y disco van holgados."
            : $"Cuello de botella: {string.Join(" y ", bottlenecks)}. Abajo tienes qué lo causa y cómo aliviarlo.";
        return rep;
    }
}
