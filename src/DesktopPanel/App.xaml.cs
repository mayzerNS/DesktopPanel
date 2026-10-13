using System.Windows;
using System.Windows.Threading;
using DesktopPanel.Core;
using DesktopPanel.Core.Config;
using DesktopPanel.Core.CrashGuard;
using DesktopPanel.Core.Hotkeys;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;
using DesktopPanel.Features.Autostart;
using DesktopPanel.Features.Desktop;
using DesktopPanel.Features.Display;
using DesktopPanel.Features.Pip;
using DesktopPanel.Features.TgProxy;
using DesktopPanel.Features.Tray;
using DesktopPanel.Features.Uninstall;
using DesktopPanel.Features.Zapret;
using DesktopPanel.UI.Overlay;
using DesktopPanel.UI.Theme;
using DesktopPanel.UI.Wizard;

namespace DesktopPanel;

/// <summary>
/// Запуск и связка всех частей: один экземпляр, откат после сбоя, конфиг, словарь строк, сочетание клавиш,
/// оверлей, трей, HDR, Zapret, мини-браузер, удаление. «Остановить программу» откатывает всё, что программа меняла.
/// </summary>
public partial class App : Application
{
    public static App Instance => (App)Current;

    public AppConfig Config { get; private set; } = new();
    public OverlayWindow? Overlay { get; private set; }
    public StateJournal Journal { get; } = new();
    public ZapretService Zapret { get; private set; } = null!;
    public TgProxyService TgProxy { get; private set; } = null!;
    public IconStore Icons { get; private set; } = null!;
    public IconLoader? IconLoaderInstance => _icons;
    public PipController? Pip { get; private set; }

    /// <summary>Работает ли сочетание для открытия панели.</summary>
    public bool HotkeyRegistered => _hotkeyId > 0;

    /// <summary>Сочетание заняла Windows — программа забирает его хуком клавиатуры (настройка «Забирать у Windows»).</summary>
    public bool HotkeyShared => _hotkeyId > 0 && _hotkeys?.IsShared(_hotkeyId) == true;

    private SingleInstance? _single;
    private HotkeyManager? _hotkeys;
    private int _hotkeyId;
    private TrayIcon? _tray;
    private IconLoader? _icons;
    private DesktopScanner? _scanner;
    private WizardWindow? _wizard;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookCrashLogging();
        Log.Info($"==== запуск {typeof(App).Assembly.GetName().Version} ====");
        var uninstall = e.Args.Any(a => string.Equals(a, Uninstaller.Arg, StringComparison.OrdinalIgnoreCase));
        // перезапуск: сначала дождаться, пока прежняя копия закроется (иначе она ещё держит «один экземпляр»)
        var ri = Array.FindIndex(e.Args, a => string.Equals(a, RestartArg, StringComparison.OrdinalIgnoreCase));
        if (ri >= 0 && ri + 1 < e.Args.Length && int.TryParse(e.Args[ri + 1], out var oldPid))
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(oldPid); old.WaitForExit(15000); }
            catch { } // уже закрылась
        }

        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            _single.SignalFirst(uninstall);
            Shutdown();
            return;
        }

        Config = ConfigStore.Load();
        Loc.Init(Config.Language);
        Accent.Init(Config.AccentColor);
        Tips.Apply(Config.TipsMode, Config.TipsOpacity);
        Motion.Configure(Config.AnimFps, !Config.AnimationsOff); // предел кадров и выключатель анимаций (меняются и на лету)

        Journal.Start();
        if (Config.ShellHotkeysOriginal is not null)
        {
            // прежние версии меняли настройку Проводника DisabledHotkeys — возвращаем как было
            ShellHotkeys.Restore(Config.ShellHotkeysOriginal);
            Config.ShellHotkeysOriginal = null;
            SaveConfig();
        }
        Zapret = new ZapretService(Config, SaveConfig, Journal);
        TgProxy = new TgProxyService(Config, SaveConfig);
        TgProxy.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            Overlay?.UpdateHeader();
            Overlay?.RefreshSettings();
        });
        Icons = new IconStore(Config, SaveConfig);
        RefreshHdr();

        DesktopView.ApplySizes(Config); // до создания панели: разметка плиток берёт размеры из ресурсов
        _icons = new IconLoader();
        Overlay = new OverlayWindow(_icons);
        Overlay.ReloadDesktop();
        Overlay.WarmUp(); // первый показ — заранее и невидимо, чтобы панель потом открывалась сразу
        _scanner = new DesktopScanner();
        _scanner.Changed += () => Dispatcher.InvokeAsync(() => Overlay.ReloadDesktop(animate: true, ifChanged: true)); // удалили/добавили — соседи доезжают; не поменялось — не пересобираем

        LogJanitor.Start(); // самоочистка папки логов (при запуске и раз в 6 ч)
        PerfLog.Start(Dispatcher); // ВРЕМЕННО: подробный лог производительности (logs\perf.log)
        Overlay.ShownChanged += PerfLog.SetWatching;

        _tray = new TrayIcon();
        _tray.OpenRequested += () => Overlay.ShowOverlay();
        _tray.SettingsRequested += () => Overlay.ShowOverlay(settings: true);
        _tray.LogsRequested += OpenLogs;
        _tray.ExitRequested += StopProgram;
        _tray.RestartRequested += RestartProgram;
        RollBackAfterCrash(Journal.LeftoverFromCrash);

        _hotkeys = new HotkeyManager { Takeover = Config.HotkeyTakeover, Recorded = hk => UI.Controls.HotkeyBox.Current?.Inject(hk) };
        RegisterMainHotkey(notifyIfBusy: Config.FirstRunDone);
        RegisterShortcuts();
        Pip = new PipController(Config, _hotkeys);
        Pip.Changed += () => Overlay?.UpdateHeader();
        Pip.RegisterToggle();
        // через несколько секунд после запуска — заранее подготовить мини-браузер (первое открытие без подвисания)
        var prewarm = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(8) };
        prewarm.Tick += (_, _) => { prewarm.Stop(); Pip?.Prewarm(); };
        prewarm.Start();

        _single.Listen(() => Dispatcher.InvokeAsync(() => Overlay.ShowOverlay()),
            () => Dispatcher.InvokeAsync(AskUninstallApp));

        Zapret.Startup(); // после отката: вернёт Zapret, если он был включён
        TgProxy.Startup();

        Task.Run(() =>
        {
            Uninstaller.Register(); // «Параметры → Приложения» (путь обновится, если exe переложили)
            if (Config.Autostart) Autostart.Enable();
        });

        if (uninstall) AskUninstallApp();
        else if (!Config.FirstRunDone) ShowWizard();
    }

    // ---------- мастер ----------
    public void ShowWizard()
    {
        Overlay?.HideOverlay();
        if (_wizard is { IsVisible: true })
        {
            _wizard.Activate();
            return;
        }
        _wizard = new WizardWindow();
        _wizard.Closed += (_, _) => _wizard = null;
        _wizard.Show();
        _wizard.Activate();
    }

    // ---------- сочетание клавиш ----------
    private void RegisterMainHotkey(bool notifyIfBusy)
    {
        var hk = Hotkey.Parse(Config.Hotkey);
        _hotkeyId = _hotkeys!.Register(hk, () => { PerfLog.Mark("горячая клавиша → панель"); Overlay?.Toggle(); });
        UpdateTrayStatus();
        if (_hotkeyId == 0 && notifyIfBusy)
            _tray?.Notify(Loc.T("notify.hotkey_busy.title"), Loc.T("notify.hotkey_busy.text", hk.ToString()), warning: true);
    }

    /// <summary>
    /// Пока в настройках записывают новое сочетание — все свои (панель, действия, браузер, мышь, перехват) отпущены,
    /// чтобы нажатое записалось, а не сработало.
    /// </summary>
    public void SuspendHotkeys(bool suspend) => _hotkeys?.Suspend(suspend);

    /// <summary>Работает ли сочетание (для точки состояния в настройках).</summary>
    public HotkeyManager.State HotkeyState(Hotkey hk) => _hotkeys?.Check(hk) ?? HotkeyManager.State.Free;

    // ---------- действия на сочетаниях (по умолчанию не назначены) ----------
    public static readonly string[] ShortcutActions = { "mini", "settings", "tasks", "trash", "search", "zapret", "tgproxy", "hdr", "icons" };
    private readonly Dictionary<string, int> _shortcutIds = new();

    /// <summary>Занять сочетания действий из настроек (при запуске и после изменения).</summary>
    public void RegisterShortcuts()
    {
        if (_hotkeys is null) return;
        foreach (var id in _shortcutIds.Values) _hotkeys.Unregister(id);
        _shortcutIds.Clear();
        foreach (var a in ShortcutActions)
        {
            if (!Config.Shortcuts.TryGetValue(a, out var s) || Hotkey.Parse(s) is not { IsValid: true } hk) continue;
            var action = a;
            _shortcutIds[a] = _hotkeys.Register(hk, () => RunShortcut(action));
        }
    }

    private void RunShortcut(string action)
    {
        var ov = Overlay;
        if (ov is null) return;
        switch (action)
        {
            case "mini": PerfLog.Mark("сочетание → уголок"); ov.ToggleMini(); break;
            case "settings": ov.TogglePage(OverlayWindow.PageSettings); break;
            case "tasks": ov.TogglePage(OverlayWindow.PageTasks); break;
            case "trash": ov.TogglePage(OverlayWindow.PageTrash); break;
            case "search": ov.OpenSearch(); break;
            case "zapret": if (Zapret.Enabled) Zapret.Toggle(); break;
            case "tgproxy": if (TgProxy.Enabled) TgProxy.Toggle(); break;
            case "hdr": ToggleHdr(); break;
            case "icons":
                ToggleDesktopIcons();
                Task.Delay(150).ContinueWith(_ => Dispatcher.InvokeAsync(() => Overlay?.UpdateHeader())); // Проводнику нужно мгновение
                break;
        }
    }

    /// <summary>Сменить сочетание. false — не вышло (старое остаётся). Занятое Windows — работает «прослушиванием».</summary>
    public bool TryChangeHotkey(Hotkey hotkey)
    {
        if (_hotkeys is null || !hotkey.IsValid) return false;
        // занято другой программой (а забирать у Windows нельзя) — оставляем прежнее: без сочетания панель не открыть.
        // Проверка — до регистрации: во время записи регистрация откладывается и «удаётся» всегда
        if (_hotkeys.Check(hotkey) == HotkeyManager.State.Busy) return false;
        _hotkeys.Unregister(_hotkeyId);
        _hotkeyId = _hotkeys.Register(hotkey, () => { PerfLog.Mark("горячая клавиша → панель"); Overlay?.Toggle(); });
        if (_hotkeyId == 0)
        {
            RegisterMainHotkey(notifyIfBusy: false); // вернуть прежнее
            return false;
        }
        Config.Hotkey = hotkey.ToString();
        SaveConfig();
        UpdateTrayStatus();
        return true;
    }

    /// <summary>Настройка «Забирать у Windows занятые ею сочетания» — перезанять всё.</summary>
    public void SetHotkeyTakeover(bool on)
    {
        Config.HotkeyTakeover = on;
        SaveConfig();
        if (_hotkeys is null) return;
        _hotkeys.Takeover = on;
        _hotkeys.Unregister(_hotkeyId);
        _hotkeyId = 0;
        RegisterMainHotkey(notifyIfBusy: false);
        RegisterShortcuts();
        Pip?.ReloadHotkeys();
    }

    private void UpdateTrayStatus() =>
        _tray?.SetStatus(HotkeyRegistered ? Loc.T("tray.status", Config.Hotkey) : Loc.T("tray.status_nohotkey"));

    // ---------- HDR ----------
    /// <summary>(есть ли мониторы с HDR, включён ли он).</summary>
    public (bool Supported, bool On) HdrState { get; private set; }

    public bool HdrBusy { get; private set; }

    public void RefreshHdr() => HdrState = Hdr.State();

    /// <summary>
    /// HDR могли переключить мимо программы (Win+Alt+B, «Параметры», другой монитор) — перечитать в фоне и обновить кнопку.
    /// Зовётся по смене режима экрана и при показе панели (раньше состояние читалось только при запуске).
    /// </summary>
    public void RefreshHdrAsync()
    {
        if (HdrBusy) return; // своё переключение само обновит кнопку, когда закончится
        Task.Run(Hdr.State).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (HdrBusy || t.IsFaulted || t.Result == HdrState) return;
            HdrState = t.Result;
            Log.Info($"hdr: состояние сменилось вне программы — {(t.Result.On ? "вкл" : "выкл")}");
            Overlay?.UpdateHdrButton();
        }));
    }

    public void ToggleHdr()
    {
        if (HdrBusy || !HdrState.Supported) return;
        var target = !HdrState.On;
        var before = HdrState.On;
        HdrBusy = true;
        HdrState = (true, target); // кнопка сразу показывает новое состояние
        Overlay?.UpdateHdrButton();
        Journal.Update(s => s.HdrOriginal ??= before);
        Task.Run(() =>
        {
            try { Hdr.Set(target); }
            catch (Exception ex) { Log.Error("hdr: не переключить", ex); }
            Thread.Sleep(600); // Windows переключает мониторы не мгновенно
            var state = Hdr.State();
            Dispatcher.InvokeAsync(() =>
            {
                HdrState = state;
                HdrBusy = false;
                Overlay?.UpdateHdrButton();
            });
        });
    }

    // ---------- действия из интерфейса ----------
    public void SaveConfig() => ConfigStore.Save(Config);

    public void SetAutostart(bool on)
    {
        Config.Autostart = on;
        SaveConfig();
        Task.Run(() =>
        {
            if (on) Autostart.Enable();
            else Autostart.Disable();
        });
    }

    public void ToggleDesktopIcons()
    {
        var wasVisible = DesktopIcons.AreVisible();
        DesktopIcons.Toggle();
        Journal.Update(s => s.IconsHiddenByUs = wasVisible);
    }

    public void OpenLogs()
    {
        try
        {
            if (!File.Exists(Log.FilePath)) Log.Info("логи открыты");
            Overlay?.HideOverlay();
            Shell.OpenLog(Log.FilePath); // копия: из %APPDATA%\DesktopPanel новый Блокнот файлы не открывает
        }
        catch (Exception ex)
        {
            Log.Error("не открыть лог", ex);
        }
    }

    private const string RestartArg = "--restart";

    /// <summary>«Перезапустить» (трей): выйти как обычно (всё откатив) и запустить программу заново.</summary>
    public void RestartProgram()
    {
        if (_exiting) return;
        Log.Info("перезапуск");
        try
        {
            // новая копия ждёт, пока эта закроется; права — те же (запускаем от себя)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.Exe)
            {
                Arguments = $"{RestartArg} {Environment.ProcessId}",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(Paths.Exe) ?? "",
            });
        }
        catch (Exception ex)
        {
            Log.Error("перезапуск: не запустить новую копию", ex);
            return;
        }
        StopProgram();
    }

    /// <summary>«Остановить программу»: вернуть всё как было и выйти.</summary>
    public void StopProgram()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("выход: откатываю изменения");
        Overlay?.Hide();
        Safe("pip", () => Pip?.Shutdown());
        Safe("zapret", () => Zapret?.Shutdown());
        Safe("tgproxy", () => TgProxy?.Shutdown());
        RollBack(Journal.Current);
        Journal.Finish();
        Shutdown();
    }

    // ---------- удаление ----------
    public void AskUninstallZapret()
    {
        Overlay?.Confirm(Loc.T("uninstall.zapret.title"), Loc.T("uninstall.zapret.text", ZapretPaths.Dir),
            Loc.T("uninstall.zapret.ok"), _ =>
            {
                Zapret.Uninstall();
                Overlay?.ShowPage(OverlayWindow.PageSettings);
            });
    }

    public void AskUninstallApp()
    {
        Overlay?.Confirm(Loc.T("uninstall.app.title"), Loc.T("uninstall.app.text", Paths.Exe),
            Loc.T("uninstall.app.ok"), wipe => Dispatcher.InvokeAsync(() => UninstallApp(wipe), DispatcherPriority.Background),
            (Loc.T("uninstall.app.wipe"), Loc.T("uninstall.app.wipe.sub"), false));
    }

    /// <summary>
    /// Удалить программу: вернуть значки и HDR, удалить Zapret со всем, что он ставил, автозапуск,
    /// запись в «Приложениях», настройку Проводника для Win+буква; файлы — после выхода.
    /// </summary>
    private void UninstallApp(bool wipeSettings)
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info($"удаление программы (стереть настройки: {wipeSettings})");
        Overlay?.Hide();
        Safe("pip", () => Pip?.Shutdown());
        Safe("zapret", () => Zapret.UninstallNow());
        Safe("tgproxy", () => TgProxy.Shutdown()); // файлы удалятся вместе с папкой ProgramData\DesktopPanel
        Safe("ipv6", () => TgProxy.RestoreIpv6());
        RollBack(Journal.Current);
        Safe("autostart", Autostart.Disable);
        Safe("apps", Uninstaller.Unregister);
        Journal.Finish();
        Safe("files", () => Uninstaller.DeleteAfterExit(wipeSettings));
        Shutdown();
    }

    // ---------- откат ----------
    private void RollBackAfterCrash(StateJournal.State? leftover)
    {
        if (leftover is null) return;
        RollBack(leftover);
        _tray?.Notify(Loc.T("app.name"), Loc.T("notify.crash_restored"));
    }

    /// <summary>Вернуть то, что записано в журнале. Каждый шаг отдельно: ошибка одного не мешает другим.</summary>
    private static void RollBack(StateJournal.State state)
    {
        if (state.IconsHiddenByUs)
            Safe("значки", DesktopIcons.EnsureVisible);
        if (state.ZapretStartedByUs)
            Safe("zapret", ZapretService.RollBack);
        if (state.HdrOriginal is { } hdr)
            Safe("hdr", () =>
            {
                var now = Hdr.State();
                if (now.Supported && now.On != hdr) Hdr.Set(hdr);
            });
    }

    private static void Safe(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error($"откат: {what}", ex); }
    }

    // ---------- завершение и ошибки ----------
    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting && Journal.Current.Running)
        {
            // выход не через «Остановить» (например, Windows завершает работу) — всё равно откатываем
            Safe("pip", () => Pip?.Shutdown());
            Safe("zapret", () => Zapret?.Shutdown());
            Safe("tgproxy", () => TgProxy?.Shutdown());
            RollBack(Journal.Current);
            Journal.Finish();
        }
        _hotkeys?.Dispose();
        Zapret?.Dispose();
        _scanner?.Dispose();
        _icons?.Dispose();
        _tray?.Dispose();
        _single?.Dispose();
        Log.Info("==== выход ====");
        base.OnExit(e);
    }

    private void HookCrashLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("необработанная ошибка интерфейса", args.Exception);
            args.Handled = true; // не падаем из-за одной ошибки
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("необработанная ошибка", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("ошибка фоновой задачи", args.Exception);
            args.SetObserved();
        };
    }
}
