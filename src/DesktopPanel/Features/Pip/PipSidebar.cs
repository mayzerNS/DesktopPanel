using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Native;
using DesktopPanel.UI.Controls;

namespace DesktopPanel.Features.Pip;

/// <summary>
/// Боковая панель мини-браузера: отдельное узкое окно рядом с ним — со стороны, где до края экрана дальше,
/// поэтому страницу не закрывает. Появляется, пока мышь над браузером или над ней (в любом режиме).
/// Кнопки — два столбца, как в браузере: ручка и взаимодействие, назад и вперёд, дальше поиск, домой и т.д.
/// Если браузер низкий — панель целиком уменьшается (а не перестраивается в три столбца).
/// Окно не забирает фокус: кнопки нажимаются, а игра под браузером остаётся активной.
/// </summary>
public sealed class PipSidebar : Window
{
    public IconButton InteractButton { get; }
    public IconButton BackButton { get; }
    public IconButton ForwardButton { get; }
    public IconButton StarButton { get; }
    public IconButton ZoomButton { get; }
    public IconButton FavoritesButton { get; }

    private const int MinOpacity = 0, MaxOpacity = 100, OpacityStep = 5;
    private int _opacity = 100;
    private readonly TextBlock _opacityValue;
    private readonly Button _less, _more;

    /// <summary>Прозрачность поменяли стрелками (20..100 %).</summary>
    public event Action<int>? OpacityChanged;

    /// <summary>Удерживают стрелку прозрачности — панель не прячется.</summary>
    public bool PopupOpen => _held;
    public event Action? InteractClicked, SearchClicked, BackClicked, ForwardClicked, ReloadClicked,
        HomeClicked, FavoritesClicked, StarClicked, ZoomClicked, CloseClicked;

    private const double Btn = 32, Gap = 4, BarH = 40, Pad = 4;
    private const double NaturalW = 2 * Btn + Gap + 2 * Pad + 2, NaturalH = 5 * Btn + 5 * Gap + BarH + 2 * Pad + 2;
    private double _maxH = double.PositiveInfinity;

    private IntPtr _hwnd;
    private readonly Viewbox _scale;
    private readonly Border _plate;

    /// <summary>Панель бледнеет вместе с браузером (как браузер: 0 % ползунка — половина); размытие под ней остаётся.</summary>
    public void SetPanelOpacity(int browserPercent)
    {
        _panelOpacity = 0.5 + 0.5 * Math.Clamp(browserPercent, 0, 100) / 100.0;
        DesktopPanel.UI.Theme.Anim.Stop(_plate, OpacityProperty);
        _plate.Opacity = _panelOpacity;
    }

    private double _panelOpacity = 1;

    /// <summary>
    /// Появиться: значки выезжают сбоку — со стороны браузера (besideRight — панель справа от него, значит слева направо)
    /// и проявляются. Фон панели (и размытие под ним) стоит на месте — едут только значки.
    /// </summary>
    public void Appear(bool besideRight)
    {
        _plate.Opacity = _panelOpacity;
        if (_plate.Child is not FrameworkElement icons) return;
        if (icons.RenderTransform is not TranslateTransform { IsFrozen: false } tt) icons.RenderTransform = tt = new TranslateTransform();
        if (!DesktopPanel.UI.Theme.Motion.Enabled)
        {
            tt.X = 0;
            icons.Opacity = 1;
            return;
        }
        DesktopPanel.UI.Theme.Anim.To(icons, OpacityProperty, 1, 140, DesktopPanel.UI.Theme.Motion.EaseOut(), from: 0);
        DesktopPanel.UI.Theme.Anim.To(tt, TranslateTransform.XProperty, 0, DesktopPanel.UI.Theme.Motion.UiMs * 0.7,
            DesktopPanel.UI.Theme.Motion.UiEase(), from: besideRight ? -8 : 8);
    }

    public PipSidebar()
    {
        WindowStyle = WindowStyle.None;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.Manual; // размер задаём сами (Relayout): иначе окно выходило шире плашки
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = Brushes.White;
        UseLayoutRounding = true;

        // сетка 2 столбца: кнопки 32, везде одинаковый зазор Gap (и по горизонтали, и по вертикали, и до плашки яркости)
        var grid = new Grid { Width = 2 * Btn + Gap, Height = 5 * Btn + 5 * Gap + BarH };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Btn) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Btn) });
        for (var i = 0; i < 5; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Btn) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Gap) });
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BarH) });
        var slot = 0;
        IconButton Add(string glyph, string tipKey, Action click)
        {
            var b = new IconButton { Glyph = glyph, Text = Loc.T(tipKey), Compact = true, FixedSize = Btn, Margin = new Thickness(0) };
            b.Click += (_, _) => click();
            Grid.SetColumn(b, slot % 2 * 2);
            Grid.SetRow(b, slot / 2 * 2);
            slot++;
            grid.Children.Add(b);
            return b;
        }



        Add("\uE711", "pip.close", () => CloseClicked?.Invoke());
        InteractButton = Add("", "pip.mode.normal", () => InteractClicked?.Invoke());
        BackButton = Add("", "pip.back", () => BackClicked?.Invoke());
        ForwardButton = Add("", "pip.forward", () => ForwardClicked?.Invoke());
        Add("", "pip.search", () => SearchClicked?.Invoke());
        Add("", "pip.home", () => HomeClicked?.Invoke());
        Add("", "pip.reload", () => ReloadClicked?.Invoke());
        ZoomButton = Add("", "pip.zoom_fit", () => ZoomClicked?.Invoke());
        FavoritesButton = Add("", "pip.favorites", () => FavoritesClicked?.Invoke());
        StarButton = Add("", "pip.add_favorite", () => StarClicked?.Invoke());

        // плашка прозрачности на всю ширину: ‹  ☀ 55%  ›
        _less = ArrowButton("\uE76B", -OpacityStep);
        _more = ArrowButton("\uE76C", OpacityStep);
        _opacityValue = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White };
        var sun = new TextBlock { Text = "\uE793", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White };
        sun.SetResourceReference(TextBlock.FontFamilyProperty, "GlyphFont");
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        center.Children.Add(sun);
        center.Children.Add(_opacityValue);
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        bar.Children.Add(_less);
        Grid.SetColumn(center, 1);
        bar.Children.Add(center);
        Grid.SetColumn(_more, 2);
        bar.Children.Add(_more);
        var barPlate = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = bar,
        };
        Grid.SetRow(barPlate, 10);
        Grid.SetColumnSpan(barPlate, 3);
        grid.Children.Add(barPlate);
        SetOpacity(100);
        var plate = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(Pad),
            Width = NaturalW, Height = NaturalH,
            Child = grid,
        };
        _plate = plate;
        plate.SetResourceReference(Border.BackgroundProperty, "TipBrush");
        plate.SetResourceReference(Border.BorderBrushProperty, "PopupEdgeBrush"); // как у подсказок и поля поиска

        DesktopPanel.UI.Theme.Tips.Blur(this);
        _scale = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = plate };
        Content = _scale;
        Relayout();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // не забирать фокус: щелчок по кнопке не выводит игру из активного состояния
            NativeMethods.SetExStyle(_hwnd, NativeMethods.ExStyle(_hwnd) | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
        };
    }

    /// <summary>
    /// Стрелка плашки прозрачности: нажатие — шаг, удержание — ползёт (через 0,4 с, шаг каждые 70 мс).
    /// Своя кнопка вместо RepeatButton: у той таймер повтора с низким приоритетом, и пока мини-браузер рисует страницу,
    /// его шаги ждали очереди по 1–2 с — удержание шло рывками. Здесь таймер с обычным (высоким) приоритетом.
    /// </summary>
    private Button ArrowButton(string glyph, int delta)
    {
        var text = new TextBlock { Text = glyph, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "GlyphFont");
        var b = new Button { Content = text };
        b.SetResourceReference(StyleProperty, "GhostButton"); // подсветка проявляется, нажатие пружинит — как у всех кнопок
        void Step()
        {
            var v = Math.Clamp(_opacity + delta, MinOpacity, MaxOpacity);
            if (v == _opacity) return;
            SetOpacity(v);
            OpacityChanged?.Invoke(v);
        }
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal);
        timer.Tick += (_, _) =>
        {
            timer.Interval = TimeSpan.FromMilliseconds(70);
            Step();
        };
        void Stop()
        {
            timer.Stop();
            _held = false;
            if (b.IsMouseCaptured) b.ReleaseMouseCapture();
        }
        b.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _held = true;
            b.CaptureMouse();
            Step();
            timer.Interval = TimeSpan.FromMilliseconds(400);
            timer.Start();
        };
        b.PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Stop();
        };
        b.LostMouseCapture += (_, _) => Stop();
        return b;
    }

    private bool _held; // стрелку прозрачности держат

    /// <summary>Показать значение прозрачности (без события).</summary>
    public void SetOpacity(int percent)
    {
        _opacity = Math.Clamp(percent, MinOpacity, MaxOpacity);
        _opacityValue.Text = $"{_opacity}%";
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Размер в пикселях экрана (после показа).</summary>
    public (int W, int H) PixelSize
    {
        get
        {
            if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var r)) return (r.Width, r.Height);
            var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return ((int)(ActualWidth * s), (int)(ActualHeight * s));
        }
    }

    /// <summary>Панель не выше браузера (высота в пикселях экрана): если не помещается — уменьшается целиком.</summary>
    public void FitHeight(int pixels)
    {
        var h = Math.Max(60, pixels / VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (Math.Abs(_maxH - h) < 0.5) return;
        _maxH = h;
        Relayout();
    }

    /// <summary>Размер окна = размер плашки (с учётом уменьшения), без запаса по краям.</summary>
    private void Relayout()
    {
        var s = Math.Min(1, _maxH / NaturalH);
        Width = Math.Round(NaturalW * s);
        Height = Math.Round(NaturalH * s);
        UpdateLayout();
    }

    /// <summary>
    /// Принудительно перерисовать окно целиком. При первом показе окно с размытием иногда рисовалось пустым:
    /// на экране оставалось только то, что потом обновилось (например, кнопка режима взаимодействия).
    /// </summary>
    public void Refresh()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | 0x0020u); // FRAMECHANGED
        _plate.InvalidateVisual();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            InvalidateVisual();
            _plate.InvalidateVisual();
        });
    }

    public void MoveTo(int x, int y)
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
