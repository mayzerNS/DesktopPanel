using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopPanel.Core.Native;
using DesktopPanel.UI.Theme;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using ContainerVisual = Windows.UI.Composition.ContainerVisual;

namespace DesktopPanel.Features.Pip;

/// <summary>
/// Картинка мини-браузера — отдельное окно ровно над окном браузера (PipWindow), без своего содержимого: только слои
/// механизма композиции Windows — страница (PipWeb) и рамка. Их собирает сама Windows вместе с экраном, поэтому цвета
/// при HDR и без него — как у Edge (без снимков страницы в 8 бит), а прозрачность — по-настоящему, по каждой точке.
/// Окно никогда не меняет свой стиль: всегда «прозрачно» для мыши (WS_EX_LAYERED + WS_EX_TRANSPARENT), без фокуса —
/// картинка одна и та же в любом режиме. Мышь и клавиатуру получает окно браузера под ним (почти полностью прозрачное);
/// сквозной клик включается у того окна, картинку он не трогает.
/// Важно: SetLayeredWindowAttributes НЕ вызывать — с ним Windows рисует окно «по-старому», одним слоем: всё, где нет
/// страницы, становится чёрным, а страница — 8-битной картинкой (так было в бете). Без него многослойное окно без
/// перенаправления (WS_EX_NOREDIRECTIONBITMAP) показывает слои композиции как есть (так же делает PowerToys).
/// Положение, размер, показ и место по глубине — следом за окном браузера (его WM_WINDOWPOSCHANGED).
/// </summary>
internal sealed class PipSurface : IDisposable
{
    /// <summary>Скругление углов страницы и рамки в пикселях экрана — как у окон Windows 11 (8 DIP, растёт с масштабом).</summary>
    public static float RadiusPx(double dpi) => (float)(DesktopPanel.UI.Theme.Corners.WindowDip * dpi);

    private readonly Window _window;
    private IntPtr _owner;
    private DesktopWindowTarget? _target;
    private CompositionRoundedRectangleGeometry? _frameGeo;
    // сплошная и пунктирная — у каждой своя кисть, у ненужной сейчас она прозрачная
    // ⚠ у готовой фигуры не менять StrokeBrush и StrokeDashArray — Windows подхватывала замену только в первый раз; только цвет кистей
    private CompositionSpriteShape? _frameShape, _dashShape;
    private CompositionColorBrush? _frameBrush, _dashBrush;
    private Color _frameColor = Color.FromArgb(0x30, 255, 255, 255);
    private double _frameWidth = 1, _opacity = 1;
    private bool _frameDashed;
    private int _w, _h;

    public PipSurface(Window window) => _window = window;

    public IntPtr Handle { get; private set; }

    /// <summary>Корень слоёв окна: страница — внизу (PipWeb), рамка — над ней.</summary>
    public ContainerVisual? Root { get; private set; }

    /// <summary>Непрозрачность страницы и рамки (0…1).</summary>
    public double Opacity
    {
        get => _opacity;
        set
        {
            _opacity = value;
            if (Root is not null) Root.Opacity = (float)value;
        }
    }

    /// <summary>Создать окно (когда у окна браузера уже есть HWND); дальше оно само ходит за ним.</summary>
    public void Ensure()
    {
        if (Handle != IntPtr.Zero) return;
        _owner = new WindowInteropHelper(_window).EnsureHandle();
        NativeMethods.GetWindowRect(_owner, out var r);
        // владелец — окно браузера: всегда над ним, прячется и закрывается вместе с ним
        Handle = CreateWindowEx(ExStyle, ClassName(), "", WS_POPUP, r.Left, r.Top, Math.Max(1, r.Width), Math.Max(1, r.Height),
            _owner, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        // углы скругляем сами (обрезка слоя), появление и исчезание — сразу, вместе с окном браузера
        var noRound = DWMWCP_DONOTROUND;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));
        var noFade = 1;
        NativeMethods.DwmSetWindowAttribute(Handle, DWMWA_TRANSITIONS_FORCEDISABLED, ref noFade, sizeof(int));

        var c = Comp.Compositor;
        _target = Comp.CreateTarget(Handle, false);
        Root = c.CreateContainerVisual();
        Root.RelativeSizeAdjustment = Vector2.One;
        Root.Opacity = (float)_opacity;
        _target.Root = Root;
        // рамка по краю окна — над страницей, страницу не сжимает
        var frame = c.CreateShapeVisual();
        frame.RelativeSizeAdjustment = Vector2.One;
        _frameGeo = c.CreateRoundedRectangleGeometry();
        _frameShape = c.CreateSpriteShape(_frameGeo);
        _frameBrush = c.CreateColorBrush();
        _frameShape.StrokeBrush = _frameBrush;
        frame.Shapes.Add(_frameShape);
        // пунктир задаётся один раз: смена StrokeDashArray у готовой фигуры Windows подхватывала только в первый раз
        _dashShape = c.CreateSpriteShape(_frameGeo);
        _dashBrush = c.CreateColorBrush();
        _dashShape.StrokeBrush = _dashBrush;
        _dashShape.StrokeDashArray.Add(4); // штрих и промежуток — в толщинах линии
        _dashShape.StrokeDashArray.Add(3);
        frame.Shapes.Add(_dashShape);
        Root.Children.InsertAtTop(frame);

        _w = r.Width;
        _h = r.Height;
        LayoutFrame();
        HwndSource.FromHwnd(_owner)?.AddHook(OwnerProc);
        Follow();
    }

    /// <summary>Рамка по краю окна: цвет (с прозрачностью), толщина в DIP, пунктир.</summary>
    public void SetFrame(Color color, double width, bool dashed = false)
    {
        _frameColor = color;
        _frameWidth = width;
        _frameDashed = dashed;
        LayoutFrame();
    }

    /// <summary>Пересчитать рамку (сменился масштаб монитора).</summary>
    public void Relayout() => LayoutFrame();

    private void LayoutFrame()
    {
        if (_frameGeo is null || _frameShape is null || _dashShape is null || _frameBrush is null || _dashBrush is null) return;
        var dpi = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        var w = (float)Math.Max(1, Math.Round(_frameWidth * dpi));
        _frameGeo.Offset = new Vector2(w / 2, w / 2);
        _frameGeo.Size = Vector2.Max(Vector2.Zero, new Vector2(_w, _h) - new Vector2(w, w));
        var radius = Math.Max(0, RadiusPx(dpi) - w / 2);
        _frameGeo.CornerRadius = new Vector2(radius, radius);
        _frameShape.StrokeThickness = w;
        _dashShape.StrokeThickness = w;
        var color = Windows.UI.Color.FromArgb(_frameColor.A, _frameColor.R, _frameColor.G, _frameColor.B);
        var none = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        _frameBrush.Color = _frameDashed ? none : color;
        _dashBrush.Color = _frameDashed ? color : none;
    }

    // ---------- за окном браузера ----------
    private IntPtr OwnerProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGED) Follow();
        return IntPtr.Zero;
    }

    /// <summary>Встать ровно на окно браузера, сразу над ним по глубине; показаться и спрятаться вместе с ним.</summary>
    private void Follow()
    {
        if (Handle == IntPtr.Zero) return;
        if (!NativeMethods.IsWindowVisible(_owner))
        {
            if (NativeMethods.IsWindowVisible(Handle)) NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
            return;
        }
        if (!NativeMethods.GetWindowRect(_owner, out var r)) return;
        // сразу над окном браузера: после окна, которое над ним (панель-оверлей остаётся выше); само окно браузера не трогаем
        // большая панель на экране — сразу под ней (как само окно браузера и его боковая панель: один слой, уголок выше всех)
        var flags = NativeMethods.SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        var after = OverlayHandle();
        if (after == IntPtr.Zero)
        {
            var above = NativeMethods.GetWindow(_owner, GW_HWNDPREV);
            after = above == IntPtr.Zero ? NativeMethods.HWND_TOPMOST : above;
            if (above == Handle) flags |= NativeMethods.SWP_NOZORDER;
        }
        else if (NativeMethods.GetWindow(Handle, GW_HWNDPREV) == after) flags |= NativeMethods.SWP_NOZORDER;
        if (!NativeMethods.IsWindowVisible(Handle)) flags |= NativeMethods.SWP_SHOWWINDOW;
        NativeMethods.SetWindowPos(Handle, after, r.Left, r.Top, r.Width, r.Height, flags);
        if (r.Width != _w || r.Height != _h)
        {
            _w = r.Width;
            _h = r.Height;
            LayoutFrame();
        }
    }

    /// <summary>Большая панель (и уголок) на экране — её окно, иначе ноль.</summary>
    private static IntPtr OverlayHandle()
    {
        var ov = App.Instance?.Overlay;
        return ov is { IsShown: true } ? ov.Handle : IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_owner != IntPtr.Zero) HwndSource.FromHwnd(_owner)?.RemoveHook(OwnerProc);
        try { _target?.Dispose(); } catch { }
        _target = null;
        Root = null;
        if (Handle != IntPtr.Zero) DestroyWindow(Handle); // обычно уже закрыто вместе с владельцем
        Handle = IntPtr.Zero;
    }

    // ---------- окно ----------
    // без перенаправления (содержимое — только композиция), многослойное и «прозрачное» для мыши, без фокуса, поверх всех
    // ⚠ SetLayeredWindowAttributes этому окну не звать (вокруг слоёв чёрное, страница 8-битной картинкой); стиль не менять
    private const int ExStyle = 0x00200000 /*WS_EX_NOREDIRECTIONBITMAP*/ | 0x00080000 /*WS_EX_LAYERED*/
        | 0x20 /*WS_EX_TRANSPARENT*/ | 0x80 /*WS_EX_TOOLWINDOW*/ | 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x8 /*WS_EX_TOPMOST*/;
    private const uint WS_POPUP = 0x80000000;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint GW_HWNDPREV = 3;
    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_WINDOWPOSCHANGED = 0x0047, WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021, WM_ERASEBKGND = 0x0014;
    private const int HTTRANSPARENT = -1, MA_NOACTIVATE = 3;
    private const int DWMWCP_DONOTROUND = 1, DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    private delegate IntPtr WndProcFn(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Windows зовёт обработчик по указателю — делегат живёт, пока жива программа
    private static readonly WndProcFn Proc = SurfaceProc;
    private static string? _class;

    private static string ClassName()
    {
        if (_class is not null) return _class;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "DesktopPanel.PipSurface",
        };
        if (RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return _class = wc.lpszClassName;
    }

    private static IntPtr SurfaceProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Windows (или владелец) поднимает окно наверх — встаёт сразу под большой панелью, а не поверх уголка
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var flags = (uint)Marshal.ReadInt32(lParam, IntPtr.Size * 2 + 16);
            var after = Marshal.ReadIntPtr(lParam, IntPtr.Size);
            var ov = OverlayHandle();
            if ((flags & NativeMethods.SWP_NOZORDER) == 0 && ov != IntPtr.Zero && ov != hwnd
                && (after == IntPtr.Zero || after == NativeMethods.HWND_TOPMOST))
                Marshal.WriteIntPtr(lParam, IntPtr.Size, ov);
        }
        return Proc0(hwnd, msg, wParam, lParam);
    }

    private static IntPtr Proc0(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) => (int)msg switch
    {
        WM_NCHITTEST => new IntPtr(HTTRANSPARENT),
        WM_MOUSEACTIVATE => new IntPtr(MA_NOACTIVATE),
        WM_ERASEBKGND => new IntPtr(1),
        _ => DefWindowProc(hwnd, msg, wParam, lParam),
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
