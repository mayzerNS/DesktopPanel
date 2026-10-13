using System.Runtime.InteropServices;
using System.Windows.Interop;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Core.Hotkeys;

/// <summary>
/// Глобальные сочетания клавиш через RegisterHotKey — штатный способ Windows: без перехвата клавиатуры,
/// работает даже когда другая программа (или игра) зависла, антивирусы на него не реагируют.
/// Если сочетание уже занято (Windows или другой программой), Register вернёт false.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _window;
    private readonly Dictionary<int, (Hotkey Key, Action Action)> _registered = new();
    private readonly Dictionary<int, (Hotkey Key, int RawId)> _shared = new(); // наш id → id в HotkeyTakeover
    private readonly Dictionary<int, (Hotkey Key, int MouseId)> _mouse = new(); // наш id → id в MouseHotkeys
    private HotkeyTakeover? _raw;
    private MouseHotkeys? _mice;
    private bool _paused;
    private int _nextId = 1;

    public HotkeyManager()
    {
        // невидимое окно только для сообщений
        _window = new HwndSource(new HwndSourceParameters("DesktopPanel.Hotkeys")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE,
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);
    }

    /// <summary>Забирать у Windows занятые ею сочетания (хук клавиатуры, см. HotkeyTakeover). Включается в настройках.</summary>
    public bool Takeover { get; set; }

    /// <summary>Куда отдавать Win+клавишу, пойманную при записи сочетания (при включённом Takeover, см. HotkeyTakeover.StartRecording).</summary>
    public Action<Hotkey>? Recorded { get; set; }

    /// <summary>
    /// Зарегистрировать сочетание. Возвращает id (больше 0) или 0, если оно занято.
    /// Если сочетание занято, а Takeover включён, — забираем его хуком клавиатуры. Узнать — IsShared(id).
    /// </summary>
    public int Register(Hotkey hotkey, Action action, bool allowShared = true)
    {
        if (!hotkey.IsValid) return 0;
        var id = _nextId++;
        if (hotkey.IsMouse)
        {
            _mice ??= new MouseHotkeys { Paused = _paused };
            var mid = _mice.Register(hotkey, action);
            if (mid == 0) return 0;
            _mouse[id] = (hotkey, mid);
            return id;
        }
        if (_paused)
        {
            // идёт запись сочетания в настройках: займём, когда она закончится (Resume)
            _registered[id] = (hotkey, action);
            _suspended.Add(id);
            return id;
        }
        if (!NativeMethods.RegisterHotKey(_window.Handle, id, hotkey.NativeModifiers, hotkey.VirtualKey))
        {
            Log.Warn($"hotkey: {hotkey} занято (ошибка {Marshal.GetLastWin32Error()})");
            if (!allowShared || !Takeover) return 0;
            _raw ??= new HotkeyTakeover();
            var rawId = _raw.Register(hotkey, action);
            if (rawId == 0) return 0;
            _shared[id] = (hotkey, rawId);
            return id;
        }
        _registered[id] = (hotkey, action);
        Log.Info($"hotkey: {hotkey} зарегистрировано");
        return id;
    }

    public void Unregister(int id)
    {
        if (id <= 0) return;
        if (_shared.Remove(id, out var sh)) _raw?.Unregister(sh.RawId);
        else if (_mouse.Remove(id, out var ms)) _mice?.Unregister(ms.MouseId);
        else if (_registered.Remove(id))
        {
            if (!_suspended.Remove(id)) NativeMethods.UnregisterHotKey(_window.Handle, id);
        }
    }

    private readonly HashSet<int> _suspended = new();

    /// <summary>
    /// Пока в настройках записывают сочетание — все наши отпущены: иначе нажатое сочетание, уже назначенное на что-то
    /// у нас, срабатывало бы, а не записывалось (Windows отдаёт его нам раньше поля). Resume — занять снова.
    /// </summary>
    public void Suspend(bool on)
    {
        if (on == _paused) return;
        _paused = on;
        if (_raw is not null) _raw.Paused = on;
        if (_mice is not null) _mice.Paused = on;
        if (on && Takeover && Recorded is { } rec)
        {
            _raw ??= new HotkeyTakeover();
            _raw.StartRecording(rec);
        }
        else _raw?.StopRecording();
        if (on)
        {
            foreach (var id in _registered.Keys)
                if (_suspended.Add(id)) NativeMethods.UnregisterHotKey(_window.Handle, id);
            return;
        }
        foreach (var id in _suspended.ToList())
        {
            _suspended.Remove(id);
            if (!_registered.TryGetValue(id, out var e)) continue;
            if (NativeMethods.RegisterHotKey(_window.Handle, id, e.Key.NativeModifiers, e.Key.VirtualKey)) continue;
            // за время записи сочетание занял кто-то другой — забираем, если можно, иначе оно не работает
            _registered.Remove(id);
            Log.Warn($"hotkey: {e.Key} после записи занято (ошибка {Marshal.GetLastWin32Error()})");
            if (!Takeover) continue;
            _raw ??= new HotkeyTakeover();
            var rawId = _raw.Register(e.Key, e.Action);
            if (rawId != 0) _shared[id] = (e.Key, rawId);
        }
    }

    /// <summary>Состояние сочетания для настроек.</summary>
    public enum State { Free, Ours, OursShared, Mouse, Busy, BusyTakeover }

    /// <summary>
    /// Работает ли сочетание: наше и занято штатно (Ours), наше через перехват у Windows (OursShared), кнопка мыши,
    /// свободно (Free), занято другой программой или Windows (Busy; BusyTakeover — при включённом «Забирать у Windows»
    /// будет забрано).
    /// </summary>
    public State Check(Hotkey hk)
    {
        if (hk.IsMouse) return State.Mouse;
        if (_registered.Values.Any(e => e.Key == hk)) return State.Ours;
        if (_shared.Values.Any(e => e.Key == hk)) return State.OursShared;
        if (IsFree(hk)) return State.Free;
        return Takeover ? State.BusyTakeover : State.Busy;
    }

    /// <summary>Сочетание занято Windows — программа забирает его хуком клавиатуры.</summary>
    public bool IsShared(int id) => _shared.ContainsKey(id);

    /// <summary>Свободно ли сочетание прямо сейчас (пробуем занять и сразу отпускаем). Кнопки мыши — всегда.</summary>
    public bool IsFree(Hotkey hotkey)
    {
        if (hotkey.IsMouse) return true;
        const int probeId = 0xBFFF;
        if (!NativeMethods.RegisterHotKey(_window.Handle, probeId, hotkey.NativeModifiers, hotkey.VirtualKey))
            return false;
        NativeMethods.UnregisterHotKey(_window.Handle, probeId);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && !_paused && _registered.TryGetValue(wParam.ToInt32(), out var entry))
        {
            handled = true;
            Logging.PerfLog.Note($"ДЕЙСТВИЕ: горячая клавиша {entry.Key}");
            try { entry.Action(); }
            catch (Exception ex) { Log.Error($"hotkey: ошибка обработчика {entry.Key}", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys.Concat(_shared.Keys).Concat(_mouse.Keys).ToList())
            Unregister(id);
        _raw?.Dispose();
        _mice?.Dispose();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
