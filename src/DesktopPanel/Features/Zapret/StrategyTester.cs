using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Features.Zapret;

/// <summary>
/// Тест стратегий: сначала «без обхода» (что вообще заблокировано), потом каждая стратегия по очереди.
/// Ход теста пишется в zapret-test.log и отдаётся в интерфейс построчно.
/// </summary>
public sealed class StrategyTester
{
    /// <summary>Проверочные сайты. Id — стабильный ключ (хранится в настройках), название — из словаря.</summary>
    public static readonly (string Id, string Url)[] Targets =
    {
        ("discord", "https://discord.com"),
        ("gateway", "https://gateway.discord.gg"),
        ("cdn", "https://cdn.discordapp.com"),
        ("updates", "https://updates.discord.com"),
        ("youtube", "https://www.youtube.com"),
        ("ytimg", "https://i.ytimg.com"),
        ("video", "https://redirector.googlevideo.com"),
        ("google", "https://www.google.com"),
    };

    /// <summary>Ключ результата «без обхода».</summary>
    public const string NoBypass = "__none__";

    public static string TargetName(string id) => Loc.T("zapret.target." + id);

    /// <summary>Строка лога (уже с временем).</summary>
    public event Action<string>? Line;

    /// <summary>Готов результат одной стратегии (или «без обхода»).</summary>
    public event Action<string, TestResult>? Result;

    /// <summary>Сейчас проверяется: номер, всего, имя.</summary>
    public event Action<int, int, string>? Progress;

    private volatile bool _stop;

    public void Stop() => _stop = true;

    /// <summary>
    /// Прогнать стратегии (результаты — событием Result). Возвращает лучшую (больше сайтов открылось, при равенстве — быстрее) или null.
    /// Вызывать в фоне: работает синхронно десятки секунд. winws.exe перед тестом и после него остановлен.
    /// </summary>
    public string? Run(string root, IReadOnlyList<string> strategies)
    {
        _stop = false;
        var results = new Dictionary<string, TestResult>();
        StartLog();
        Write(Loc.T("zapret.test.log.begin", strategies.Count));

        ZapretRuntime.Kill();
        ResolveTargets();
        Progress?.Invoke(0, strategies.Count, NoBypass);
        Write(Loc.T("zapret.test.log.nobypass"));
        Report(NoBypass, Probe(), results);

        ZapretFiles.EnsureUserLists(root);
        ZapretRuntime.EnableTcpTimestamps();
        try
        {
            for (var i = 0; i < strategies.Count && !_stop; i++)
            {
                var bat = strategies[i];
                Progress?.Invoke(i + 1, strategies.Count, bat);
                Write("");
                Write(Loc.T("zapret.test.log.strategy", i + 1, strategies.Count, Path.GetFileNameWithoutExtension(bat)));
                TestResult res;
                try
                {
                    res = ZapretRuntime.Launch(root, bat)
                        ? Probe()
                        : Failed(ZapretRuntime.ExplainCrash(), withWinwsLog: true);
                }
                catch (ZapretException ex)
                {
                    res = Failed(ex.Message, withWinwsLog: false);
                }
                ZapretRuntime.Kill();
                Report(bat, res, results);
            }
        }
        finally
        {
            ZapretRuntime.Kill();
        }

        if (_stop) Write(Loc.T("zapret.test.log.stopped"));
        var best = results.Where(kv => kv.Key != NoBypass && kv.Value.Ok > 0)
            .OrderByDescending(kv => kv.Value.Ok).ThenBy(kv => kv.Value.Ms ?? int.MaxValue)
            .Select(kv => kv.Key).FirstOrDefault();
        Write("");
        Write(best is null ? Loc.T("zapret.test.log.no_best")
                           : Loc.T("zapret.test.log.best", Path.GetFileNameWithoutExtension(best)));
        return best;
    }

    private TestResult Failed(string error, bool withWinwsLog)
    {
        Write("  ✗ " + error);
        if (withWinwsLog)
            foreach (var l in ZapretRuntime.LogTail(4)) Write("    winws: " + l);
        return TestResult.Failure(error, Targets.Length);
    }

    private void Report(string key, TestResult r, IDictionary<string, TestResult> results)
    {
        results[key] = r;
        if (r.Error is null)
            Write(Loc.T("zapret.test.log.summary", r.Ok, r.Total, r.Ms?.ToString() ?? "—"));
        Result?.Invoke(key, r);
    }

    // ---------- проверка сайтов ----------
    /// <summary>Адреса проверочных сайтов через DNS-over-HTTPS (узнаём один раз, до запуска стратегий).</summary>
    private readonly Dictionary<string, IPAddress[]> _addresses = new(StringComparer.OrdinalIgnoreCase);

    private void ResolveTargets()
    {
        var hosts = Targets.Select(t => new Uri(t.Url).Host).Distinct().ToList();
        var tasks = hosts.Select(h => DohResolver.ResolveAsync(h)).ToArray();
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(10)); }
        catch { }
        for (var i = 0; i < hosts.Count; i++)
            if (tasks[i].IsCompletedSuccessfully && tasks[i].Result.Length > 0) _addresses[hosts[i]] = tasks[i].Result;
    }

    /// <summary>Соединение по адресу из DoH (если узнали), иначе — обычный DNS. Имя сайта (SNI) — настоящее.</summary>
    private async ValueTask<Stream> Connect(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var addrs = _addresses.TryGetValue(host, out var a) ? a : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Открыть все сайты параллельно. Новое соединение на каждый тест — иначе старое, открытое
    /// до запуска стратегии, исказит результат.</summary>
    private TestResult Probe()
    {
        using var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            ConnectCallback = Connect,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

        var tasks = Targets.Select(t => ProbeOne(http, t.Url)).ToArray();
        Task.WaitAll(tasks);
        var map = new Dictionary<string, int?>();
        for (var i = 0; i < Targets.Length; i++)
        {
            var (ms, err) = tasks[i].Result;
            map[Targets[i].Id] = ms;
            var name = TargetName(Targets[i].Id);
            Write(ms is { } v ? $"  ✓ {name}: {v} {Loc.T("zapret.ms")}" : $"  ✗ {name}: {err}");
        }
        return TestResult.From(map);
    }

    /// <summary>Мс до ответа или null + причина. Читаем до 64 КБ: так видно и блокировку «обрыв после 16 КБ».
    /// Код ошибки HTTP — тоже успех: соединение прошло.</summary>
    private static async Task<(int? Ms, string Error)> ProbeOne(HttpClient http, string url)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buf = new byte[16384];
            var total = 0;
            int n;
            while (total < 65536 && (n = await s.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0) total += n;
            return ((int)sw.ElapsedMilliseconds, "");
        }
        catch (OperationCanceledException)
        {
            return (null, Loc.T("zapret.test.timeout"));
        }
        catch (Exception ex)
        {
            var e = ex;
            while (e.InnerException is not null) e = e.InnerException;
            return (null, e.Message);
        }
    }

    // ---------- лог ----------
    private readonly object _logGate = new();

    private static void StartLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ZapretPaths.TestLog)!);
            File.WriteAllText(ZapretPaths.TestLog, "");
        }
        catch (Exception ex) { Log.Warn($"zapret: лог теста не создать ({ex.Message})"); }
    }

    private void Write(string text)
    {
        var line = text.Length == 0 ? "" : $"[{DateTime.Now:HH:mm:ss}] {text}";
        lock (_logGate)
        {
            try { File.AppendAllText(ZapretPaths.TestLog, line + Environment.NewLine); }
            catch { }
        }
        Line?.Invoke(line);
    }
}
