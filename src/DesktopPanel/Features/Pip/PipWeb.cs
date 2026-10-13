using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using DesktopPanel.Core.Native;
using DesktopPanel.UI.Theme;
using Windows.UI.Composition;
using WinRT;
using ContainerVisual = Windows.UI.Composition.ContainerVisual;

namespace DesktopPanel.Features.Pip;

/// <summary>
/// Страница мини-браузера — слой механизма композиции Windows (WebView2 «visual hosting») в окне-картинке PipSurface:
/// • страницу рисует сама Windows вместе с экраном — HDR и яркость SDR как у Edge (без снимков в 8 бит, как было
///   у WebView2CompositionControl: при HDR видео пересвечивалось);
/// • это не окно в окне, поэтому у слоя есть прозрачность и скругление (у окна-страницы WebView2 их не было);
/// • родитель браузера — окно PipWindow (под картинкой, того же места и размера): ему фокус и клавиатура, от него
///   меню и выпадающие списки страницы; мышь странице передаём сами (SendMouseInput) — только в режиме взаимодействия
///   (Interactive); иначе щелчки остаются окну (перетащить, тройной щелчок).
/// </summary>
internal sealed class PipWeb : IDisposable
{
    private readonly Window _host;
    private readonly FrameworkElement _area;
    private readonly PipSurface _surface;
    private IntPtr _hwnd;
    private CoreWebView2CompositionController? _cc;
    private ContainerVisual? _visual;
    private CompositionRoundedRectangleGeometry? _clip;
    private double _zoom = 1;
    private bool _visible = true;

    // окна, у которых курсор и горизонтальная прокрутка уже перехвачены (пересозданная страница — тот же перехват)
    private static readonly HashSet<IntPtr> Hooked = new();

    /// <param name="host">окно браузера: родитель WebView2, мышь и клавиатура</param>
    /// <param name="area">место страницы в нём</param>
    /// <param name="surface">окно-картинка над ним: туда кладётся слой страницы</param>
    public PipWeb(Window host, FrameworkElement area, PipSurface surface)
    {
        _host = host;
        _area = area;
        _surface = surface;
    }

    public CoreWebView2? CoreWebView2 => _cc?.CoreWebView2;

    /// <summary>
    /// Мышь — странице (режим взаимодействия). Выключили — странице «мышь ушла»: иначе она считала, что курсор всё ещё
    /// над ней (подсветка и подсказка к ссылке оставались на экране).
    /// </summary>
    public bool Interactive
    {
        get => _interactive;
        set
        {
            _interactive = value;
            if (value) return;
            if (_captured)
            {
                _captured = false;
                _host.ReleaseMouseCapture();
            }
            if (_over) Leave();
        }
    }

    private bool _interactive;

    public System.Drawing.Color DefaultBackgroundColor { get; set; } = System.Drawing.Color.FromArgb(0x10, 0x10, 0x14);

    public double ZoomFactor
    {
        get => _zoom;
        set
        {
            _zoom = value;
            if (_cc is not null) _cc.ZoomFactor = value;
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            ApplyVisible();
        }
    }

    /// <summary>
    /// Окно браузера на экране. Скрыли — движок тоже «скрыт»: прячет свои всплывающие подсказки и невидимое окно
    /// (иначе подсказка к ссылке оставалась висеть на экране); страница — как свёрнутая вкладка (звук играет дальше).
    /// </summary>
    public bool HostVisible
    {
        get => _hostVisible;
        set
        {
            if (!value && _over) Leave();
            _hostVisible = value;
            ApplyVisible();
        }
    }

    private bool _hostVisible = true;

    private void ApplyVisible()
    {
        if (_cc is not null) _cc.IsVisible = _visible && _hostVisible;
        if (_visual is not null) _visual.IsVisible = _visible;
    }

    public async Task EnsureCoreWebView2Async(CoreWebView2Environment env)
    {
        _hwnd = new WindowInteropHelper(_host).EnsureHandle();
        if (Hooked.Add(_hwnd))
        {
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            var hwnd = _hwnd;
            _host.Closed += (_, _) => Hooked.Remove(hwnd);
        }
        _surface.Ensure();
        var c = Comp.Compositor;
        _visual = c.CreateContainerVisual();
        _clip = c.CreateRoundedRectangleGeometry();
        _visual.Clip = c.CreateGeometricClip(_clip);
        _visual.IsVisible = _visible;
        _surface.Root!.Children.InsertAtBottom(_visual); // рамка — выше страницы

        _cc = await env.CreateCoreWebView2CompositionControllerAsync(_hwnd);
        // WebView2 нужен сам объект слоя Windows, а не обёртка .NET вокруг него
        var raw = ((IWinRTObject)_visual).NativeObject.ThisPtr;
        _cc.RootVisualTarget = Marshal.GetObjectForIUnknown(raw);
        _cc.DefaultBackgroundColor = DefaultBackgroundColor;
        _cc.ShouldDetectMonitorScaleChanges = false;
        _cc.ZoomFactor = _zoom;
        _cc.IsVisible = _visible && _hostVisible;
        _cc.AllowExternalDrop = false; // файлы в браузер не бросают
        TameEngineWindows();
        _cc.CursorChanged += (_, _) => ApplyCursor();
        Live[_hwnd] = this; // курсор и горизонтальная прокрутка — этой странице
        Layout();
        // ⚠ не LayoutUpdated: он приходит после ЛЮБОЙ раскладки любого окна программы (анимация поля поиска в панели —
        // каждый кадр). Место страницы в окне меняется только вместе с её размером (окно растянули, полоса ошибки выше)
        _area.SizeChanged += OnLayout;
        _host.DpiChanged += OnDpi;
        _host.PreviewMouseMove += OnMove;
        _host.PreviewMouseDown += OnDown;
        _host.PreviewMouseUp += OnUp;
        _host.PreviewMouseWheel += OnWheel;
        _host.MouseLeave += OnLeave;
    }

    private bool _moving;

    /// <summary>Окно браузера тащат: движку о новом месте — одним вызовом, когда отпустят.</summary>
    public bool Moving
    {
        set
        {
            if (_moving == value) return;
            _moving = value;
            if (value) return;
            try { _cc?.NotifyParentWindowPositionChanged(); } catch { }
        }
    }

    public void Focus()
    {
        try { _cc?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch { }
    }

    // ---------- место и размер ----------
    private Rect _last = Rect.Empty;

    private void OnLayout(object? sender, EventArgs e) => Layout();

    private void OnDpi(object sender, DpiChangedEventArgs e)
    {
        _last = Rect.Empty;
        Layout();
    }

    /// <summary>
    /// Слой — ровно на месте элемента area (в пикселях экрана), браузер — того же размера. Окно-картинка стоит ровно
    /// на окне браузера, поэтому место в окне браузера — это и место в нём.
    /// </summary>
    private void Layout()
    {
        if (_cc is null || _visual is null || _clip is null || PresentationSource.FromVisual(_area) is null) return;
        var dpi = VisualTreeHelper.GetDpi(_host).DpiScaleX;
        Rect r;
        try { r = _area.TransformToAncestor(_host).TransformBounds(new Rect(_area.RenderSize)); }
        catch (InvalidOperationException) { return; }
        var px = new Rect(Math.Round(r.X * dpi), Math.Round(r.Y * dpi), Math.Round(r.Width * dpi), Math.Round(r.Height * dpi));
        if (px == _last) return;
        _last = px;
        _visual.Offset = new Vector3((float)px.X, (float)px.Y, 0);
        _visual.Size = new Vector2((float)px.Width, (float)px.Height);
        _clip.Size = _visual.Size;
        _clip.CornerRadius = new Vector2(PipSurface.RadiusPx(dpi), PipSurface.RadiusPx(dpi));
        _cc.RasterizationScale = dpi;
        _cc.Bounds = new System.Drawing.Rectangle(0, 0, (int)px.Width, (int)px.Height);
    }

    // ---------- мышь — странице ----------
    private bool _captured;

    /// <summary>Точка относительно страницы в пикселях; null — мышь не над страницей (и не тянут — без захвата).</summary>
    private System.Drawing.Point? PagePoint(MouseEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(_host).DpiScaleX;
        var p = e.GetPosition(_area);
        var inside = p.X >= 0 && p.Y >= 0 && p.X < _area.ActualWidth && p.Y < _area.ActualHeight;
        if (!inside && !_captured) return null;
        return new System.Drawing.Point((int)Math.Round(p.X * dpi), (int)Math.Round(p.Y * dpi));
    }

    private static CoreWebView2MouseEventVirtualKeys Keys(MouseEventArgs e)
    {
        var k = CoreWebView2MouseEventVirtualKeys.None;
        if (e.LeftButton == MouseButtonState.Pressed) k |= CoreWebView2MouseEventVirtualKeys.LeftButton;
        if (e.RightButton == MouseButtonState.Pressed) k |= CoreWebView2MouseEventVirtualKeys.RightButton;
        if (e.MiddleButton == MouseButtonState.Pressed) k |= CoreWebView2MouseEventVirtualKeys.MiddleButton;
        if (e.XButton1 == MouseButtonState.Pressed) k |= CoreWebView2MouseEventVirtualKeys.XButton1;
        if (e.XButton2 == MouseButtonState.Pressed) k |= CoreWebView2MouseEventVirtualKeys.XButton2;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) k |= CoreWebView2MouseEventVirtualKeys.Shift;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) k |= CoreWebView2MouseEventVirtualKeys.Control;
        return k;
    }

    private void Send(CoreWebView2MouseEventKind kind, MouseEventArgs e, uint data, System.Drawing.Point p)
    {
        try { _cc?.SendMouseInput(kind, Keys(e), data, p); } catch { }
    }

    private bool _over;

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!Interactive || _cc is null) return;
        if (PagePoint(e) is not { } p)
        {
            if (_over) Leave();
            return;
        }
        _over = true;
        Send(CoreWebView2MouseEventKind.Move, e, 0, p);
        e.Handled = true;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (!Interactive || _cc is null || PagePoint(e) is not { } p) return;
        var dbl = e.ClickCount == 2;
        var (kind, data) = e.ChangedButton switch
        {
            MouseButton.Left => (dbl ? CoreWebView2MouseEventKind.LeftButtonDoubleClick : CoreWebView2MouseEventKind.LeftButtonDown, 0u),
            MouseButton.Right => (dbl ? CoreWebView2MouseEventKind.RightButtonDoubleClick : CoreWebView2MouseEventKind.RightButtonDown, 0u),
            MouseButton.Middle => (dbl ? CoreWebView2MouseEventKind.MiddleButtonDoubleClick : CoreWebView2MouseEventKind.MiddleButtonDown, 0u),
            MouseButton.XButton1 => (dbl ? CoreWebView2MouseEventKind.XButtonDoubleClick : CoreWebView2MouseEventKind.XButtonDown, 1u),
            _ => (dbl ? CoreWebView2MouseEventKind.XButtonDoubleClick : CoreWebView2MouseEventKind.XButtonDown, 2u),
        };
        Focus();
        // тянут (выделение, ползунок плеера) — мышь остаётся у окна и за его краем
        if (!_captured) _captured = _host.CaptureMouse();
        Send(kind, e, data, p);
        e.Handled = true;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!Interactive || _cc is null || PagePoint(e) is not { } p) return;
        var (kind, data) = e.ChangedButton switch
        {
            MouseButton.Left => (CoreWebView2MouseEventKind.LeftButtonUp, 0u),
            MouseButton.Right => (CoreWebView2MouseEventKind.RightButtonUp, 0u),
            MouseButton.Middle => (CoreWebView2MouseEventKind.MiddleButtonUp, 0u),
            MouseButton.XButton1 => (CoreWebView2MouseEventKind.XButtonUp, 1u),
            _ => (CoreWebView2MouseEventKind.XButtonUp, 2u),
        };
        Send(kind, e, data, p);
        if (_captured && e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released
            && e.MiddleButton == MouseButtonState.Released)
        {
            _captured = false;
            _host.ReleaseMouseCapture();
        }
        e.Handled = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Interactive || _cc is null || PagePoint(e) is not { } p) return;
        Send(CoreWebView2MouseEventKind.Wheel, e, unchecked((uint)e.Delta), p);
        e.Handled = true;
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        if (_over && !_captured) Leave();
    }

    private void Leave()
    {
        _over = false;
        try { _cc?.SendMouseInput(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, default); } catch { }
    }

    // ---------- невидимое окно движка ----------
    /// <summary>
    /// У WebView2 в этом режиме есть своё окно — невидимое (доступность, перетаскивание файлов), отдельное от наших
    /// и не «поверх всех». Мышь оно ловит: там, где браузер появился, оставалась невидимая «дыра» — щелчки и перетащенные
    /// файлы уходили в браузер (даже скрытый, даже если его увезли), а при сквозном клике оно перехватывало щелчки над
    /// браузером. Делаем его прозрачным для мыши (как наше окно-картинку). Всплывающие окна движка (выпадающие списки,
    /// меню — «поверх всех», и их в эти моменты нет: зовём при создании, после перехода, при показе) не трогаем.
    /// </summary>
    public void TameEngineWindows()
    {
        uint pid;
        try { pid = _cc?.CoreWebView2?.BrowserProcessId ?? 0; }
        catch { return; }
        if (pid == 0) return;
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out var p);
            if (p != pid) return true;
            var ex = NativeMethods.ExStyle(h);
            const long need = WS_EX_NOREDIRECTIONBITMAP | NativeMethods.WS_EX_LAYERED;
            if ((ex & need) != need || (ex & (WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT)) != 0) return true;
            if (!NativeMethods.ClassName(h).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return true;
            NativeMethods.SetExStyle(h, ex | NativeMethods.WS_EX_TRANSPARENT);
            return true;
        }, IntPtr.Zero);
    }

    private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_TOPMOST = 0x8;

    // ---------- курсор страницы, горизонтальная прокрутка, место окна ----------
    private const int WM_SETCURSOR = 0x0020, WM_MOUSEHWHEEL = 0x020E, WM_WINDOWPOSCHANGED = 0x0047;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var me = Current(hwnd);
        if (me is null) return IntPtr.Zero;
        if (msg == WM_WINDOWPOSCHANGED)
        {
            // окно браузера сдвинули или растянули — движку: его невидимое окно, подсказки и выпадающие списки — по новому
            // месту (без этого они оставались там, где браузер появился)
            var flags = (uint)Marshal.ReadInt32(lParam, IntPtr.Size * 2 + 16); // WINDOWPOS.flags
            // пока окно тащат — не на каждый кадр (межпроцессный вызов в движок), а один раз в конце (Moving = false)
            if ((flags & 3) != 3 && !me._moving && me._cc is { } c0)
            {
                try { c0.NotifyParentWindowPositionChanged(); } catch { }
            }
            return IntPtr.Zero;
        }
        if (msg == WM_SETCURSOR && me.Interactive && me._over && me._cc is { } cc && (lParam.ToInt64() & 0xFFFF) == 1) // HTCLIENT
        {
            SetCursor(cc.Cursor);
            handled = true;
            return new IntPtr(1);
        }
        if (msg == WM_MOUSEHWHEEL && me.Interactive && me._over && me._cc is { } c2)
        {
            NativeMethodsPoint(lParam, out var sx, out var sy);
            var pt = me._area.PointFromScreen(new Point(sx, sy));
            var dpi = VisualTreeHelper.GetDpi(me._host).DpiScaleX;
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            try
            {
                c2.SendMouseInput(CoreWebView2MouseEventKind.HorizontalWheel, CoreWebView2MouseEventVirtualKeys.None, unchecked((uint)delta),
                    new System.Drawing.Point((int)Math.Round(pt.X * dpi), (int)Math.Round(pt.Y * dpi)));
            }
            catch { }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ApplyCursor()
    {
        if (Interactive && _over && _cc is not null) SetCursor(_cc.Cursor);
    }

    // текущая страница окна (после пересоздания — новая)
    private static readonly Dictionary<IntPtr, PipWeb> Live = new();

    private static PipWeb? Current(IntPtr hwnd) => Live.TryGetValue(hwnd, out var w) ? w : null;

    /// <summary>Эта страница — текущая у окна (курсор и горизонтальная прокрутка — ей).</summary>
    public void MakeCurrent() => Live[_hwnd] = this;

    private static void NativeMethodsPoint(IntPtr lParam, out int x, out int y)
    {
        var v = lParam.ToInt64();
        x = (short)(v & 0xFFFF);
        y = (short)((v >> 16) & 0xFFFF);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    public void Dispose()
    {
        _area.SizeChanged -= OnLayout;
        _host.DpiChanged -= OnDpi;
        _host.PreviewMouseMove -= OnMove;
        _host.PreviewMouseDown -= OnDown;
        _host.PreviewMouseUp -= OnUp;
        _host.PreviewMouseWheel -= OnWheel;
        _host.MouseLeave -= OnLeave;
        if (_captured) _host.ReleaseMouseCapture();
        if (Live.TryGetValue(_hwnd, out var cur) && cur == this) Live.Remove(_hwnd);
        try { _cc?.Close(); } catch { }
        _cc = null;
        if (_visual is not null) _surface.Root?.Children.Remove(_visual);
        _visual?.Dispose();
        _visual = null;
    }
}
