using DesktopPanel.Core.Config;
using DesktopPanel.Core.Hotkeys;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Features.Pip;

/// <summary>
/// Мини-браузер: окно и его сочетания клавиш. «Показать/скрыть» работает всегда (если мини-браузер включён),
/// остальные сочетания (взаимодействие, пауза, перемотка, громкость) занимаются только пока окно на экране —
/// чтобы не отнимать клавиши у других программ.
/// </summary>
public sealed class PipController
{
    /// <summary>Действия и сочетания по умолчанию.</summary>
    public static readonly (string Action, string Default)[] Actions =
    {
        ("toggle", "Ctrl+Alt+B"),
        ("interact", "Ctrl+Alt+I"),
        ("playpause", "Ctrl+Alt+P"),
        ("back", "Ctrl+Alt+Left"),
        ("forward", "Ctrl+Alt+Right"),
        ("volup", "Ctrl+Alt+Up"),
        ("voldown", "Ctrl+Alt+Down"),
        ("mute", "Ctrl+Alt+M"),
    };

    private readonly AppConfig _cfg;
    private readonly HotkeyManager _hotkeys;
    private PipWindow? _window;
    private int _toggleId;
    private string? _toggleKey;
    private readonly List<int> _playerIds = new();

    // скрытый браузер держит в памяти ~400 МБ (процессы WebView2): через минуту без дела окно закрываем
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(60);
    private System.Windows.Threading.DispatcherTimer? _idleTimer;

    public event Action? Changed;

    public PipController(AppConfig cfg, HotkeyManager hotkeys)
    {
        _cfg = cfg;
        _hotkeys = hotkeys;
    }

    public bool IsOpen => _window is { IsVisible: true };

    /// <summary>Где сейчас окно браузера (в пикселях экрана), null — закрыто.</summary>
    internal Core.Native.NativeMethods.RECT? Bounds =>
        IsOpen && _window!.Handle != IntPtr.Zero && Core.Native.NativeMethods.GetWindowRect(_window.Handle, out var r) ? r : null;

    /// <summary>Браузер вместе с его боковой панелью, если она на экране (пиксели экрана); null — закрыт.</summary>
    internal Core.Native.NativeMethods.RECT? PanelBounds => IsOpen ? _window!.PanelRect : null;

    /// <summary>Сдвинуть окно (его расталкивает уголок, который тащат).</summary>
    public void MoveTo(int x, int y) => _window?.MoveTo(x, y);

    public static string HotkeyOf(AppConfig cfg, string action) =>
        cfg.PipHotkeys.TryGetValue(action, out var h) ? h : Actions.First(a => a.Action == action).Default;

    /// <summary>Занять «показать/скрыть» (при запуске и после смены настроек).</summary>
    public void RegisterToggle()
    {
        // ползунки настроек зовут это на каждый шаг — то же сочетание заново не перезанимаем
        var want = _cfg.PipEnabled ? HotkeyOf(_cfg, "toggle") : null;
        if (_toggleId > 0 && want == _toggleKey) return;
        _hotkeys.Unregister(_toggleId);
        _toggleId = 0;
        _toggleKey = want;
        if (want is null) return;
        _toggleId = _hotkeys.Register(Hotkey.Parse(want), Toggle);
    }

    /// <summary>
    /// Сочетания управления: пока окно на экране — все; скрыто, но звук не ставится на паузу («Пауза, когда окно скрыто»
    /// выключена) — пауза, перемотка и громкость остаются (музыка играет — её надо чем-то остановить), режим окна — нет.
    /// </summary>
    private void RegisterPlayer(bool on, bool withMode = true)
    {
        foreach (var id in _playerIds) _hotkeys.Unregister(id);
        _playerIds.Clear();
        if (!on) return;
        foreach (var (action, _) in Actions.Where(a => a.Action != "toggle" && (withMode || a.Action != "interact")))
        {
            var a = action;
            var id = _hotkeys.Register(Hotkey.Parse(HotkeyOf(_cfg, a)), () => Do(a));
            if (id > 0) _playerIds.Add(id);
        }
    }

    /// <summary>Сочетания поменяли в настройках — перезанять.</summary>
    public void ReloadHotkeys()
    {
        RegisterToggle();
        RegisterPlayerNow();
    }

    /// <summary>Сочетания управления — по тому, на экране ли окно и играет ли оно скрытым.</summary>
    private void RegisterPlayerNow()
    {
        if (IsOpen) RegisterPlayer(true);
        else RegisterPlayer(_window is not null && !_cfg.PipAutoPause, withMode: false);
    }

    /// <summary>
    /// Назначить сочетание действию (пустое — убрать). Назначается всегда: работает ли оно (свободно, забрано у Windows,
    /// занято) — показывает точка в настройках; одинаковые сочетания у разных действий не даёт сама страница настроек.
    /// </summary>
    public void Assign(string action, Hotkey hk)
    {
        _cfg.PipHotkeys[action] = hk.ToString();
        App.Instance.SaveConfig();
        ReloadHotkeys();
    }

    public void Toggle()
    {
        if (IsOpen) Hide();
        else Show();
    }

    /// <summary>Показать. interact — сразу в режиме взаимодействия (когда открыли кнопкой в панели).</summary>
    public void Show(bool interact = false)
    {
        if (!_cfg.PipEnabled) return;
        Core.Logging.PerfLog.Mark(_window is null ? "мини-браузер (первый показ)" : "мини-браузер");
        _idleTimer?.Stop();
        var wasOpen = IsOpen;
        EnsureWindow();
        _window!.ApplyStyles();
        _window.Show();
        DesktopPanel.UI.Theme.BelowOverlay.Apply(_window.Handle); // только что показанный браузер — под панелью, а не поверх
        if (interact) _window.SetInteract(true);
        if (wasOpen) return; // уже на экране — сочетания уже заняты
        App.Instance.Overlay?.AvoidPip(); // уголок не остаётся под браузером
        RegisterPlayer(true);
        Log.Info("pip: показан");
        Changed?.Invoke();
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;
        var w = new PipWindow(_cfg);
        w.Closed += (_, _) =>
        {
            if (_window != w) return;
            _window = null;
            RegisterPlayer(false);
            Changed?.Invoke();
        };
        _window = w;
    }

    /// <summary>
    /// Подготовка после запуска, чтобы первое открытие не подвисало: в фоне загружаем библиотеки WebView2,
    /// потом в простое создаём окно, не показывая его. Движок (процессы Edge, память) при этом не запускается —
    /// он стартует только при показе окна.
    /// </summary>
    public void Prewarm()
    {
        if (!_cfg.PipEnabled || _window is not null) return;
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        Task.Run(() =>
        {
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                    typeof(Microsoft.Web.WebView2.Core.CoreWebView2Environment).TypeHandle);
            }
            catch (Exception ex) { Log.Warn($"pip: подготовка не удалась: {ex.Message}"); }
            ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
            {
                if (!_cfg.PipEnabled) return;
                try { EnsureWindow(); }
                catch (Exception ex) { Log.Warn($"pip: подготовка окна не удалась: {ex.Message}"); }
            });
        });
    }

    public void Hide()
    {
        if (_window is null || !_window.IsVisible) return;
        _window.SetInteract(false);
        if (_cfg.PipAutoPause) _window.Player("pause");
        _window.Hide();
        RegisterPlayerNow(); // скрыт, но не на паузе — пауза и громкость остаются
        Changed?.Invoke();
        if (_cfg.PipFreeMemory) StartIdleTimer();
    }

    /// <summary>Скрыт дольше минуты и ничего не играет — закрываем окно вместе с движком; откроется заново за секунду-две.</summary>
    private void StartIdleTimer()
    {
        _idleTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = IdleClose };
        _idleTimer.Tick -= IdleTick;
        _idleTimer.Tick += IdleTick;
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void IdleTick(object? sender, EventArgs e)
    {
        _idleTimer?.Stop();
        if (_window is null || _window.IsVisible || !_window.EngineStarted || !_cfg.PipFreeMemory) return;
        if (_window.PlayingAudio)
        {
            _idleTimer?.Start(); // звук идёт — не обрываем, проверим ещё раз через минуту
            return;
        }
        Log.Info("pip: скрыт больше минуты — закрываю окно, освобождаю память");
        try { _window.Close(); } catch { }
    }

    /// <summary>Настройки (прозрачность, сквозной клик) поменялись — применить сразу.</summary>
    public void ApplySettings()
    {
        _window?.ApplyStyles();
        if (!_cfg.PipFreeMemory) _idleTimer?.Stop(); // выключили — скрытое окно больше не закрываем
        else if (_window is { IsVisible: false }) StartIdleTimer();
        RegisterToggle();
        if (!_cfg.PipEnabled) Hide();
        else RegisterPlayerNow(); // «Пауза, когда окно скрыто» могли переключить, пока окно скрыто
    }

    public void ResetPlacement() => _window?.ResetPlacement();

    /// <summary>Размер из настроек поменяли ползунком — применить к окну.</summary>
    public void ApplySize() => _window?.ApplySize();

    private void Do(string action)
    {
        if (_window is null) return;
        if (action == "interact")
        {
            if (IsOpen) _window.CycleMode();
        }
        else _window.Player(action); // и у скрытого окна, которое играет
    }

    /// <summary>Выход из программы: окно закрываем, браузер освобождаем.</summary>
    public void Shutdown()
    {
        RegisterPlayer(false);
        _hotkeys.Unregister(_toggleId);
        try { _window?.Close(); } catch { }
        _window = null;
    }
}
