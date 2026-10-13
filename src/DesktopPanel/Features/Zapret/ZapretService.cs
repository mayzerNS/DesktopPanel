using DesktopPanel.Core.Config;
using DesktopPanel.Core.CrashGuard;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.Zapret;

/// <summary>
/// Zapret целиком: состояние для интерфейса и все действия. Действия идут в фоне по одному
/// (пока идёт одно — Busy не null, остальные кнопки ждут). Об изменениях сообщает событие Changed
/// (может прийти из фонового потока). Никакой автоматики по событиям — только то, что нажал пользователь,
/// плюс возврат состояния после перезапуска программы и обновление раз в 3 часа.
/// </summary>
public sealed class ZapretService : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Action _save;
    private readonly StateJournal _journal;
    private readonly object _gate = new();
    private Timer? _updateTimer;
    private StrategyTester? _tester;
    private volatile bool _quitting;

    public ZapretService(AppConfig cfg, Action save, StateJournal journal)
    {
        _cfg = cfg;
        _save = save;
        _journal = journal;
    }

    // ---------- состояние ----------
    /// <summary>Что делается сейчас (текст для интерфейса) или null.</summary>
    public string? Busy { get; private set; }

    /// <summary>Чем закончилось последнее действие: ошибка…</summary>
    public string? Error { get; private set; }

    /// <summary>…или итог («Готово: …»).</summary>
    public string? Notice { get; private set; }

    public bool Running { get; private set; }

    /// <summary>Состояние службы zapret (null — не установлена).</summary>
    public string? Service { get; private set; }

    /// <summary>Ход теста: «3 из 18: general (ALT)».</summary>
    public string? TestInfo { get; private set; }

    public bool Testing => _tester is not null;

    /// <summary>Строки лога последнего теста (в памяти; целиком — в zapret-test.log).</summary>
    public List<string> TestLines { get; } = new();

    public List<DiagItem>? Diagnostics { get; private set; }

    /// <summary>Рекомендованные записи hosts и каких из них нет (null — ещё не проверяли).</summary>
    public (List<string> Recommended, List<string> Missing)? Hosts { get; private set; }

    public event Action? Changed;

    /// <summary>Новая строка лога теста.</summary>
    public event Action<string>? TestLine;

    public bool Enabled => _cfg.ZapretEnabled;
    public bool AutoUpdate => _cfg.ZapretAutoUpdate;
    public string Version => _cfg.ZapretVersion;
    private readonly object _testsGate = new();

    /// <summary>Результаты тестов (копия: сам словарь пополняется из фона).</summary>
    public IReadOnlyDictionary<string, TestResult> Tests
    {
        get
        {
            lock (_testsGate) return new Dictionary<string, TestResult>(_cfg.ZapretTests);
        }
    }

    /// <summary>Папка установленной версии ("" — Zapret не скачан или его удалил антивирус).</summary>
    public string Root
    {
        get
        {
            var r = _cfg.ZapretRoot;
            return !string.IsNullOrEmpty(r) && File.Exists(ZapretPaths.Winws(r)) ? r : "";
        }
    }

    public List<string> Strategies()
    {
        var r = Root;
        if (r.Length == 0) return new();
        try
        {
            var list = Directory.GetFiles(r, "*.bat").Select(Path.GetFileName).Select(n => n!)
                .Where(n => !n.StartsWith("service", StringComparison.OrdinalIgnoreCase)).ToList();
            list.Sort((a, b) =>
            {
                var ga = a.Equals("general.bat", StringComparison.OrdinalIgnoreCase);
                var gb = b.Equals("general.bat", StringComparison.OrdinalIgnoreCase);
                return ga != gb ? (ga ? -1 : 1) : NativeMethods.StrCmpLogicalW(a, b);
            });
            return list;
        }
        catch { return new(); }
    }

    public string Strategy
    {
        get
        {
            var s = Strategies();
            var cur = s.FirstOrDefault(n => n.Equals(_cfg.ZapretStrategy, StringComparison.OrdinalIgnoreCase));
            return cur ?? s.FirstOrDefault() ?? "";
        }
    }

    public void Refresh()
    {
        Running = ZapretRuntime.IsRunning;
        Service = ZapretRuntime.ServiceState();
    }

    private int _refreshing;

    /// <summary>То же, что Refresh, но в фоне: sc query думает десятки миллисекунд.</summary>
    public void RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        Task.Run(() =>
        {
            try { Refresh(); }
            finally
            {
                _refreshing = 0;
                Raise();
            }
        });
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("zapret: обработчик Changed", ex); }
    }

    // ---------- выполнение в фоне ----------
    /// <summary>Запустить действие в фоне. done — текст итога (или функция от результата).</summary>
    private bool Spawn(Action work, string busyKey, Func<string?>? done = null)
    {
        lock (_gate)
        {
            if (Busy is not null) return false;
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
            catch (ZapretException ex)
            {
                Error = ex.Message;
                Log.Warn($"zapret: {ex.Message}");
            }
            catch (Exception ex)
            {
                Error = Loc.T("zapret.err.generic", ex.Message);
                Log.Error("zapret", ex);
            }
            finally
            {
                lock (_gate) Busy = null;
                TestInfo = null;
                Refresh();
                Raise();
            }
        });
        return true;
    }

    private void SetBusy(string text)
    {
        lock (_gate) Busy = text;
        Raise();
    }

    // ---------- запуск / остановка ----------
    private void StartCore()
    {
        var root = Root;
        if (root.Length == 0) throw new ZapretException(Loc.T("zapret.err.not_installed"));
        if (ZapretRuntime.IsRunning) return;
        ZapretRuntime.Start(root, Strategy);
        _journal.Update(s => s.ZapretStartedByUs = true);
    }

    private void StopCore()
    {
        ZapretRuntime.Stop();
        _journal.Update(s => s.ZapretStartedByUs = false);
    }

    /// <summary>Применить новые настройки: служба — переустановить, запущенный winws — перезапустить.</summary>
    private void Reapply()
    {
        if (ZapretRuntime.ServiceState() is not null)
            ZapretRuntime.InstallService(Root, Strategy);
        else if (ZapretRuntime.IsRunning)
        {
            StopCore();
            StartCore();
        }
    }

    private void SaveCfg()
    {
        lock (_testsGate) _save();
    }

    /// <summary>Показать итог действия, сделанного прямо в интерфейсе (например, «путь скопирован»).</summary>
    public void SetNotice(string text)
    {
        Error = null;
        Notice = text;
        Raise();
    }

    // ---------- действия из интерфейса ----------
    /// <summary>«Подгружать Zapret» в настройках.</summary>
    /// <summary>
    /// «Подгружать Zapret». Выключение удаляет Zapret целиком (служба, драйвер, hosts, файлы, его настройки) —
    /// включение скачивает его заново.
    /// </summary>
    public void SetEnabled(bool on)
    {
        if (on)
        {
            _cfg.ZapretEnabled = true;
            SaveCfg();
            StartTimer();
            CheckUpdates(force: Root.Length == 0);
        }
        else
        {
            _cfg.ZapretEnabled = false;
            SaveCfg();
            Error = null;
            Uninstall();
        }
        Raise();
    }

    /// <summary>
    /// Сбросить Zapret: удалить его со всеми своими списками, фильтрами и результатами тестов и скачать заново.
    /// Zapret остаётся включённым в программе.
    /// </summary>
    public void Reset()
    {
        _tester?.Stop();
        void Work()
        {
            var wasOn = _cfg.ZapretOn || ZapretRuntime.IsRunning;
            UninstallWork();
            _cfg.ZapretOn = wasOn; // работал — после скачивания включится снова
            _cfg.ZapretEnabled = true;
            SaveCfg();
            StartTimer();
            Update();
        }
        if (Spawn(Work, "zapret.busy.resetting", () => Loc.T("zapret.notice.reset"))) return;
        Task.Run(async () =>
        {
            for (var i = 0; i < 240 && Busy is not null; i++) await Task.Delay(500);
            Spawn(Work, "zapret.busy.resetting", () => Loc.T("zapret.notice.reset"));
        });
    }

    public void SetAutoUpdate(bool on)
    {
        _cfg.ZapretAutoUpdate = on;
        SaveCfg();
        Raise();
    }

    public void CheckUpdates(bool force = false)
    {
        if (!_cfg.ZapretEnabled) return;
        if (!force && Root.Length > 0)
        {
            if (!_cfg.ZapretAutoUpdate) return;
            if (DateTime.Now - _cfg.ZapretChecked < ZapretPaths.UpdateEvery) return;
        }
        Spawn(() => Update(), "zapret.busy.checking",
            () => force ? Loc.T("zapret.notice.version", _cfg.ZapretVersion) : null);
    }

    /// <summary>Кнопка «Вкл/Выкл».</summary>
    public void Toggle()
    {
        if (Busy is not null) return;
        if (Root.Length == 0)
        {
            CheckUpdates(force: true);
            return;
        }
        Refresh();
        if (Service is not null)
        {
            if (Running) Spawn(ZapretRuntime.StopService, "zapret.busy.service_stopping");
            else Spawn(ZapretRuntime.StartService, "zapret.busy.service_starting");
            return;
        }
        _cfg.ZapretOn = !Running;
        SaveCfg();
        if (Running) Spawn(StopCore, "zapret.busy.stopping");
        else Spawn(StartCore, "zapret.busy.starting");
    }

    public void SetStrategy(string bat)
    {
        if (Busy is not null || !Strategies().Contains(bat)) return;
        _cfg.ZapretStrategy = bat;
        SaveCfg();
        AfterSetting();
    }

    private void AfterSetting()
    {
        Refresh();
        if (Running || Service is not null) Spawn(Reapply, "zapret.busy.restarting");
        Raise();
    }

    /// <summary>Игровой фильтр: режим и/или диапазон портов (proto: null — оба, "tcp", "udp").</summary>
    public void SetGame(string? mode = null, string? range = null, string? proto = null)
    {
        var root = Root;
        if (root.Length == 0 || Busy is not null) return;
        var (m, tcp, udp) = ZapretFiles.ReadGame(root);
        if (mode is not null) m = mode;
        if (range is not null)
        {
            if (proto is null or "tcp") tcp = range;
            if (proto is null or "udp") udp = range;
        }
        ZapretFiles.WriteGame(root, m, tcp, udp);
        AfterSetting();
    }

    /// <summary>Свои порты игрового фильтра: TCP и UDP разом (один перезапуск).</summary>
    public void SetPorts(string tcp, string udp)
    {
        var root = Root;
        if (root.Length == 0 || Busy is not null) return;
        var (m, _, _) = ZapretFiles.ReadGame(root);
        ZapretFiles.WriteGame(root, m, tcp, udp);
        AfterSetting();
    }

    /// <summary>Свой список поправили в редакторе — записать и применить (перезапуск, если Zapret работает).</summary>
    public void SaveUserList(string file, string text)
    {
        var root = Root;
        if (root.Length == 0) return;
        ZapretFiles.WriteUserList(root, file, text);
        ApplyLists();
    }

    public void SetIpset(string mode)
    {
        var root = Root;
        if (root.Length == 0) return;
        Spawn(() =>
        {
            ZapretFiles.SetIpsetAsync(root, mode, ZapretUpdater.Http).GetAwaiter().GetResult();
            Reapply();
        }, "zapret.busy.ipset");
    }

    public void UpdateIpset()
    {
        var root = Root;
        if (root.Length == 0) return;
        Spawn(() =>
        {
            ZapretFiles.UpdateIpsetAsync(root, ZapretUpdater.Http).GetAwaiter().GetResult();
            Reapply();
        }, "zapret.busy.ipset_download", () => Loc.T("zapret.notice.ipset"));
    }

    /// <summary>Списки поправили руками — перезапустить, чтобы winws их перечитал.</summary>
    public void ApplyLists()
    {
        var root = Root;
        if (root.Length == 0) return;
        ZapretFiles.EnsureUserLists(root);
        Refresh();
        if (Running || Service is not null)
            Spawn(Reapply, "zapret.busy.restarting", () => Loc.T("zapret.notice.restarted"));
        else
        {
            Notice = Loc.T("zapret.notice.lists_saved");
            Raise();
        }
    }

    public void SetFake(string slot, int step)
    {
        var root = Root;
        var names = root.Length > 0 ? ZapretFiles.FakeNames(root) : new();
        if (names.Count == 0 || Busy is not null) return;
        var cur = ZapretFiles.ActiveFake(root, slot);
        var i = cur is not null && names.Contains(cur) ? names.IndexOf(cur) + step : 0;
        var name = names[((i % names.Count) + names.Count) % names.Count];
        ZapretFiles.SetFake(root, slot, name);
        _cfg.ZapretFakes[slot] = name;
        SaveCfg();
        AfterSetting();
    }

    // --- hosts ---
    public void CheckHosts() => Spawn(() =>
    {
        var rec = ZapretFiles.RecommendedHostsAsync(ZapretUpdater.Http).GetAwaiter().GetResult();
        Hosts = (rec, ZapretFiles.MissingHosts(rec));
    }, "zapret.busy.hosts_check");

    public void AddHosts() => Spawn(() =>
    {
        var rec = Hosts?.Recommended ?? ZapretFiles.RecommendedHostsAsync(ZapretUpdater.Http).GetAwaiter().GetResult();
        ZapretFiles.AddHosts(rec);
        Hosts = (rec, ZapretFiles.MissingHosts(rec));
    }, "zapret.busy.hosts_write", () => Loc.T("zapret.notice.hosts_added"));

    public void RemoveHosts() => Spawn(() =>
    {
        ZapretFiles.RemoveHosts();
        if (Hosts is { } h) Hosts = (h.Recommended, ZapretFiles.MissingHosts(h.Recommended));
    }, "zapret.busy.hosts_clean", () => Loc.T("zapret.notice.hosts_removed"));

    // --- служба ---
    public void InstallService()
    {
        var root = Root;
        if (root.Length == 0) return;
        Spawn(() =>
        {
            ZapretRuntime.InstallService(root, Strategy);
            _journal.Update(s => s.ZapretStartedByUs = false); // служба живёт сама по себе — при выходе не трогаем
        }, "zapret.busy.service_install", () => Loc.T("zapret.notice.service_installed"));
    }

    public void RemoveService() => Spawn(() =>
    {
        ZapretRuntime.RemoveService();
        _cfg.ZapretOn = false;
        SaveCfg();
    }, "zapret.busy.service_remove", () => Loc.T("zapret.notice.service_removed"));

    // --- диагностика ---
    public void RunDiagnostics() => Spawn(() => Diagnostics = ZapretDiagnostics.Run(Root), "zapret.busy.diag");

    public void Fix(string key)
    {
        switch (key)
        {
            case ZapretDiagnostics.FixUpdate:
                CheckUpdates(force: true);
                return;
            case ZapretDiagnostics.FixDefender:
                ZapretDiagnostics.OpenDefenderSettings();
                return;
        }
        Spawn(() =>
        {
            ZapretDiagnostics.Fix(key, Root);
            Diagnostics = ZapretDiagnostics.Run(Root);
        }, "zapret.busy.fixing");
    }

    public void ClearDiscordCache()
    {
        List<string> done = new();
        Spawn(() => done = ZapretDiagnostics.ClearDiscordCache(), "zapret.busy.discord",
            () => done.Count > 0 ? Loc.T("zapret.notice.discord_cleared", string.Join(", ", done))
                                 : Loc.T("zapret.notice.discord_not_found"));
    }

    // --- тест ---
    public void RunTests()
    {
        var root = Root;
        if (root.Length == 0 || Busy is not null) return;
        if (ZapretRuntime.ServiceState() is not null)
        {
            Error = Loc.T("zapret.err.test_service");
            Raise();
            return;
        }
        var tester = new StrategyTester();
        tester.Line += l =>
        {
            lock (TestLines)
            {
                TestLines.Add(l);
                if (TestLines.Count > 2000) TestLines.RemoveRange(0, 500);
            }
            TestLine?.Invoke(l);
        };
        tester.Progress += (i, n, bat) =>
        {
            TestInfo = i == 0 ? Loc.T("zapret.test.nobypass")
                              : Loc.T("zapret.test.progress", i, n, Path.GetFileNameWithoutExtension(bat));
            Raise();
        };
        tester.Result += (bat, r) =>
        {
            lock (_testsGate) _cfg.ZapretTests[bat] = r;
            SaveCfg();
            Raise();
        };
        lock (TestLines) TestLines.Clear();

        string? best = null;
        var strategies = Strategies();
        var started = Spawn(() =>
        {
            _tester = tester;
            var wasOn = ZapretRuntime.IsRunning;
            try
            {
                best = tester.Run(root, strategies);
            }
            finally
            {
                _tester = null;
                SaveCfg();
                if (wasOn && !_quitting) StartCore();
                else StopCore();
            }
        }, "zapret.busy.testing",
            () => best is null ? null : Loc.T("zapret.notice.best", Path.GetFileNameWithoutExtension(best)));
        if (!started) _tester = null;
    }

    public void StopTests() => _tester?.Stop();

    // ---------- обновление ----------
    private void Update()
    {
        var (tag, url) = ZapretUpdater.LatestAsync().GetAwaiter().GetResult();
        _cfg.ZapretChecked = DateTime.Now;
        if (tag == _cfg.ZapretVersion && Root.Length > 0)
        {
            SaveCfg();
            return;
        }
        SetBusy(Loc.T("zapret.busy.downloading", tag));
        // свои списки сначала откладываем: при повторной загрузке той же версии папка перезапишется
        var oldRoot = _cfg.ZapretRoot;
        var backup = Path.Combine(ZapretPaths.Dir, "_user_backup");
        if (oldRoot.Length > 0 && Directory.Exists(oldRoot))
            ZapretUpdater.CopyUserFiles(oldRoot, backup);
        var was = ZapretRuntime.IsRunning;
        var svc = ZapretRuntime.ServiceState() is not null;
        if (was && !svc) ZapretRuntime.Stop(); // иначе драйвер держит файлы старой версии
        string newRoot;
        try { newRoot = ZapretUpdater.DownloadAsync(tag, url).GetAwaiter().GetResult(); }
        catch
        {
            if (was && !svc && Root.Length > 0) // не скачалось — возвращаем как было
                try { StartCore(); } catch { }
            throw;
        }
        if (Directory.Exists(backup))
        {
            ZapretUpdater.CopyUserFiles(backup, newRoot);
            try { Directory.Delete(backup, true); } catch { }
        }
        ZapretFiles.EnsureUserLists(newRoot);
        var fakes = ZapretFiles.FakeNames(newRoot);
        foreach (var (slot, name) in _cfg.ZapretFakes.ToList())
            if (fakes.Contains(name)) ZapretFiles.SetFake(newRoot, slot, name);

        _cfg.ZapretRoot = newRoot;
        _cfg.ZapretVersion = tag;
        SaveCfg();
        Log.Info($"zapret: установлена {tag} в {newRoot}");

        if (svc) ZapretRuntime.InstallService(newRoot, Strategy);
        else if (was || _cfg.ZapretOn) StartCore();
        ZapretUpdater.Cleanup(newRoot);
    }

    private void StartTimer()
    {
        _updateTimer ??= new Timer(_ => CheckUpdates(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    // ---------- жизненный цикл ----------
    /// <summary>Запуск программы: вернуть Zapret, если он был включён, и проверить обновления.</summary>
    public void Startup()
    {
        if (!_cfg.ZapretEnabled) return;
        StartTimer();
        Task.Run(() =>
        {
            MigrateOldDir();
            // по сырому пути из настроек: если антивирус удалил winws.exe, Root пустой — а папку со списками удалять нельзя
            if (_cfg.ZapretRoot.Length > 0)
                try { ZapretUpdater.Cleanup(_cfg.ZapretRoot); } catch { }
            Refresh();
            if (_cfg.ZapretOn && Root.Length > 0 && !Running && Service is null)
                Spawn(() =>
                {
                    StartCore();
                    if (_cfg.ZapretAutoUpdate && DateTime.Now - _cfg.ZapretChecked >= ZapretPaths.UpdateEvery)
                        try { Update(); }
                        catch (Exception ex) { Log.Warn($"zapret: обновление не удалось ({ex.Message})"); }
                }, "zapret.busy.starting");
            else
                CheckUpdates();
            Raise();
        });
    }

    /// <summary>
    /// Zapret из %LOCALAPPDATA% переносим в ProgramData: из профиля пользователя драйвер WinDivert не загружается.
    /// Копируем (новые файлы получают права папки ProgramData), старую папку удаляем. Не вышло — свои списки
    /// откладываем в _user_backup, а версию скачаем заново.
    /// </summary>
    private void MigrateOldDir()
    {
        var old = ZapretPaths.OldDir;
        if (!Directory.Exists(old)) return;
        var svc = ZapretRuntime.ServiceState() is not null;
        if (svc) ZapretRuntime.RemoveService();
        else ZapretRuntime.Stop();

        var oldRoot = _cfg.ZapretRoot;
        var inOld = oldRoot.StartsWith(old + "\\", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(ZapretPaths.Dir)) Directory.Delete(ZapretPaths.Dir, true);
            CopyTree(old, ZapretPaths.Dir);
            if (inOld) _cfg.ZapretRoot = ZapretPaths.Dir + oldRoot[old.Length..];
            // из старой папки драйвер не загружался — все прошлые результаты теста недействительны
            lock (_testsGate) _cfg.ZapretTests.Clear();
            Log.Info($"zapret: перенесён в {ZapretPaths.Dir}");
        }
        catch (Exception ex)
        {
            Log.Warn($"zapret: не перенести в {ZapretPaths.Dir} ({ex.Message}) — скачаю заново");
            if (inOld && Directory.Exists(oldRoot))
                try { ZapretUpdater.CopyUserFiles(oldRoot, Path.Combine(ZapretPaths.Dir, "_user_backup")); } catch { }
            if (inOld)
            {
                _cfg.ZapretRoot = "";
                _cfg.ZapretVersion = "";
            }
        }
        SaveCfg();
        for (var i = 0; i < 10 && Directory.Exists(old); i++)
        {
            try { Directory.Delete(old, true); }
            catch { Thread.Sleep(300); }
        }
        if (svc && Root.Length > 0)
            try { ZapretRuntime.InstallService(Root, Strategy); }
            catch (Exception ex) { Log.Warn($"zapret: служба после переноса ({ex.Message})"); }
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>Удалить Zapret полностью: службу, драйвер, записи hosts, файлы. Настройки — по умолчанию.</summary>
    public void Uninstall()
    {
        _tester?.Stop();
        if (Spawn(UninstallWork, "zapret.busy.uninstalling", () => Loc.T("zapret.notice.uninstalled"))) return;
        // идёт другое действие (тест, загрузка) — дождёмся его конца
        Task.Run(async () =>
        {
            for (var i = 0; i < 240 && Busy is not null; i++) await Task.Delay(500);
            Spawn(UninstallWork, "zapret.busy.uninstalling", () => Loc.T("zapret.notice.uninstalled"));
        });
    }

    /// <summary>То же самое, но сразу и в этом потоке (удаление всей программы).</summary>
    public void UninstallNow()
    {
        _quitting = true;
        _tester?.Stop();
        try { UninstallWork(); }
        catch (Exception ex) { Log.Error("zapret: удаление", ex); }
    }

    private void UninstallWork()
    {
        if (ZapretRuntime.ServiceState() is not null) ZapretRuntime.RemoveService();
        StopCore();
        if (ZapretFiles.HostsHasBlock()) ZapretFiles.RemoveHosts();
        ZapretUpdater.DeleteAll();
        var d = new AppConfig();
        _cfg.ZapretEnabled = false;
        _cfg.ZapretOn = false;
        _cfg.ZapretRoot = d.ZapretRoot;
        _cfg.ZapretVersion = d.ZapretVersion;
        _cfg.ZapretStrategy = d.ZapretStrategy;
        _cfg.ZapretChecked = d.ZapretChecked;
        _cfg.ZapretAutoUpdate = d.ZapretAutoUpdate;
        lock (_testsGate) _cfg.ZapretTests.Clear();
        _cfg.ZapretFakes.Clear();
        SaveCfg();
        Diagnostics = null;
        Hosts = null;
        _updateTimer?.Dispose();
        _updateTimer = null;
    }

    /// <summary>Установлен ли Zapret (есть его папка).</summary>
    public static bool Installed => Directory.Exists(ZapretPaths.Dir);

    /// <summary>Остановка программы: выключаем то, что запускали сами (службу не трогаем).</summary>
    public void Shutdown()
    {
        _quitting = true;
        _tester?.Stop();
        _updateTimer?.Dispose();
        if (ZapretRuntime.IsRunning && ZapretRuntime.ServiceState() is null)
            StopCore();
    }

    /// <summary>Откат после сбоя: winws, запущенный прошлым запуском, останавливаем.</summary>
    public static void RollBack()
    {
        if (ZapretRuntime.ServiceState() is not null) return;
        ZapretRuntime.Stop();
    }

    public void Dispose() => _updateTimer?.Dispose();
}
