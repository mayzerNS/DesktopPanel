using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Core.Hotkeys;

/// <summary>
/// Сочетания на кнопках мыши (нажатие колеса, боковые, прокрутка с модификатором) — через Raw Input: Windows присылает
/// копию нажатий кнопок мыши в наше невидимое окно (RIDEV_INPUTSINK — и когда программа не активна). Это не хук: ничего
/// не перехватывается и не внедряется, поэтому и «съесть» нажатие нельзя — кнопка делает и своё обычное действие
/// (боковая в браузере — «назад»).
/// Своё окно в своём потоке: Raw Input шлёт сообщение на КАЖДОЕ движение мыши (до 1000 в секунду у игровых мышей) —
/// поток интерфейса это не нагружает. Подписка на мышь — только пока назначено хоть одно такое сочетание.
/// </summary>
public sealed class MouseHotkeys : IDisposable
{
    private readonly Dictionary<int, (Hotkey Key, Action Action)> _items = new();
    private readonly object _gate = new();
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private Dispatcher? _thread;
    private HwndSource? _window;
    private bool _listening;
    private int _nextId = 1;

    /// <summary>На время записи нового сочетания в настройках — нажатия не срабатывают.</summary>
    public bool Paused { get; set; }

    public int Register(Hotkey hk, Action action)
    {
        if (!hk.IsMouse || !hk.IsValid) return 0;
        int id;
        lock (_gate)
        {
            id = _nextId++;
            _items[id] = (hk, action);
        }
        Update();
        Log.Info($"mouse: {hk} назначено");
        return id;
    }

    public void Unregister(int id)
    {
        lock (_gate)
            if (!_items.Remove(id)) return;
        Update();
    }

    /// <summary>Слушать мышь, только пока есть что слушать.</summary>
    private void Update()
    {
        bool want;
        lock (_gate) want = _items.Count > 0;
        if (want == _listening) return;
        _listening = want;
        if (want) EnsureThread();
        _thread?.BeginInvoke(() => Listen(want));
    }

    private void EnsureThread()
    {
        if (_thread is not null) return;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            _window = new HwndSource(new HwndSourceParameters("DesktopPanel.Mouse")
            {
                ParentWindow = NativeMethods.HWND_MESSAGE, WindowStyle = 0,
            });
            _window.AddHook(WndProc);
            _thread = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "MouseHotkeys" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
    }

    private void Listen(bool on)
    {
        if (_window is null) return;
        var dev = new RAWINPUTDEVICE
        {
            usUsagePage = 1, usUsage = 2, // мышь
            dwFlags = on ? RIDEV_INPUTSINK : RIDEV_REMOVE,
            hwndTarget = on ? _window.Handle : IntPtr.Zero,
        };
        if (!RegisterRawInputDevices(new[] { dev }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log.Warn($"mouse: Raw Input {(on ? "не включить" : "не выключить")} ({Marshal.GetLastWin32Error()})");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        var flags = ReadButtons(lParam, out var data);
        if (flags == 0 || Paused) return IntPtr.Zero; // движение без кнопок — сразу мимо
        var button = (flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0 ? MouseKey.Middle
            : (flags & RI_MOUSE_BUTTON_4_DOWN) != 0 ? MouseKey.X1
            : (flags & RI_MOUSE_BUTTON_5_DOWN) != 0 ? MouseKey.X2
            : (flags & RI_MOUSE_WHEEL) != 0 ? (data > 0 ? MouseKey.WheelUp : MouseKey.WheelDown)
            : MouseKey.None;
        if (button == MouseKey.None) return IntPtr.Zero;
        var mods = CurrentModifiers();
        Action? action = null;
        lock (_gate)
            foreach (var (hk, a) in _items.Values)
                if (hk.Mouse == button && hk.Modifiers == mods)
                {
                    action = a;
                    break;
                }
        if (action is null) return IntPtr.Zero;
        Logging.PerfLog.Note($"ДЕЙСТВИЕ: кнопка мыши {new Hotkey(mods, Key.None, button)}");
        _ui.BeginInvoke(() =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error("mouse: ошибка обработчика", ex); }
        });
        return IntPtr.Zero;
    }

    /// <summary>Флаги кнопок из сообщения Raw Input (0 — не мышь или только движение); data — прокрутка колеса.</summary>
    private static unsafe ushort ReadButtons(IntPtr lParam, out short data)
    {
        data = 0;
        var size = 64u;
        var buf = stackalloc byte[64];
        var header = (uint)(8 + 2 * IntPtr.Size);
        if (GetRawInputData(lParam, RID_INPUT, (IntPtr)buf, ref size, header) == unchecked((uint)-1)) return 0;
        if (*(uint*)buf != RIM_TYPEMOUSE) return 0;
        // RAWMOUSE сразу за заголовком: usFlags (2), выравнивание (2), usButtonFlags (2), usButtonData (2)
        var mouse = buf + header;
        data = *(short*)(mouse + 6);
        return *(ushort*)(mouse + 4);
    }

    private static ModifierKeys CurrentModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var m = ModifierKeys.None;
        if (Down(0x5B) || Down(0x5C)) m |= ModifierKeys.Windows;
        if (Down(0x11)) m |= ModifierKeys.Control;
        if (Down(0x12)) m |= ModifierKeys.Alt;
        if (Down(0x10)) m |= ModifierKeys.Shift;
        return m;
    }

    public void Dispose()
    {
        _thread?.BeginInvokeShutdown(DispatcherPriority.Normal);
        _thread = null;
    }

    // ---------- WinAPI ----------
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0, RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x10, RI_MOUSE_BUTTON_4_DOWN = 0x40, RI_MOUSE_BUTTON_5_DOWN = 0x100,
        RI_MOUSE_WHEEL = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage, usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);
}
