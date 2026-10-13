using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopPanel.Core;
using DesktopPanel.Core.Config;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Logging;
using DesktopPanel.Features.Zapret;

namespace DesktopPanel.Features.TgProxy;

/// <summary>
/// TG WS Proxy (github.com/Flowseal/tg-ws-proxy) — локальный MTProto-прокси для Telegram Desktop: трафик Telegram
/// идёт через WebSocket к его же серверам, сторонние серверы не нужны. Как и Zapret: скачиваем последнюю версию,
/// запускаем и останавливаем по кнопке, Telegram подключаем ссылкой tg://proxy.
/// Программа — exe с собственным значком в трее. Рядом с ним кладём папку TgWsProxy_data («переносной режим»):
/// там её настройки (порт, секрет) и метка «первый запуск пройден», чтобы не открывалось окно-инструкция.
/// </summary>
public sealed class TgProxyService
{
    public const string ReleaseApi = "https://api.github.com/repos/Flowseal/tg-ws-proxy/releases/latest";
    private const string Asset = "TgWsProxy_windows.exe";
    private const string ProcessName = "TgWsProxy";

    public static string Dir => Path.Combine(Paths.SharedData, "tgproxy");
    public static string Exe => Path.Combine(Dir, ProcessName + ".exe");
    public static string DataDir => Path.Combine(Dir, "TgWsProxy_data");
    public static string LogFile => Path.Combine(DataDir, "proxy.log");

    private readonly AppConfig _cfg;
    private readonly Action _save;
    private readonly object _gate = new();
    private Timer? _updateTimer;

    public TgProxyService(AppConfig cfg, Action save)
    {
        _cfg = cfg;
        _save = save;
    }

    // ---------- состояние ----------
    public bool Enabled => _cfg.TgProxyEnabled;
    public bool Installed => File.Exists(Exe);
    public string Version => _cfg.TgProxyVersion;
    public int Port => _cfg.TgProxyPort;
    public bool Running { get; private set; }

    /// <summary>Что делается сейчас (текст для интерфейса) или null.</summary>
    public string? Busy { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    /// <summary>Что-то поменялось (может прийти из фонового потока).</summary>
    public event Action? Changed;

    /// <summary>Ссылка, по которой Telegram сам добавит прокси.</summary>
    public string Link => $"tg://proxy?server=127.0.0.1&port={_cfg.TgProxyPort}&secret=dd{Secret()}";

    public void Refresh() => Running = Process.GetProcessesByName(ProcessName).Any(p =>
    {
        using (p) return true;
    });

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("tgproxy: обработчик Changed", ex); }
    }

    /// <summary>Действие в фоне; пока идёт одно — остальные не начинаются.</summary>
    private void Spawn(Action work, string busyKey, Func<string?>? done = null)
    {
        lock (_gate)
        {
            if (Busy is not null) return;
            Busy = Loc.T(busyKey);
            Error = Notice = null;
        }
        Raise();
        Task.Run(() =>
        {
            try
            {
                work();
                Notice = done?.Invoke();
            }
            catch (Exception ex)
            {
                Error = ex is TgProxyException ? ex.Message : Loc.T("tgproxy.err.generic", ex.Message);
                Log.Warn($"tgproxy: {ex.Message}");
            }
            finally
            {
                lock (_gate) Busy = null;
                Refresh();
                Raise();
            }
        });
    }

    // ---------- действия из интерфейса ----------
    /// <summary>«Подгружать прокси для Telegram»: включение скачивает и запускает, выключение — удаляет целиком.</summary>
    public void SetEnabled(bool on)
    {
        _cfg.TgProxyEnabled = on;
        if (on) _cfg.TgProxyOn = true;
        _save();
        if (on)
        {
            StartTimer();
            Spawn(() =>
            {
                if (!Installed) Update();
                StartCore();
            }, "tgproxy.busy.installing", () => Loc.T("tgproxy.notice.ready"));
        }
        else
        {
            _updateTimer?.Dispose();
            _updateTimer = null;
            Spawn(UninstallCore, "tgproxy.busy.removing", () => Loc.T("tgproxy.notice.removed"));
        }
    }

    public void Toggle()
    {
        if (Busy is not null) return;
        Refresh();
        _cfg.TgProxyOn = !Running;
        _save();
        if (Running) Spawn(StopCore, "tgproxy.busy.stopping");
        else Spawn(() =>
        {
            if (!Installed) Update();
            StartCore();
        }, "tgproxy.busy.starting");
    }

    public void Restart() => Spawn(() =>
    {
        StopCore();
        StartCore();
    }, "tgproxy.busy.starting", () => Loc.T("tgproxy.notice.restarted"));

    public void SetPort(int port)
    {
        if (port is < 1024 or > 65535 || port == _cfg.TgProxyPort) return;
        _cfg.TgProxyPort = port;
        _save();
        Refresh();
        if (Running) Restart();
        else Raise();
    }

    public void CheckUpdates(bool force = false)
    {
        if (!_cfg.TgProxyEnabled) return;
        if (!force && DateTime.Now - _cfg.TgProxyChecked < TimeSpan.FromHours(6)) return;
        Spawn(() => Update(), "tgproxy.busy.checking",
            () => force ? Loc.T("tgproxy.notice.version", _cfg.TgProxyVersion) : null);
    }

    /// <summary>Открыть ссылку tg://proxy — Telegram сам предложит подключить прокси.</summary>
    public void ConnectTelegram()
    {
        // через Проводник: Telegram должен открыться без прав администратора
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Link}\"")); }
        catch (Exception ex) { Log.Warn($"tgproxy: ссылку не открыть: {ex.Message}"); }
    }

    // ---------- IPv6 ----------
    /// <summary>Программа выключила IPv6 в Windows (и вернёт, когда переключатель выключат или программу удалят).</summary>
    public bool Ipv6Off => _cfg.Ipv6DisabledOn.Count > 0;

    public void SetIpv6Off(bool off) => Spawn(() =>
    {
        if (off && _cfg.Ipv6DisabledOn.Count == 0)
        {
            var guids = Ipv6.Disable();
            if (guids.Count == 0) throw new TgProxyException(Loc.T("tgproxy.err.ipv6_none"));
            _cfg.Ipv6DisabledOn = guids;
        }
        else if (!off)
        {
            Ipv6.Restore(_cfg.Ipv6DisabledOn);
            _cfg.Ipv6DisabledOn = new();
        }
        _save();
    }, "tgproxy.busy.ipv6");

    /// <summary>Удаление программы: вернуть IPv6 как было.</summary>
    public void RestoreIpv6()
    {
        if (_cfg.Ipv6DisabledOn.Count == 0) return;
        Ipv6.Restore(_cfg.Ipv6DisabledOn);
        _cfg.Ipv6DisabledOn = new();
        _save();
    }

    // ---------- работа ----------
    private string Secret()
    {
        if (_cfg.TgProxySecret.Length != 32)
        {
            _cfg.TgProxySecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _save();
        }
        return _cfg.TgProxySecret;
    }

    /// <summary>Настройки прокси: наши — порт, секрет, без своей проверки обновлений и автозапуска; остальное — как было.</summary>
    private void WriteProxyConfig()
    {
        Directory.CreateDirectory(DataDir);
        var file = Path.Combine(DataDir, "config.json");
        JsonObject cfg;
        try { cfg = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new() : new(); }
        catch { cfg = new(); }
        cfg["host"] = "127.0.0.1";
        cfg["port"] = _cfg.TgProxyPort;
        cfg["secret"] = Secret();
        cfg["check_updates"] = false; // обновляет DesktopPanel
        cfg["autostart"] = false;     // запускает DesktopPanel
        File.WriteAllText(file, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        // окно-инструкция первого запуска и окно-предупреждение про IPv6 не нужны: подключаем Telegram сами,
        // а про IPv6 говорит наша подсказка в настройках
        foreach (var name in new[] { ".first_run_done_mtproto", ".ipv6_warned" })
        {
            var marker = Path.Combine(DataDir, name);
            if (!File.Exists(marker)) File.WriteAllText(marker, "");
        }
    }

    private void StartCore()
    {
        Refresh();
        if (Running) return;
        if (!Installed) throw new TgProxyException(Loc.T("tgproxy.err.not_installed"));
        WriteProxyConfig();
        try
        {
            Process.Start(new ProcessStartInfo(Exe) { WorkingDirectory = Dir, UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 225)
        {
            throw new TgProxyException(Loc.T("tgproxy.err.antivirus"));
        }
        for (var i = 0; i < 30; i++)
        {
            Thread.Sleep(200);
            Refresh();
            if (Running) break;
        }
        if (!Running) throw new TgProxyException(Loc.T("tgproxy.err.closed"));
        Log.Info($"tgproxy: запущен на 127.0.0.1:{_cfg.TgProxyPort}");
    }

    private void StopCore()
    {
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            try { p.Kill(true); }
            catch (Exception ex) { Log.Warn($"tgproxy: не остановить ({ex.Message})"); }
            finally { p.Dispose(); }
        }
        for (var i = 0; i < 20; i++)
        {
            Refresh();
            if (!Running) break;
            Thread.Sleep(100);
        }
        Log.Info("tgproxy: остановлен");
    }

    private void UninstallCore()
    {
        StopCore();
        for (var i = 0; i < 10 && Directory.Exists(Dir); i++)
        {
            try { Directory.Delete(Dir, true); }
            catch { Thread.Sleep(300); }
        }
        _cfg.TgProxyOn = false;
        _cfg.TgProxyVersion = "";
        _save();
    }

    /// <summary>Скачать новую версию, если она вышла .</summary>
    private void Update()
    {
        var (tag, url) = LatestAsync().GetAwaiter().GetResult();
        _cfg.TgProxyChecked = DateTime.Now;
        _save();
        if (tag == _cfg.TgProxyVersion && Installed) return; // уже последняя
        Busy = Loc.T("tgproxy.busy.downloading", tag);
        Raise();
        Directory.CreateDirectory(Dir);
        var tmp = Exe + ".download";
        using (var src = ZapretUpdater.Http.GetStreamAsync(url).GetAwaiter().GetResult())
        using (var dst = File.Create(tmp))
            src.CopyTo(dst);
        Refresh();
        var was = Running;
        if (was) StopCore(); // работающий exe заменить нельзя
        File.Move(tmp, Exe, overwrite: true);
        _cfg.TgProxyVersion = tag;
        _save();
        Log.Info($"tgproxy: установлена {tag}");
        if (was) StartCore();
    }

    private static async Task<(string Tag, string Url)> LatestAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ReleaseApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await ZapretUpdater.Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            if (a.GetProperty("name").GetString() == Asset)
                return (tag, a.GetProperty("browser_download_url").GetString() ?? "");
        throw new TgProxyException(Loc.T("tgproxy.err.no_asset"));
    }

    private void StartTimer() =>
        _updateTimer ??= new Timer(_ => CheckUpdates(), null, TimeSpan.FromMinutes(30), TimeSpan.FromHours(1));

    // ---------- жизненный цикл ----------
    /// <summary>Запуск программы: вернуть прокси, если он был включён, и проверить обновления.</summary>
    public void Startup()
    {
        if (!_cfg.TgProxyEnabled) return;
        StartTimer();
        Task.Run(() =>
        {
            Refresh();
            if (_cfg.TgProxyOn && !Running)
                Spawn(() =>
                {
                    if (!Installed) Update();
                    StartCore();
                }, "tgproxy.busy.starting");
            else CheckUpdates();
            Raise();
        });
    }

    /// <summary>Выход из программы: прокси, запущенный нами, останавливаем.</summary>
    public void Shutdown()
    {
        _updateTimer?.Dispose();
        if (!_cfg.TgProxyEnabled) return; // не наш — чужой запущенный прокси не трогаем
        try { StopCore(); }
        catch (Exception ex) { Log.Warn($"tgproxy: остановка при выходе ({ex.Message})"); }
    }
}

public sealed class TgProxyException : Exception
{
    public TgProxyException(string message) : base(message) { }
}
