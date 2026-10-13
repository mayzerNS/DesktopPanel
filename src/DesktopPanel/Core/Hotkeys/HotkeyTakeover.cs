using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Core.Hotkeys;

/// <summary>
/// «Забирать у Windows» сочетания, которые она держит сама (Win+C открывает поиск и т.п.) — RegisterHotKey их не даёт.
/// Включается только по желанию пользователя (настройка), потому что это низкоуровневый хук клавиатуры
/// (WH_KEYBOARD_LL): он работает внутри нашей программы, ничего не внедряет в чужие процессы, но видит все нажатия.
/// Программа пропускает всё, кроме одного выбранного сочетания: его «съедает», чтобы не сработало окно Windows,
/// и после отпускания Win посылает пустую клавишу — иначе Windows открыла бы меню «Пуск».
/// </summary>
public sealed class HotkeyTakeover : IDisposable
{
    private readonly Dictionary<int, (Hotkey Key, Action Action)> _items = new();
    private readonly object _gate = new();
    private readonly HashSet<uint> _swallowedDown = new();
    private readonly LowLevelKeyboardProc _proc; // держим ссылку, иначе сборщик мусора уберёт делегат
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private IntPtr _hook;
    private int _nextId = 1;
    private Thread? _thread;
    private uint _threadId;

    public HotkeyTakeover()
    {
        _proc = HookProc;
    }

    /// <summary>Идёт запись сочетания в настройках — ничего не забираем, нажатие доходит до поля записи.</summary>
    public bool Paused { get; set; }

    private volatile Action<Hotkey>? _record;

    /// <summary>
    /// Запись сочетания в настройках: Win+клавиша съедается (иначе Windows откроет свою панель раньше поля записи)
    /// и уходит в onKey. Хук живёт только на время записи, если других забранных сочетаний нет.
    /// </summary>
    public void StartRecording(Action<Hotkey> onKey)
    {
        _record = onKey;
        EnsureHookThread();
    }

    public void StopRecording()
    {
        _record = null;
        bool empty;
        lock (_gate) empty = _items.Count == 0;
        if (empty) StopHookThread();
    }

    public int Register(Hotkey hk, Action action)
    {
        if (!hk.IsValid) return 0;
        if (!EnsureHookThread()) return 0;
        int id;
        lock (_gate)
        {
            id = _nextId++;
            _items[id] = (hk, action);
        }
        Log.Info($"takeover: {hk} забираю у Windows");
        return id;
    }

    public void Unregister(int id)
    {
        bool empty;
        lock (_gate)
        {
            _items.Remove(id);
            empty = _items.Count == 0;
        }
        if (empty) StopHookThread();
    }

    /// <summary>
    /// Хук живёт в своём потоке с очередью сообщений. В потоке интерфейса он не годится: пока тот занят
    /// (мини-браузер рисует видео), Windows не дожидается ответа хука и отдаёт нажатие дальше — сочетание
    /// «пролетает» в активное окно, а панель не открывается.
    /// </summary>
    private bool EnsureHookThread()
    {
        if (_thread is not null) return _hook != IntPtr.Zero;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            using (var self = Process.GetCurrentProcess())
            using (var module = self.MainModule)
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
            if (_hook == IntPtr.Zero) Log.Warn($"takeover: хук не поставить ({Marshal.GetLastWin32Error()})");
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "HotkeyTakeover", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
        if (_hook != IntPtr.Zero) return true;
        _thread = null;
        return false;
    }

    private void StopHookThread()
    {
        var t = _thread;
        if (t is null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = wParam.ToInt32();
            var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            var up = msg is WM_KEYUP or WM_SYSKEYUP;
            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                if (down && _record is { } rec && RecordKey(k.vkCode) is { } hk)
                {
                    if (_swallowedDown.Add(k.vkCode))
                    {
                        _dispatcher.BeginInvoke(() => rec(hk));
                        TapDummyKey(); // отпускание Win не откроет «Пуск»
                    }
                    return new IntPtr(1);
                }
                if (down && !Paused && Match(k.vkCode) is { } action)
                {
                    if (_swallowedDown.Add(k.vkCode)) // не автоповтор
                    {
                        _dispatcher.BeginInvoke(action);
                        Logging.PerfLog.Note("ДЕЙСТВИЕ: перехваченное сочетание (клавиша " + k.vkCode + ")");
                        // Win ещё зажата — «пустая» клавиша сейчас, иначе её отпускание откроет «Пуск»
                        if (CurrentModifiers().HasFlag(ModifierKeys.Windows)) TapDummyKey();
                    }
                    return new IntPtr(1); // съели — Windows его не увидит
                }
                if (up && _swallowedDown.Remove(k.vkCode)) return new IntPtr(1);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Во время записи: клавиша с зажатой Win (не сама Win) — сочетание для поля записи.</summary>
    private static Hotkey? RecordKey(uint vk)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        if (Hotkey.IsModifierKey(key) || key == Key.None) return null;
        var mods = CurrentModifiers();
        return mods.HasFlag(ModifierKeys.Windows) ? new Hotkey(mods, key) : null;
    }

    private Action? Match(uint vk)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        if (Hotkey.IsModifierKey(key)) return null;
        var mods = CurrentModifiers();
        lock (_gate)
        {
            foreach (var (hk, action) in _items.Values)
            {
                if (hk.Key != key || hk.Modifiers != mods) continue;
                return action;
            }
        }
        return null;
    }

    private static ModifierKeys CurrentModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var m = ModifierKeys.None;
        if (Down(VK_LWIN) || Down(VK_RWIN)) m |= ModifierKeys.Windows;
        if (Down(0x11)) m |= ModifierKeys.Control;
        if (Down(0x12)) m |= ModifierKeys.Alt;
        if (Down(0x10)) m |= ModifierKeys.Shift;
        return m;
    }

    /// <summary>Нажать и отпустить «пустую» клавишу 0xE8 (ничего не делает), чтобы отпускание Win не открыло «Пуск».</summary>
    private static void TapDummyKey()
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0xE8 } },
            new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0xE8, dwFlags = KEYEVENTF_KEYUP } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose() => StopHookThread();

    // ---------- WinAPI ----------
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const uint LLKHF_INJECTED = 0x10, INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    /// <summary>INPUT с объединением: размер — как у самого большого варианта (MOUSEINPUT, 32 байта на x64).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
