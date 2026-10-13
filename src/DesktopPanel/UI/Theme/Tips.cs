using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopPanel.UI.Controls;

namespace DesktopPanel.UI.Theme;

/// <summary>
/// Всплывающие подсказки и меню по правой кнопке.
/// Режимы подсказок: "full" — все; "minimal" — только те, что что-то сообщают (стратегия Zapret, сколько в корзине,
/// настоящее имя значка…; помечены Tips.Info), подписи кнопок вроде «Закрыть» не показываются; "off" — никаких
/// (кружки «?» — справка, они работают всегда).
/// Непрозрачность фона общая для подсказок и меню (ресурсы TipBrush, MenuBrush); если фон не сплошной — под ним размытие.
/// </summary>
public static class Tips
{
    public const string Full = "full", Min = "minimal", Off = "off";

    private static string _mode = Full;
    private static int _opacity = 94;
    private static bool _hooked;

    public static bool Minimal => _mode == Min;

    /// <summary>Подсказка что-то сообщает (а не просто подписывает кнопку) — видна и в минималистичном режиме.</summary>
    public static readonly DependencyProperty InfoProperty = DependencyProperty.RegisterAttached(
        "Info", typeof(bool), typeof(Tips), new PropertyMetadata(false));

    public static void SetInfo(DependencyObject d, bool value) => d.SetValue(InfoProperty, value);
    public static bool GetInfo(DependencyObject d) => (bool)d.GetValue(InfoProperty);

    /// <summary>Текст подсказки: первая строка — заголовок (жирным), остальное — описание. Для ContentTemplate подсказок.</summary>
    public static readonly DependencyProperty CardTextProperty = DependencyProperty.RegisterAttached(
        "CardText", typeof(string), typeof(Tips), new PropertyMetadata(null, OnCardText));

    public static void SetCardText(DependencyObject d, string? value) => d.SetValue(CardTextProperty, value);
    public static string? GetCardText(DependencyObject d) => (string?)d.GetValue(CardTextProperty);

    private static void OnCardText(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var s = (e.NewValue as string ?? "").Replace("\r", "");
        var i = s.IndexOf('\n');
        tb.Inlines.Add(new Run(i < 0 ? s : s[..i]) { FontWeight = FontWeights.SemiBold });
        if (i >= 0)
        {
            tb.Inlines.Add(new LineBreak());
            tb.Inlines.Add(new Run(s[(i + 1)..]));
        }
    }

    /// <summary>Размытие под окошком всплывающего блока (Popup) — как у подсказок; фон блока берёт TipBrush.</summary>
    public static void Blur(Popup p)
    {
        // окно у всплывашки своё на каждое открытие: форму окна — по нему; подписка на размер — одна на всю жизнь
        // (раньше добавлялась при каждом открытии и копилась: N открытий — N пересчётов формы на каждое изменение размера)
        void Shape()
        {
            if (p.IsOpen && p.Child is Visual v && PresentationSource.FromVisual(v) is HwndSource src && src.Handle != IntPtr.Zero)
                Round(src.Handle, VisualTreeHelper.GetDpi(v).DpiScaleX);
        }
        p.Opened += (_, _) =>
        {
            if (p.Child is Visual v && PresentationSource.FromVisual(v) is HwndSource src && src.Handle != IntPtr.Zero)
                Glass.BlurBehind(src.Handle);
            Shape();
        };
        if (p.Child is FrameworkElement fe) fe.SizeChanged += (_, _) => Shape();
    }

    /// <summary>То же для отдельного окна-плашки (боковая панель и поле поиска мини-браузера).</summary>
    public static void Blur(Window w) => w.SourceInitialized += (_, _) =>
    {
        // окно без AllowsTransparency: скругление рисует Windows 11 (гладкое, как у большой панели), размытие — акцент
        Glass.Prepare(w);
        Glass.BlurBehind(new WindowInteropHelper(w).Handle);
    };

    /// <summary>Окно со скруглёнными углами (радиус 8 DIP): размытие у прозрачных окон иначе рисуется прямоугольником.</summary>
    private static void Round(IntPtr hwnd, double dpi)
    {
        if (!GetWindowRect(hwnd, out var r)) return;
        var d = (int)Math.Round(16 * dpi);
        SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, r.Right - r.Left + 1, r.Bottom - r.Top + 1, d, d), true);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectI r);

    private struct RectI { public int Left, Top, Right, Bottom; }

    public static void Apply(string mode, int opacityPercent)
    {
        _mode = mode is Min or Off ? mode : Full;
        _opacity = Math.Clamp(opacityPercent, 0, 100);
        if (!_hooked)
        {
            // подсказка — под элементом (не под курсором): иначе она перекрывала бы элемент, мышь «уходила» на неё,
            // подсказка закрывалась и открывалась снова — мигала
            ToolTipService.PlacementProperty.OverrideMetadata(typeof(DependencyObject),
                new FrameworkPropertyMetadata(PlacementMode.Bottom));
            ToolTipService.VerticalOffsetProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(6.0));
            // ToolTipOpening с Handled = true — подсказка не показывается
            EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent,
                new ToolTipEventHandler(OnOpening));
            // размытие под полупрозрачной подсказкой и меню — у их окошек, когда они открылись
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnPopupOpened));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnPopupOpened));
            _hooked = true;
        }
        var a = (byte)Math.Round(_opacity / 100.0 * 255);
        Set("TipBrush", Color.FromArgb(a, 0x1E, 0x1E, 0x24));
        Set("MenuBrush", Color.FromArgb(a, 0x20, 0x20, 0x26));
    }

    private static void Set(string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        Application.Current.Resources[key] = b;
    }

    private static void OnOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is HelpDot) return;
        if (_mode == Off || (_mode == Min && sender is DependencyObject d && !GetInfo(d))) e.Handled = true;
    }

    private static void OnPopupOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not Visual v) return; // размытие — всегда; ползунок меняет только затемнение поверх него
        var owner = (sender as ToolTip)?.PlacementTarget ?? (sender as ContextMenu)?.PlacementTarget;
        var who = (owner as FrameworkElement)?.Name is { Length: > 0 } n ? n : owner?.GetType().Name;
        Core.Logging.PerfLog.Note($"{(sender is ToolTip ? "подсказка" : "меню")} открыта: {who}");
        if (PresentationSource.FromVisual(v) is HwndSource src && src.Handle != IntPtr.Zero)
            Glass.BlurBehind(src.Handle);
    }
}
