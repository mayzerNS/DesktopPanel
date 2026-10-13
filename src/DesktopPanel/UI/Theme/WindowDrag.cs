using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopPanel.Core.Native;

namespace DesktopPanel.UI.Theme;

/// <summary>
/// Перетаскивание окон своим ходом (панель, уголок, мини-браузер): пока нажата левая кнопка, окно ставится под курсор
/// каждый кадр монитора — не зависит от предела кадров анимаций. Курсор не сдвинулся — окно не трогаем.
/// Такт — VsyncPump: отдельный поток просыпается сразу после каждого кадра монитора (по часам Windows, которая собирает
/// картинку экрана) и просит поток интерфейса передвинуть окно. Раньше такт брался у отрисовки WPF (CompositionTarget.Rendering),
/// а она, пока окно двигают, проседала: на мониторе 200 Гц выходило 100–145 сдвигов в секунду и кадры по 30 мс.
/// Системное перетаскивание (DragMove/HTCAPTION) не годится: оно включало Aero Snap и не давало на ходу расталкивать соседние окна.
/// </summary>
internal static class WindowDrag
{

    /// <summary>
    /// Тащить окно hwnd: place получает прямоугольник «окно под курсором» и возвращает, куда его поставить на самом деле
    /// (не за край экрана, заодно — растолкать соседей). from — где нажали (по умолчанию — где курсор сейчас).
    /// apply — само перемещение, если окно двигается не одно (уголок — два окна разом); по умолчанию — SetWindowPos.
    /// threshold — нажали, но это ещё может быть щелчок: окно трогается, только когда курсор отошёл от точки нажатия
    /// на threshold пикселей (тогда зовётся begin); отпустили раньше — зовётся click, а не end. За курсором следит сам
    /// (а не по событиям мыши WPF: при резком рывке курсор успевал уйти с окна, события переставали приходить,
    /// и окно трогалось с опозданием — далеко от курсора).
    /// </summary>
    public static void Start(IntPtr hwnd, Func<NativeMethods.RECT, NativeMethods.RECT> place, Action? end = null,
        NativeMethods.POINT? from = null, Action<NativeMethods.RECT>? apply = null,
        int threshold = 0, Action? begin = null, Action? click = null)
    {
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.POINT p0;
        if (from is { } f) p0 = f;
        else NativeMethods.GetCursorPos(out p0);
        var started = threshold <= 0;
        if (started) begin?.Invoke();
        if (!NativeMethods.GetWindowRect(hwnd, out var start)) return;
        int lastX = int.MinValue, lastY = int.MinValue;
        VsyncPump? pump = null;
        pump = VsyncPump.Start(() =>
        {
            if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
            {
                pump!.Stop();
                if (started) end?.Invoke();
                else click?.Invoke();
                return;
            }
            NativeMethods.GetCursorPos(out var p);
            if (!started)
            {
                if (Math.Abs(p.X - p0.X) < threshold && Math.Abs(p.Y - p0.Y) < threshold) return;
                started = true;
                begin?.Invoke();
                NativeMethods.GetWindowRect(hwnd, out start); // begin мог изменить окно (досворачивание уголка)
            }
            if (p.X == lastX && p.Y == lastY) return;
            lastX = p.X;
            lastY = p.Y;
            var r = new NativeMethods.RECT
            {
                Left = start.Left + p.X - p0.X, Top = start.Top + p.Y - p0.Y,
                Right = start.Right + p.X - p0.X, Bottom = start.Bottom + p.Y - p0.Y,
            };
            r = place(r);
            if (apply is not null) apply(r);
            else NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            Core.Logging.PerfLog.AnimTick("перетаскивание окна");
        });
    }

    /// <summary>Рабочая область монитора, на котором середина прямоугольника.</summary>
    public static NativeMethods.RECT WorkAreaOf(NativeMethods.RECT r)
    {
        var mon = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        return NativeMethods.GetMonitorInfo(mon, ref mi) ? mi.rcWork : r;
    }

    /// <summary>Не за край рабочей области (размер не меняется).</summary>
    public static NativeMethods.RECT Clamp(NativeMethods.RECT r, NativeMethods.RECT wa)
    {
        int w = r.Width, h = r.Height;
        var x = Math.Max(wa.Left, Math.Min(r.Left, wa.Right - w));
        var y = Math.Max(wa.Top, Math.Min(r.Top, wa.Bottom - h));
        return new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    private const int VK_LBUTTON = 0x01;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}

/// <summary>
/// Такт «каждый кадр монитора» для потока интерфейса, не зависящий от отрисовки WPF. Свой поток ждёт момента сразу после
/// очередного кадра монитора (DwmGetCompositionTimingInfo: когда был последний кадр и сколько длится кадр) и ставит в очередь
/// потока интерфейса tick с наивысшим приоритетом. Пока прошлый tick не выполнен, новый не ставится (очередь не копится).
/// Если Windows не отдала расписание кадров — шаг по запомненной частоте монитора.
/// </summary>
internal sealed class VsyncPump
{
    private readonly Action _tick;
    private readonly System.Windows.Threading.Dispatcher _ui;
    private volatile bool _stop;
    private int _pending;

    private VsyncPump(Action tick)
    {
        _tick = tick;
        _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        // один делегат на всё перетаскивание, а не новый на каждый кадр монитора
        _post = () =>
        {
            if (!_stop)
            {
                var t0 = Stopwatch.GetTimestamp();
                _tick();
                var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                _n++;
                _sum += ms;
                if (ms > _max) _max = ms;
                if (_stop) Core.Logging.PerfLog.Note($"перетаскивание окна: тактов {_n}, такт сред {_sum / Math.Max(1, _n):0.00} мс, худший {_max:0.0} мс");
            }
            // ⚠ «выполнен» — только после такта: иначе долгие такты шли вплотную один за другим с приоритетом Send
            // и душили всё остальное (отклик интерфейса до 0,6 с, пока тащат браузер)
            Volatile.Write(ref _pending, 0);
        };
    }

    private readonly Action _post;
    private int _n;
    private double _sum, _max;

    public static VsyncPump Start(Action tick)
    {
        var p = new VsyncPump(tick);
        new Thread(p.Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "vsync" }.Start();
        return p;
    }

    public void Stop() => _stop = true;

    private void Run()
    {
        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        var hiRes = timer != IntPtr.Zero;
        if (!hiRes)
        {
            timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
            timeBeginPeriod(1);
        }
        var freq = Stopwatch.Frequency;
        var info = new byte[TimingSize];
        try
        {
            while (!_stop)
            {
                // до момента чуть позже следующего кадра монитора
                var now = Stopwatch.GetTimestamp();
                long wait;
                if (NextVblank(info, now) is { } next) wait = next - now + freq / 2000; // +0,5 мс: кадр уже начат
                else wait = (long)(freq / Math.Max(30.0, Motion.MonitorFps));
                var due = -Math.Max(1, wait * 10_000_000 / freq); // отрицательное — «через», в 100 нс
                if (timer != IntPtr.Zero && SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    WaitForSingleObject(timer, 100);
                else Thread.Sleep(1);
                if (_stop) break;
                // ⚠ не ставить новый такт, пока прошлый не выполнен: иначе очередь с приоритетом Send копится и душит интерфейс
                if (Interlocked.Exchange(ref _pending, 1) == 1) continue; // прошлый ещё не выполнен
                _ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, _post);
            }
        }
        finally
        {
            if (timer != IntPtr.Zero) CloseHandle(timer);
            if (!hiRes) timeEndPeriod(1);
        }
    }

    // DWM_TIMING_INFO (упакована по 1 байту): cbSize @0, qpcRefreshPeriod @12, qpcVBlank @28; размер 292
    private const int TimingSize = 292;

    private static long? NextVblank(byte[] info, long now)
    {
        Array.Clear(info);
        BitConverter.TryWriteBytes(info.AsSpan(0, 4), TimingSize);
        if (DwmGetCompositionTimingInfo(IntPtr.Zero, info) != 0) return null;
        var period = BitConverter.ToInt64(info, 12);
        var vblank = BitConverter.ToInt64(info, 28);
        if (period <= 0 || vblank <= 0) return null;
        if (vblank <= now) vblank += ((now - vblank) / period + 1) * period;
        return vblank;
    }

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, byte[] info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint ms);
}

/// <summary>
/// Уголок и мини-браузер не ложатся друг на друга: то окно, которое тащат (A), расталкивает другое (B).
/// • B уходит в сторону движения A (или вбок, если так ему ехать меньше).
/// • Упёрлось в край экрана — B перескакивает на другую сторону A и, пока A продолжают давить к стенке, едет вместе с ним,
///   как прилипшее к его обратному краю.
/// • Окно появилось или выросло поверх другого (без движения) — другое отодвигается туда, куда ему ближе.
/// Один объект — на одно перетаскивание (помнит, куда двигалось A и прилипло ли B).
/// </summary>
internal sealed class WindowPush
{
    private int _ax = int.MinValue, _ay;
    private int _stickDx, _stickDy, _dirX, _dirY;
    private bool _stuck;

    /// <summary>
    /// Куда поставить B (null — оставить на месте). gap — зазор между окнами. verticalOnly — B уходит только выше или ниже A
    /// (вбок — только если ни сверху, ни снизу места нет): так уголок уходит от мини-браузера.
    /// </summary>
    public NativeMethods.RECT? Resolve(NativeMethods.RECT a, NativeMethods.RECT b, int gap, bool verticalOnly = false, bool horizontalOnly = false)
    {
        int mx = _ax == int.MinValue ? 0 : a.Left - _ax, my = _ax == int.MinValue ? 0 : a.Top - _ay;
        _ax = a.Left;
        _ay = a.Top;
        var wa = WindowDrag.WorkAreaOf(b);
        if (_stuck)
        {
            // давят дальше к той же стенке (или стоят) — B держится за обратный край A;
            // уголок у мини-браузера (verticalOnly), перескочив на другую сторону, едет с ним до конца перетаскивания
            var pressing = verticalOnly || (mx == 0 && my == 0) || (_dirX != 0 && Math.Sign(mx) == _dirX) || (_dirY != 0 && Math.Sign(my) == _dirY);
            if (pressing)
            {
                var nb = WindowDrag.Clamp(Move(b, a.Left + _stickDx, a.Top + _stickDy), wa);
                if (!Overlaps(a, nb, gap)) return Same(nb, b) ? null : nb;
            }
            _stuck = false;
        }
        if (!Overlaps(a, b, gap)) return null;

        int bw = b.Width, bh = b.Height;
        int ClampX(int x) => Math.Max(wa.Left, Math.Min(x, wa.Right - bw));
        int ClampY(int y) => Math.Max(wa.Top, Math.Min(y, wa.Bottom - bh));
        var left = Move(b, a.Left - gap - bw, ClampY(b.Top));
        var right = Move(b, a.Right + gap, ClampY(b.Top));
        var up = Move(b, ClampX(b.Left), a.Top - gap - bh);
        var down = Move(b, ClampX(b.Left), a.Bottom + gap);
        bool Fits(NativeMethods.RECT r) => r.Left >= wa.Left && r.Top >= wa.Top && r.Right <= wa.Right && r.Bottom <= wa.Bottom;
        double Cost(NativeMethods.RECT r) => Math.Abs(r.Left - b.Left) + Math.Abs(r.Top - b.Top);
        NativeMethods.RECT? Best(params NativeMethods.RECT[] c)
        {
            NativeMethods.RECT? best = null;
            foreach (var r in c)
                if (Fits(r) && (best is null || Cost(r) < Cost(best.Value))) best = r;
            return best;
        }

        if (horizontalOnly) return Best(left, right) ?? Best(up, down) ?? WindowDrag.Clamp(b, wa); // вбок, где ближе
        if (verticalOnly)
        {
            // толкают вверх или вниз — туда же; упёрлось в край экрана — перескакивает на другую сторону и прилипает
            // (едет вместе с браузером, пока его тащат); толкают вбок — выше или ниже, где ближе
            if (my != 0)
            {
                NativeMethods.RECT f = my < 0 ? up : down, bk = my < 0 ? down : up;
                if (Fits(f)) return f;
                if (Fits(bk))
                {
                    _stuck = true;
                    _stickDx = bk.Left - a.Left;
                    _stickDy = bk.Top - a.Top;
                    _dirX = 0;
                    _dirY = Math.Sign(my);
                    return bk;
                }
            }
            return Best(up, down) ?? Best(left, right) ?? WindowDrag.Clamp(b, wa);
        }
        if (mx == 0 && my == 0) return Best(left, right, up, down) ?? WindowDrag.Clamp(b, wa); // появилось/выросло — куда ближе

        // толкают: вперёд по ходу (или вбок, если так короче); упёрлось в стенку — на другую сторону, и прилипает
        var horizontal = Math.Abs(mx) >= Math.Abs(my);
        NativeMethods.RECT fwd, back;
        if (horizontal)
        {
            fwd = mx > 0 ? right : left;
            back = mx > 0 ? left : right;
        }
        else
        {
            fwd = my > 0 ? down : up;
            back = my > 0 ? up : down;
        }
        if (Fits(fwd)) return Best(fwd, horizontal ? up : left, horizontal ? down : right);
        if (Fits(back))
        {
            _stuck = true;
            _stickDx = back.Left - a.Left;
            _stickDy = back.Top - a.Top;
            _dirX = horizontal ? Math.Sign(mx) : 0;
            _dirY = horizontal ? 0 : Math.Sign(my);
            return back;
        }
        return Best(up, down, left, right) ?? WindowDrag.Clamp(b, wa);
    }

    /// <summary>
    /// Окно r (его тащат) не заходит в стену wall — как в край экрана: остаётся с той стороны, с которой было (prev),
    /// и скользит вдоль неё. Сама стена не двигается.
    /// </summary>
    public static NativeMethods.RECT KeepOut(NativeMethods.RECT r, NativeMethods.RECT prev, NativeMethods.RECT wall, int gap, NativeMethods.RECT wa)
    {
        if (!Overlaps(r, wall, gap)) return r;
        int w = r.Width, h = r.Height;
        bool wasLeft = prev.Right <= wall.Left - gap + 1, wasRight = prev.Left >= wall.Right + gap - 1,
            wasAbove = prev.Bottom <= wall.Top - gap + 1, wasBelow = prev.Top >= wall.Bottom + gap - 1;
        // с какой стороны было — туда и упираемся; было по диагонали — по той оси, где зашло меньше
        int penX = wasLeft ? r.Right - (wall.Left - gap) : wasRight ? (wall.Right + gap) - r.Left : int.MaxValue;
        int penY = wasAbove ? r.Bottom - (wall.Top - gap) : wasBelow ? (wall.Bottom + gap) - r.Top : int.MaxValue;
        var res = r;
        if (penX == int.MaxValue && penY == int.MaxValue) return Separate(wall, r, gap) is { } s ? s : prev; // уже было внутри
        if (penX <= penY) res = Move(r, wasLeft ? wall.Left - gap - w : wall.Right + gap, r.Top);
        else res = Move(r, r.Left, wasAbove ? wall.Top - gap - h : wall.Bottom + gap);
        res = WindowDrag.Clamp(res, wa);
        return Overlaps(res, wall, gap) ? prev : res; // зажали между стеной и краем экрана — стоим
    }

    /// <summary>Разовое расталкивание (окно появилось или изменило размер): куда отодвинуть B, null — не мешает.</summary>
    public static NativeMethods.RECT? Separate(NativeMethods.RECT a, NativeMethods.RECT b, int gap, bool verticalOnly = false,
        bool horizontalOnly = false) => new WindowPush().Resolve(a, b, gap, verticalOnly, horizontalOnly);

    /// <summary>Прямоугольник, охватывающий оба.</summary>
    public static NativeMethods.RECT Union(NativeMethods.RECT a, NativeMethods.RECT b) => new()
    {
        Left = Math.Min(a.Left, b.Left), Top = Math.Min(a.Top, b.Top),
        Right = Math.Max(a.Right, b.Right), Bottom = Math.Max(a.Bottom, b.Bottom),
    };

    private static NativeMethods.RECT Move(NativeMethods.RECT r, int x, int y) =>
        new() { Left = x, Top = y, Right = x + r.Width, Bottom = y + r.Height };

    private static bool Same(NativeMethods.RECT a, NativeMethods.RECT b) => a.Left == b.Left && a.Top == b.Top;

    private static bool Overlaps(NativeMethods.RECT a, NativeMethods.RECT b, int gap) =>
        a.Left - gap < b.Right && a.Right + gap > b.Left && a.Top - gap < b.Bottom && a.Bottom + gap > b.Top;
}

/// <summary>
/// Панель всегда поверх мини-браузера: его окна (страница, боковая панель, поле адреса) встают сразу под панелью,
/// когда Windows или они сами поднимают их наверх (щелчок, показ, «поверх всех»). Панель не открыта — как обычно.
/// </summary>
internal static class BelowOverlay
{
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    /// <summary>Поставить окно сразу под панелью (если она на экране) — например, только что показанное.</summary>
    public static void Apply(IntPtr hwnd)
    {
        var ov = App.Instance?.Overlay;
        if (hwnd == IntPtr.Zero || ov is null || !ov.IsShown || ov.Handle == IntPtr.Zero || ov.Handle == hwnd) return;
        NativeMethods.SetWindowPos(hwnd, ov.Handle, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public static void Attach(Window w)
    {
        void Hook() => HwndSource.FromHwnd(new WindowInteropHelper(w).Handle)?.AddHook(WndProc);
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Hook();
        else w.SourceInitialized += (_, _) => Hook();
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_WINDOWPOSCHANGING) return IntPtr.Zero;
        var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((wp.flags & NativeMethods.SWP_NOZORDER) != 0) return IntPtr.Zero;
        if (wp.hwndInsertAfter != HWND_TOP && wp.hwndInsertAfter != NativeMethods.HWND_TOPMOST) return IntPtr.Zero;
        var ov = App.Instance?.Overlay;
        if (ov is null || !ov.IsShown || ov.Handle == IntPtr.Zero || ov.Handle == hwnd) return IntPtr.Zero;
        wp.hwndInsertAfter = ov.Handle; // сразу под панелью (она «поверх всех» — значит, и браузер остаётся поверх всех)
        Marshal.StructureToPtr(wp, lParam, false);
        return IntPtr.Zero;
    }
}
