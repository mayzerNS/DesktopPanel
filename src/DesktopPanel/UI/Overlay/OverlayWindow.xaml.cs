using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Native;
using DesktopPanel.Features.Desktop;
using DesktopPanel.Features.Games;
using DesktopPanel.UI.Controls;
using DesktopPanel.UI.Theme;

namespace DesktopPanel.UI.Overlay;

/// <summary>
/// Окно-оверлей. Два вида:
///  • полный — рабочий стол (ярлыки, группы, поиск), настройки, Zapret, Диспетчер задач;
///  • «в уголке» — маленькая плашка с кнопками, её можно перетаскивать; положение запоминается.
/// Щелчок мимо окна его не закрывает (по настройке — сворачивает в уголок).
/// Поверх игры на весь экран: если панель не видна (эксклюзивный режим) — игра сворачивается,
/// после закрытия панели — разворачивается обратно (обе вещи отключаются в настройках).
/// </summary>
public partial class OverlayWindow : Window
{
    private const double BaseWidth = 1000, BaseHeight = 640; // размер в «логических» пикселях (при 100 %)

    public const string PageDesktop = "desktop", PageSettings = "settings", PageZapret = "zapret",
        PageTasks = "tasks", PageTrash = "trash", PageConfirm = "confirm";

    private IntPtr _hwnd;
    private bool _hiding;
    private string _page = PageDesktop;
    private string _pageBeforeConfirm = PageSettings;
    private IntPtr _game;            // игра на весь экран, поверх которой открыта панель
    private bool _gameMinimized;     // мы её свернули
    private bool _dragging;

    public OverlayWindow(IconLoader icons)
    {
        InitializeComponent();
        DesktopPage.AttachTrash(TrashZone, TrashGlyph, TrashLidTurn, TrashLidLift, BackGlyph, BackShift, HeaderPlate, HeaderBg, TitleShift,
            TrashPressLayer, TrashGlyphs, TrashHoverLayer); // корзина живёт в шапке, ведёт её страница рабочего стола
        DesktopPage.BackRequested += GoBack; // на других вкладках та же плашка — «Назад"
        Loaded += (_, _) => MatchCorners();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        DpiChanged += (_, _) =>
        {
            MatchCorners();
            if (IsMini) ApplyCollapse(_collapseK);
        };
        // окно уголка (MiniCorner) показывается и прячется вместе с панелью
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _corner?.Hide();
            else if (IsMini) ShowCorner();
        };
        Tips.Blur(SdrPopup);
        // пока открыта яркость SDR, уголок не сворачивается; закрыли, а мышь уже не над уголком — сворачивается как обычно
        SdrPopup.Closed += (_, _) =>
        {
            _sdrWatch.Stop();
            if (IsMini && Cfg.MiniCollapsed && !MiniMouseOver && !_dragging) _collapseDelay.Start();
        };
        SdrPopup.Opened += (_, _) =>
        {
            _sdrAwayMs = 0;
            _sdrDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            _sdrPressInside = true;
            if (IsMini) _sdrWatch.Start();
        };
        _sdrWatch.Tick += (_, _) => WatchSdrPopup();
        DesktopPage.Init(icons);
        TrashPage.Init(icons);
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Glass.Prepare(this);
            ApplyGlass();
        };
        ScreenClamp.Attach(this); // плашку нельзя утащить за край экрана
        // окно формально можно развернуть (Win+↑) — не даём
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        };
        SizeChanged += (_, _) =>
        {
            // плашка поменяла ширину (подписи кнопок) — не вылезаем за край; размер уголка ведём сами (SizeCorner)
            if (IsMini && !_dragging && SizeToContent != SizeToContent.Manual) PlaceMini();
        };
        // уголок, свёрнутый к краю, разворачивается, пока над ним мышь
        MouseEnter += (_, _) => MiniHover(true);
        MouseLeave += (_, _) => MiniHover(false);
        _collapseDelay.Tick += (_, _) =>
        {
            _collapseDelay.Stop();
            if (IsMini && Cfg.MiniCollapsed && !MiniMouseOver && !_dragging && !SdrPopup.IsOpen) AnimateCollapse(1);
        };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDownAnywhere;
        // уголок тащится за любую точку, даже за кнопку: сдвинули мышь с нажатой кнопкой — это перетаскивание, а не щелчок
        PreviewMouseLeftButtonDown += MiniPress;
        DesktopPage.LaunchRequested += () => HideOverlay(returnFocus: false);

        UpdateTexts();
        ApplyLook();
        UpdateHeader();
    }

    // ---------- вид ----------
    public bool IsMini { get; private set; }

    public bool IsShown => IsVisible && !_hiding;

    public IntPtr Handle => _hwnd;

    /// <summary>Панель показали или спрятали (для страниц, которые что-то обновляют, пока их видно).</summary>
    public event Action<bool>? ShownChanged;

    private Core.Config.AppConfig Cfg => App.Instance.Config;

    /// <summary>Стекло и затемнение — по настройкам (зовётся и при их смене).</summary>
    public void ApplyLook()
    {
        Tint.Opacity = Glass.DarkOpacity(Cfg.Darkness);
        Music.Enabled = Cfg.MusicVisualizer;
        if (_corner is not null) _corner.Tint.Opacity = Tint.Opacity;
        ApplyGlass();
        UpdateSearchBox();
    }

    private void ApplyGlass()
    {
        if (_hwnd != IntPtr.Zero && !_dragging) Glass.Apply(this);
    }

    /// <summary>
    /// Включили/выключили HDR или поменяли режим экрана: у открытой панели иногда оставалось одно размытие без содержимого.
    /// Через полсекунды перерисовываем всё и возвращаем непрозрачность (если панель не исчезает).
    /// </summary>
    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(RepaintAfterDisplayChange); // SystemEvents зовёт из своего потока

    private void RepaintAfterDisplayChange()
    {
        Core.Logging.Log.Info("display: поменялся режим экрана");
        Motion.RefreshMonitor();
        App.Instance.RefreshHdrAsync(); // HDR включили/выключили мимо программы (Win+Alt+B) — кнопка в шапке следом
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!IsVisible || _hiding) return;
            Anim.Stop(Body, "show");
            Body.Opacity = 1;
            BodyScale.ScaleX = BodyScale.ScaleY = 1;
            InvalidateVisual();
            Body.InvalidateVisual();
            UpdateLayout();
            ApplyGlass();
        };
        t.Start();
    }

    /// <summary>
    /// Углы окна скругляет Windows 11 — на 8 DIP («эффективных пикселей»: при 125 % это 10 пикселей экрана). Рамку окна,
    /// шапку и уголок рисуем с тем же радиусом. Раньше брали 8 пикселей экрана (8 / масштаб DIP) — рамка была круглее
    /// меньше, чем окно, и Windows «прикусывала» её на углах.
    /// </summary>
    private void MatchCorners()
    {
        const double r = Theme.Corners.WindowDip;
        OuterFrame.CornerRadius = new CornerRadius(r);
        HeaderPlate.CornerRadius = new CornerRadius(r);
        HeaderBg.CornerRadius = new CornerRadius(r);
        TrashZone.CornerRadius = new CornerRadius(r); // корзина рядом с шапкой — то же скругление
        _corner?.SetRadius(r);
    }

    /// <summary>Поиск можно отключить в настройках; подсказка — по тому, где он ищет.</summary>
    public void UpdateSearchBox()
    {
        SearchBox.Tag = Loc.T(Cfg.SearchSystem ? "overlay.search" : "overlay.search_desktop");
        // в полной панели — Hidden (держит место, кнопки шапки не прыгают), в уголке — Collapsed: иначе пустая дыра
        SearchHost.Visibility = !IsMini && Cfg.SearchEnabled && _page == PageDesktop ? Visibility.Visible
                              : IsMini ? Visibility.Collapsed : Visibility.Hidden;
        SearchArea.Visibility = IsMini ? Visibility.Collapsed : Visibility.Visible; // в уголке — без отступов поиска
        if (!Cfg.SearchEnabled && SearchBox.Text.Length > 0) SearchBox.Text = "";
        if (!Cfg.SearchEnabled || _page != PageDesktop) SetSearchOpen(false, animate: false);
    }

    // ---------- поиск: лупа, которая разворачивается ----------
    private const double SearchClosedWidth = 40, SearchOpenWidth = 380;
    private bool _searchOpen;

    /// <summary>Развернуть/свернуть поле поиска плавно (анимация идёт с частотой монитора).</summary>
    private void SetSearchOpen(bool open, bool animate = true)
    {
        if (_searchOpen == open && animate) return;
        _searchOpen = open;
        SearchBox.IsHitTestVisible = open;
        SearchBox.Focusable = open;
        UpdateSearchLook();
        if (!open && SearchBox.IsKeyboardFocusWithin)
        {
            // подсветка поля (рамка фокуса) гаснет вместе со сворачиванием
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, null);
        }
        // Поле всегда одного устройства: прижато вправо, ширина своя, не больше места (MaxWidth в разметке). Ни в начале,
        // ни в конце не переключаем на «растянуть по месту» — раньше в конце поле переключалось на растяжение и добирало
        // остаток прыжком («доехало, замерло, тэпнулось»), а ширина, равная месту, на долю пикселя раздвигала шапку.
        // Место изменилось, пока поле открыто (окно растянули), — ширина подстраивается (SearchArea_SizeChanged).
        var to = open ? SearchTargetWidth() : SearchClosedWidth;
        var from = SearchHost.ActualWidth > 0 ? SearchHost.ActualWidth : SearchClosedWidth;
        Anim.Stop(SearchHost, WidthProperty);
        // ⚠ режим раскладки не переключать ни в начале, ни в конце хода (Stretch/Auto): последний кусок шёл прыжком
        SearchHost.HorizontalAlignment = HorizontalAlignment.Right;
        SearchHost.Width = from;
        void Settle()
        {
            if (_searchOpen != open) return; // уже пошла обратная анимация
            SearchHost.Width = to;
        }
        if (animate && IsLoaded)
        {
            // то же время, что у плиток, без перелёта (поле упирается в заголовок и в лупу). Кубическое замедление, а не
            // пятой степени: у той последняя треть времени — меньше пикселя хода, поле будто вставало раньше конца.
            Core.Logging.PerfLog.AnimFor("поиск", Motion.UiMs);
            Anim.To(SearchHost, WidthProperty, to, Motion.UiMs, Motion.EaseOut(), Settle, from: from);
        }
        else Settle();
        if (open)
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }
    }

    // Свёрнутая лупа реагирует как кнопки шапки (тот же язык Fx): наведение — светлый слой и яркий значок, нажатие — слой
    // нажатия и вдавливание, открывается по отпусканию над ней (увели мышь — не открылась). Развёрнутое поле — обычное
    // поле ввода со своей подсветкой (InputBox), слои кнопки гаснут.
    private bool _searchPressed;

    /// <summary>Ширина открытого поля: всё место, но не шире SearchOpenWidth (в пикселях экрана — целое).</summary>
    private double SearchTargetWidth()
    {
        var area = SearchArea.ActualWidth > 0 ? SearchArea.ActualWidth : SearchOpenWidth;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var w = Math.Floor(Math.Min(area, SearchOpenWidth) * dpi) / dpi;
        return Math.Max(SearchClosedWidth, w);
    }

    private void SearchArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_searchOpen && !Anim.IsRunning(SearchHost, WidthProperty)) SearchHost.Width = SearchTargetWidth();
    }

    private void SearchHost_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_searchOpen) return;
        e.Handled = true;
        _searchPressed = SearchHost.CaptureMouse();
        UpdateSearchLook();
    }

    private void SearchHost_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_searchPressed) return;
        e.Handled = true;
        var over = SearchHost.IsMouseOver
                   || new Rect(SearchHost.RenderSize).Contains(e.GetPosition(SearchHost));
        _searchPressed = false;
        SearchHost.ReleaseMouseCapture();
        if (over) SetSearchOpen(true);
        UpdateSearchLook();
    }

    private void SearchHost_LostCapture(object sender, MouseEventArgs e)
    {
        if (!_searchPressed) return;
        _searchPressed = false;
        UpdateSearchLook();
    }

    private void SearchHost_Hover(object sender, MouseEventArgs e) => UpdateSearchLook();

    private void UpdateSearchLook()
    {
        var button = !_searchOpen;
        var hot = button && SearchHost.IsMouseOver;
        var down = button && _searchPressed && new Rect(SearchHost.RenderSize).Contains(Mouse.GetPosition(SearchHost));
        Fx.SetShow(SearchHoverLayer, hot || _searchPressed);
        Fx.SetShow(SearchPressLayer, down);
        Fx.SetPress(SearchHost, down);
        // значок: ярче под мышью и в открытом поле
        var to = hot || _searchOpen ? Color.FromArgb(0xFF, 255, 255, 255) : Color.FromArgb(0xA0, 255, 255, 255);
        if (SearchGlyph.Foreground is not SolidColorBrush { IsFrozen: false } b)
            SearchGlyph.Foreground = b = new SolidColorBrush(((SolidColorBrush)SearchGlyph.Foreground).Color);
        Anim.Color(b, to, hot || _searchOpen ? Motion.HoverInMs : Motion.HoverOutMs, Motion.EaseOut());
    }

    private void SearchBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // потеряли фокус при пустом тексте — снова лупа
        if (_searchOpen && SearchBox.Text.Length == 0 && !SearchBox.IsKeyboardFocusWithin) SetSearchOpen(false);
    }

    /// <summary>
    /// Первый показ окна самый медленный (WPF собирает всё, Windows готовит стекло) — делаем его при запуске
    /// программы, за краем экрана и невидимо, чтобы потом панель открывалась сразу.
    /// </summary>
    public void WarmUp()
    {
        var activated = ShowActivated;
        ShowActivated = false;
        Body.Opacity = 0;
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();
        Hide();
        ShowActivated = activated;
    }

    private void UpdateTexts()
    {
        TasksButton.Text = Loc.T("overlay.tasks");
        PipButton.Text = Loc.T("overlay.pip");
        SettingsButton.Text = "";
        SettingsButton.Tip = Loc.T("overlay.settings");
        MinimizeButton.Tip = Loc.T("overlay.minimize");
        ExpandButton.Tip = Loc.T("overlay.expand");
        CloseButton.Tip = Loc.T("overlay.close");
    }

    // ---------- показ / скрытие ----------
    /// <summary>Сочетание клавиш: закрыто — открыть; в уголке — развернуть; полная панель — закрыть.</summary>
    public void Toggle()
    {
        if (IsShown && IsMini) GoFull();
        else if (IsShown) HideOverlay();
        else ShowOverlay();
    }

    public void ShowOverlay(bool settings = false) => ShowOverlay(settings ? PageSettings : PageDesktop);

    public void ShowOverlay(string page)
    {
        var wasHiding = _hiding;
        _hiding = false;
        if (!IsVisible)
        {
            new WindowInteropHelper(this).EnsureHandle();
            _game = GameWindow.FullscreenForeground(_hwnd);
            _gameMinimized = false;
            if (IsMini) SetMini(false);
            PlaceOnCursorMonitor();
            ShowPage(page);
            SearchBox.Text = "";
            SetSearchOpen(false, animate: false);
            Show();
            PlaceOnCursorMonitor(); // ещё раз: при переходе на монитор с другим масштабом WPF мог подправить размер
            Animate(show: true);
            ShownChanged?.Invoke(true);
            if (_game != IntPtr.Zero) CheckVisibleOverGame();
            App.Instance.RefreshHdrAsync(); // в фоне: кнопка HDR — по настоящему состоянию
        }
        else
        {
            if (wasHiding) Animate(show: true); // нажали снова, пока панель исчезала
            if (IsMini) SetMiniAnimated(false);
            ShowPage(page);
        }
        GameWindow.ReleaseCursor();
        UpdateHeader();
        BringToFront();
        App.Instance.Zapret.RefreshAsync();
    }

    /// <summary>
    /// Игра в эксклюзивном полноэкранном режиме рисует поверх всех окон — тогда панель не получает фокус.
    /// Если так и вышло, сворачиваем игру (если это разрешено в настройках).
    /// </summary>
    private void CheckVisibleOverGame()
    {
        var game = _game;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!IsShown || game != _game) return;
            if (NativeMethods.GetForegroundWindow() == _hwnd || !Cfg.GameMinimize) return;
            GameWindow.Minimize(game);
            _gameMinimized = true;
            BringToFront();
        };
        t.Start();
    }

    public void HideOverlay(bool returnFocus = true)
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        Animate(show: false, () =>
        {
            if (!_hiding) return;
            Hide();
            _hiding = false;
            if (IsMini) SetMini(false); // в следующий раз откроется полная панель
            TasksPage.SetActive(false);
            TrashPage.SetActive(false);
            if (returnFocus) GiveBackToGame();
            _game = IntPtr.Zero;
            ShownChanged?.Invoke(false);
        });
    }

    /// <summary>Вернуть игру: развернуть, если мы её свернули (и это разрешено), и отдать ей фокус.</summary>
    private void GiveBackToGame()
    {
        if (_game == IntPtr.Zero) return;
        if (_gameMinimized && !Cfg.GameRestore) return;
        GameWindow.Restore(_game, _gameMinimized);
        _gameMinimized = false;
    }

    private void Animate(bool show, Action? done = null)
    {
        Core.Logging.PerfLog.AnimFor(show ? "показ панели" : "скрытие панели", show ? 170 : 120);
        // панель проявляется и чуть «вырастает» (исчезает — наоборот); картинкой, без перерисовки содержимого каждый кадр
        double o0 = show ? 0 : Body.Opacity, o1 = show ? 1 : 0, s0 = show ? 0.97 : BodyScale.ScaleX, s1 = show ? 1 : 0.98;
        var token = CacheBody(true);
        Anim.Run(Body, "show", show ? 170 : 120, k =>
        {
            Body.Opacity = o0 + (o1 - o0) * k;
            BodyScale.ScaleX = BodyScale.ScaleY = s0 + (s1 - s0) * k;
        }, show ? Motion.EaseOut() : Motion.EaseIn(), () =>
        {
            CacheBody(false, token);
            done?.Invoke();
        });
    }

    private NativeMethods.RECT CursorWorkArea(out double scale)
    {
        NativeMethods.GetCursorPos(out var pt);
        var mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return WorkArea(mon, out scale);
    }

    private static NativeMethods.RECT WorkArea(IntPtr mon, out double scale)
    {
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(mon, ref mi);
        scale = NativeMethods.GetDpiForMonitor(mon, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return mi.rcWork;
    }

    /// <summary>По центру монитора, на котором сейчас курсор. Размер — 1000×640 с учётом масштаба этого монитора.</summary>
    private void PlaceOnCursorMonitor()
    {
        if (_hwnd == IntPtr.Zero) return;
        var work = CursorWorkArea(out var scale);
        var w = (int)Math.Min(BaseWidth * scale, work.Width * 0.85);
        var h = (int)Math.Min(BaseHeight * scale, work.Height * 0.85);
        var x = work.Left + (work.Width - w) / 2;
        var y = work.Top + (work.Height - h) / 2;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Вывести окно на передний план (и забрать фокус клавиатуры), даже если сейчас активна другая программа.</summary>
    private void BringToFront()
    {
        NativeMethods.BringWindowToTop(_hwnd);
        if (NativeMethods.SetForegroundWindow(_hwnd))
        {
            Activate();
            return;
        }
        // запасной путь: «прицепиться» к потоку активного окна (может подождать, если тот поток занят)
        var fg = NativeMethods.GetForegroundWindow();
        var fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
        var me = NativeMethods.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && NativeMethods.AttachThreadInput(fgThread, me, true);
        try
        {
            NativeMethods.BringWindowToTop(_hwnd);
            NativeMethods.SetForegroundWindow(_hwnd);
            Activate();
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(fgThread, me, false);
        }
    }

    // ---------- «в уголок» ----------
    /// <summary>Свернуть в уголок (кнопка или потеря фокуса — по настройке).</summary>
    /// <summary>
    /// Сочетание «вкладка»: панель закрыта или на другой вкладке — открыть эту; уже на ней — закрыть панель.
    /// В уголке — развернуть сразу на этой вкладке.
    /// </summary>
    public void TogglePage(string page)
    {
        if (IsShown && IsMini) GoFull(page);
        else if (IsShown && _page == page) HideOverlay();
        else ShowOverlay(page);
    }

    /// <summary>Сочетание «уголок»: закрыта — открыть; полная — в уголок; в уголке — развернуть.</summary>
    public void ToggleMini()
    {
        if (!IsShown) ShowOverlay();
        else if (IsMini) GoFull();
        else GoMini();
    }

    /// <summary>Сочетание «поиск»: панель с рабочим столом и раскрытым полем поиска в фокусе.</summary>
    public void OpenSearch()
    {
        if (!Cfg.SearchEnabled) return;
        if (IsShown && IsMini) GoFull(PageDesktop);
        else if (!IsShown || _page != PageDesktop) ShowOverlay(PageDesktop);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => SetSearchOpen(true)); // когда панель уже на месте
    }

    public void GoMini()
    {
        if (!IsShown || IsMini) return;
        SetMiniAnimated(true);
    }

    public void GoFull(string? page = null)
    {
        if (!IsMini) return;
        if (page is not null) _page = page;
        SetMiniAnimated(false);
    }

    /// <summary>Панель тает, перестраивается, пока её не видно, и проявляется на новом месте (без дёрганья размера).</summary>
    private void SetMiniAnimated(bool mini)
    {
        Core.Logging.PerfLog.AnimFor(mini ? "в уголок" : "из уголка", 270);
        Core.Logging.PerfLog.Mark(mini ? "в уголок" : "из уголка");
        // Панель гаснет картинкой (BitmapCache — не перерисовывая каждый кадр все значки и размытия), перестраивается и меняет
        // размер, пока её не видно, и проявляется только после того, как новый вид уже отрисован: раньше перестройка всей сетки
        // попадала в первые кадры проявления — переход шёл на 43–54 кадрах/с с рывками.
        var token = CacheBody(true);
        var o0 = Body.Opacity;
        Anim.Run(Body, "show", 110, k => Body.Opacity = o0 * (1 - k), Motion.EaseIn(), () =>
        {
            if (token != _cacheToken) return; // переход перебили новым
            try
            {
                // ⚠ показ окна уголка отсюда (из конца анимации) прокачивает сообщения — WPF рисует вложенный кадр;
                // FrameClock его пропускает целиком, этого не трогать
                SetMini(mini);
                if (mini) GiveBackToGame(); // плашка не мешает играть
                else BringToFront();
            }
            catch (Exception ex)
            {
                // панель всё равно проявляется — иначе она оставалась невидимой и не отзывалась на сочетание клавиш
                Core.Logging.Log.Error(mini ? "уголок: ошибка при переходе в уголок" : "уголок: ошибка при выходе из уголка", ex);
            }
            AfterFrames(2, () =>
            {
                if (token != _cacheToken) return;
                Anim.Run(Body, "show", 160, k => Body.Opacity = k, Motion.EaseOut(), () => CacheBody(false, token));
            });
        });
    }

    private int _cacheToken;

    /// <summary>
    /// На время анимации прозрачности/размера всей панели — рисуем её одной картинкой. Возвращает номер, по которому
    /// кэш снимется только своей же анимацией (если её перебили новой — снимет новая).
    /// </summary>
    private int CacheBody(bool on, int token = 0)
    {
        if (on)
        {
            Body.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
            return ++_cacheToken;
        }
        if (token == _cacheToken) Body.CacheMode = null; // снова вектором: текст и значки чёткие
        return token;
    }

    /// <summary>Выполнить action после n отрисованных кадров (чтобы тяжёлая перестройка уже прошла и была показана).</summary>
    private static void AfterFrames(int n, Action action)
    {
        if (!Motion.Enabled)
        {
            action();
            return;
        }
        EventHandler? h = null;
        h = (_, _) =>
        {
            if (--n > 0) return;
            CompositionTarget.Rendering -= h;
            action();
        };
        CompositionTarget.Rendering += h;
    }

    private void SetMini(bool mini)
    {
        _sideHome = null;
        Anim.Stop(this, "yield");
        IsMini = mini;
        Anim.Stop(this, "collapse");
        _collapsing = false;
        Pages.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        UpdateSearchBox();
        Body.Margin = mini ? new Thickness(0) : new Thickness(7, 7, 7, 14); // от края окна до кромки шапки — 7 px со всех сторон
        Pages.Margin = mini ? new Thickness(0) : new Thickness(11, 10, 11, 0); // содержимое — как раньше, на 18 px от края
        TasksPage.SetActive(!mini && _page == PageTasks);
        TrashPage.SetActive(!mini && _page == PageTrash);

        // плашка не забирает фокус у игры при щелчке по кнопкам
        if (_hwnd != IntPtr.Zero)
        {
            var ex = NativeMethods.ExStyle(_hwnd);
            NativeMethods.SetExStyle(_hwnd, mini ? ex | NativeMethods.WS_EX_NOACTIVATE : ex & ~NativeMethods.WS_EX_NOACTIVATE);
        }

        // в уголке затемнение, рамку и кнопки рисует окно MiniCorner; у самого окна остаются размытие и тень Windows
        Tint.Visibility = OuterFrame.Visibility = mini ? Visibility.Hidden : Visibility.Visible;
        Music.Visibility = mini ? Visibility.Collapsed : Visibility.Visible; // в уголке — без музыки (и без захвата звука)
        MoveButtons(mini);
        SizeToContent = SizeToContent.Manual;
        ClearValue(WidthProperty); // размер окна ведём сами (SizeCorner / PlaceOnCursorMonitor) — WPF свой не держит
        ClearValue(HeightProperty);
        if (mini)
        {
            _collapseK = Cfg.MiniCollapsed && !MiniMouseOver ? 1 : 0;
            UpdateHeader(); // кнопки уголка и его ширина (ApplyCollapse)
            PlaceMini();
            UpdateMiniSide(force: true);
            ShowCorner();
        }
        else
        {
            _corner?.Hide();
            UpdateHeader();
            PlaceOnCursorMonitor();
            ShowPage(_page);
        }
    }

    // ---------- уголок: два окна (см. MiniCorner) ----------
    private MiniCorner? _corner;
    private int _buttonsIndex = -1;
    private double _miniW, _miniFull; // ширина видимой части уголка сейчас и развёрнутого (DIP)
    private bool _collapsing;          // идёт сворачивание/разворачивание
    private const double MiniH = MiniBtn + MiniEdge * 2;

    /// <summary>Мышь над уголком (над его окном с кнопками или над самим окном панели).</summary>
    private bool MiniMouseOver => IsMouseOver || _corner is { IsVisible: true, IsMouseOver: true };

    private MiniCorner EnsureCorner()
    {
        if (_corner is not null) return _corner;
        var c = new MiniCorner { Owner = this }; // принадлежит панели: всегда над ней, мини-браузер — под обоими
        // появление и исчезновение — вместе с содержимым панели
        c.Root.SetBinding(OpacityProperty, new Binding(nameof(Opacity)) { Source = Body });
        BindingOperations.SetBinding(c.Scale, ScaleTransform.ScaleXProperty, new Binding(nameof(ScaleTransform.ScaleX)) { Source = BodyScale });
        BindingOperations.SetBinding(c.Scale, ScaleTransform.ScaleYProperty, new Binding(nameof(ScaleTransform.ScaleY)) { Source = BodyScale });
        // мышь: тащить за любую точку, разворачивать при наведении — как у самой панели
        c.PreviewMouseLeftButtonDown += MiniPress;
        c.MouseEnter += (_, _) => MiniHover(true);
        c.MouseLeave += (_, _) => MiniHover(false);
        c.DpiChanged += (_, _) => Dispatcher.InvokeAsync(() => SizeCorner(_miniW));
        c.Tint.Opacity = Tint.Opacity;
        c.SetRadius(OuterFrame.CornerRadius.TopLeft);
        new WindowInteropHelper(c).EnsureHandle();
        _corner = c;
        return c;
    }

    /// <summary>Кнопки шапки — в окно уголка (и обратно в шапку панели).</summary>
    private void MoveButtons(bool toCorner)
    {
        if (toCorner)
        {
            if (HeaderButtons.Parent != Header) return;
            var c = EnsureCorner();
            _buttonsIndex = Header.Children.IndexOf(HeaderButtons);
            Header.Children.Remove(HeaderButtons);
            c.Host.Child = HeaderButtons;
        }
        else
        {
            if (_corner is null || _corner.Host.Child != HeaderButtons) return;
            _corner.Host.Child = null;
            Header.Children.Insert(Math.Clamp(_buttonsIndex, 0, Header.Children.Count), HeaderButtons);
        }
    }

    private void ShowCorner()
    {
        if (_corner is not { } c || !IsVisible || !IsMini) return;
        SizeCorner(_miniW);
        if (!c.IsVisible) c.Show();
        SizeCorner(_miniW); // ещё раз: показ мог сдвинуть окно
    }

    /// <summary>
    /// Размер и место обоих окон уголка от прижатого края: нижнее (размытие, тень) — шириной blurDip, верхнее (кнопки) —
    /// шириной развёрнутого уголка. Одним вызовом Windows (DeferWindowPos) — оба на экране в одном кадре.
    /// </summary>
    private void SizeCorner(double blurDip)
    {
        // ⚠ окно с кнопками (MiniCorner) на ходу не сужать и не двигать — только нижнее окно с размытием: WPF рисует
        // кадром позже, чем Windows двигает окно, — кнопки дрожали (пробовали SetWindowRgn, WVR_ALIGNRIGHT, снимок)
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var r)) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var ph = (int)Math.Round(MiniH * dpi);
        var pw = (int)Math.Round(blurDip * dpi);
        var anchor = _sideRight ? r.Right : r.Left;
        var bx = _sideRight ? anchor - pw : anchor;
        var moveBlur = bx != r.Left || pw != r.Width || ph != r.Height;
        var ch = _corner?.Handle ?? IntPtr.Zero;
        var moveCorner = false;
        int cx = 0, fw = 0;
        if (ch != IntPtr.Zero && NativeMethods.GetWindowRect(ch, out var cr))
        {
            fw = Math.Max(pw, (int)Math.Round(_miniFull * dpi));
            cx = _sideRight ? anchor - fw : anchor;
            moveCorner = cx != cr.Left || r.Top != cr.Top || fw != cr.Width || ph != cr.Height;
        }
        if (!moveBlur && !moveCorner) return;
        const uint f = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        var h = BeginDeferWindowPos(2);
        if (moveBlur) h = DeferWindowPos(h, _hwnd, IntPtr.Zero, bx, r.Top, pw, ph, f);
        if (moveCorner && h != IntPtr.Zero) h = DeferWindowPos(h, ch, IntPtr.Zero, cx, r.Top, fw, ph, f);
        if (h != IntPtr.Zero) EndDeferWindowPos(h);
    }

    /// <summary>Сдвинуть уголок (оба окна разом): left, top — видимой части.</summary>
    private void MoveCorner(int left, int top)
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var r)) return;
        const uint f = NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        var ch = _corner is { IsVisible: true } c ? c.Handle : IntPtr.Zero;
        var h = BeginDeferWindowPos(2);
        h = DeferWindowPos(h, _hwnd, IntPtr.Zero, left, top, 0, 0, f);
        if (ch != IntPtr.Zero && h != IntPtr.Zero && NativeMethods.GetWindowRect(ch, out var cr))
            h = DeferWindowPos(h, ch, IntPtr.Zero, _sideRight ? left + r.Width - cr.Width : left, top, 0, 0, f);
        if (h != IntPtr.Zero) EndDeferWindowPos(h);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr BeginDeferWindowPos(int n);

    [DllImport("user32.dll")]
    private static extern IntPtr DeferWindowPos(IntPtr h, IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EndDeferWindowPos(IntPtr h);

    // ---------- уголок: сворачивание к краю экрана ----------
    // Кнопка-стрелка включает «сворачиваться»: уголок плавно сужается к ближнему краю экрана (тот бок остаётся на месте),
    // видны только крестик и маленькая стрелка; мышь навели — разворачивается, ушла — через полсекунды сворачивается.
    // Ход — Anim (предел кадров и тумблер анимаций действуют и здесь).
    private const double MiniBtn = 28, MiniArrow = 16, MiniGap = 4;
    private bool _sideRight = true;   // ближе к правому краю — сворачивается вправо
    private double _collapseK;        // 0 — развёрнут, 1 — свёрнут
    private bool _hoverBlocked;       // свернули кнопкой, пока мышь над уголком: развернуть — только когда мышь уйдёт и вернётся
    private readonly DispatcherTimer _collapseDelay = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        Cfg.MiniCollapsed = !Cfg.MiniCollapsed;
        App.Instance.SaveConfig();
        _hoverBlocked = Cfg.MiniCollapsed;
        AnimateCollapse(Cfg.MiniCollapsed ? 1 : 0);
    }

    private void MiniHover(bool over)
    {
        if (!IsMini || !Cfg.MiniCollapsed || _dragging) return;
        _collapseDelay.Stop();
        if (over)
        {
            if (!_hoverBlocked) AnimateCollapse(0);
        }
        else
        {
            _hoverBlocked = false;
            _collapseDelay.Start();
        }
    }

    private void AnimateCollapse(double to)
    {
        var from = _collapseK;
        UpdateCollapseGlyph();
        if (Math.Abs(from - to) < 0.001)
        {
            ApplyCollapse(to);
            return;
        }
        _collapsing = true;
        Core.Logging.PerfLog.AnimFor(to > from ? "сворачивание уголка" : "разворачивание уголка", 420);
        Anim.Run(this, "collapse", 420, k => ApplyCollapse(from + (to - from) * k), Motion.EaseOut(), () =>
        {
            _collapsing = false;
            ApplyCollapse(_collapseK); // нижнее окно — точно по видимой части
        });
    }

    private void UpdateCollapseGlyph()
    {
        // стрелка показывает, что будет по щелчку: в режиме «сворачиваться» — развернуть (от края), иначе — свернуть (к краю)
        var toEdge = !Cfg.MiniCollapsed;
        CollapseButton.Glyph = toEdge == _sideRight ? "\uE76C" : "\uE76B";
        CollapseButton.Tip = Loc.T(Cfg.MiniCollapsed ? "overlay.mini_pin" : "overlay.mini_collapse");
    }

    /// <summary>К какому краю экрана уголок ближе; поменялось — переставить крестик и стрелку к этому краю.</summary>
    private void UpdateMiniSide(bool force = false)
    {
        if (!IsMini || MiniRect is not { } r) return;
        var wa = WindowDrag.WorkAreaOf(r);
        var right = r.Left + r.Width / 2 > wa.Left + wa.Width / 2;
        if (right == _sideRight && !force) return;
        _sideRight = right;
        ArrangeMini();
        UpdateCollapseGlyph();
        ApplyCollapse(_collapseK);
    }

    /// <summary>
    /// Порядок кнопок: у прижатого к краю бока — стрелка и крестик (их и видно в свёрнутом виде). Справа: … стрелка, крестик;
    /// слева: крестик, стрелка, … В полной панели — как в разметке (стрелки нет).
    /// </summary>
    private void ArrangeMini()
    {
        var kids = HeaderButtons.Children;
        var left = IsMini && !_sideRight;
        var ok = left ? kids.IndexOf(CloseButton) == 0 && kids.IndexOf(CollapseButton) == 1
                      : kids.IndexOf(CloseButton) == kids.Count - 1 && kids.IndexOf(CollapseButton) == kids.Count - 2;
        if (ok) return;
        kids.Remove(CollapseButton);
        kids.Remove(CloseButton);
        if (left)
        {
            kids.Insert(0, CloseButton);
            kids.Insert(1, CollapseButton);
        }
        else
        {
            kids.Add(CollapseButton);
            kids.Add(CloseButton);
        }
    }

    /// <summary>
    /// Свёрнутость k. Видимая часть уголка сужается к прижатому краю (тот бок стоит на месте), крестик и стрелка прижаты к нему.
    /// Кнопки, подложка и рамка — в окне MiniCorner, которое при этом не двигается (дрожать нечему); окно панели под ним
    /// (размытие, тень) сужается вслед. Пока идёт ход, оно на кадр отстаёт при сужении — так размытие всегда не уже рисунка.
    /// </summary>
    private void ApplyCollapse(double k)
    {
        _collapseK = k;
        if (!IsMini) return;
        HeaderButtons.HorizontalAlignment = _sideRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        // ⚠ ширины и поля кнопок на ходу не менять — только RenderTransform (иначе перескоки на пиксель и рывок в конце).
        // Раскладка кнопок всегда как у развёрнутого уголка (целые пиксели, чётко). Сжатие и сдвиг на ходу — только смещением
        // отрисовки (RenderTransform): оно к пикселям не подгоняется, поэтому кнопки идут плавно, без перескоков на соседний
        // пиксель ни вбок, ни вверх-вниз; в конце смещение — ноль (или кнопка не видна), и переходить «в статику» нечему.
        var list = new List<(IconButton B, double W, double M0, double Mk, double Wk)>();
        var first = true;
        foreach (var b in HeaderButtons.Children.OfType<IconButton>())
        {
            if (b.Visibility != Visibility.Visible) continue;
            b.MinWidth = 0; // у стиля кнопок MinWidth 32 — иначе крестик и стрелка выходили шире заданного, поля — неровными
            b.LayoutTransform = Transform.Identity;
            b.UseLayoutRounding = true;
            var essential = b == CloseButton || b == CollapseButton;
            var w = b == CollapseButton ? MiniArrow : MiniBtn;
            var m = first ? 0 : MiniGap;
            first = false;
            b.Width = w;
            b.Margin = new Thickness(m, 0, 0, 0);
            if (essential)
            {
                b.Opacity = 1;
                b.IsHitTestVisible = true;
                // справа перед стрелкой были остальные кнопки — промежуток уходит с ними
                list.Add((b, w, m, b == CollapseButton && _sideRight ? m * (1 - k) : m, w));
            }
            else
            {
                var sc = 1 - k;
                b.Opacity = Math.Min(1, sc * 1.6);
                b.IsHitTestVisible = k < 0.5;
                list.Add((b, w, m, m * sc, w * sc));
            }
        }
        double w0 = 0, wk = 0;
        foreach (var e in list)
        {
            w0 += e.M0 + e.W;
            wk += e.Mk + e.Wk;
        }
        // где кнопка стоит в раскладке (x0) и где должна быть при этом k (xk) — от левого края полосы кнопок;
        // полоса прижата к краю уголка, поэтому справа к разнице добавляется сдвиг её левого края (w0 − wk)
        double x0 = 0, xk = 0, shift = _sideRight ? w0 - wk : 0;
        foreach (var e in list)
        {
            x0 += e.M0;
            xk += e.Mk;
            var dx = shift + xk - x0;
            var sc = e.Wk / e.W;
            if (Math.Abs(dx) < 1e-6 && Math.Abs(sc - 1) < 1e-6) e.B.RenderTransform = Transform.Identity;
            else
            {
                if (e.B.RenderTransform is not MatrixTransform { IsFrozen: false } mt) e.B.RenderTransform = mt = new MatrixTransform();
                // сжатие к середине по высоте и к левому краю по ширине (как в раскладке со сжатыми кнопками), потом сдвиг
                var h = e.B.ActualHeight > 0 ? e.B.ActualHeight : MiniBtn;
                mt.Matrix = new Matrix(sc, 0, 0, sc, dx, h * (1 - sc) / 2);
            }
            x0 += e.W;
            xk += e.Wk;
        }
        var width = MiniEdge * 2 + wk; // ширина видимой части при этом k
        var prev = _miniW;
        _miniW = width;
        _miniFull = MiniEdge * 2 + w0;
        _corner?.SetVisible(width, _sideRight);
        SizeCorner(_collapsing ? Math.Max(width, prev) : width);
    }

    private const double MiniEdge = 6; // от края уголка до кнопок: рамка шапки 1 + поле 5

    /// <summary>Уголок закрыли или развернули в полную панель — кнопки снова у правого края.</summary>
    private void ResetMiniClip() => HeaderButtons.HorizontalAlignment = HorizontalAlignment.Right;

    /// <summary>Плашка — там, где её оставили (или в правом нижнем углу), не вылезая за край экрана.</summary>
    private void PlaceMini()
    {
        if (_hwnd == IntPtr.Zero) return;
        var scaleNow = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Round(_miniW * scaleNow), h = (int)Math.Round(MiniH * scaleNow);
        NativeMethods.RECT work;
        int x, y;
        if (Cfg.MiniX is { } mx && Cfg.MiniY is { } my)
        {
            var mon = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = mx + w / 2, Y = my + h / 2 },
                NativeMethods.MONITOR_DEFAULTTONEAREST);
            work = WorkArea(mon, out _);
            x = mx;
            y = my;
        }
        else
        {
            work = CursorWorkArea(out var s);
            x = work.Right - w - (int)(24 * s);
            y = work.Bottom - h - (int)(24 * s);
        }
        x = Math.Max(work.Left, Math.Min(x, work.Right - w));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - h));
        var r = new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        if (App.Instance.Pip?.PanelBounds is { } pip && WindowPush.Separate(pip, r, Gap, verticalOnly: true) is { } free) r = free; // запомненное место не меняем
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, r.Left, r.Top, w, h, NativeMethods.SWP_NOACTIVATE);
        SizeCorner(_miniW); // окно с кнопками — к нему
    }

    /// <summary>Зазор между уголком и мини-браузером (пиксели экрана).</summary>
    private int Gap => (int)Math.Round(6 * VisualTreeHelper.GetDpi(this).DpiScaleX);

    /// <summary>Где сейчас уголок (пиксели экрана); null — уголка нет на экране.</summary>
    internal NativeMethods.RECT? MiniRect =>
        IsMini && IsShown && _hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var r) ? r : null;

    /// <summary>Сдвинуть уголок (его расталкивает мини-браузер): x, y — видимой части.</summary>
    public void MoveMini(int x, int y)
    {
        if (!IsMini) return;
        ForgetSideHome(); // толкает сам браузер — уголок остаётся там, куда его отодвинули
        MoveCorner(x, y);
    }

    /// <summary>Запомнить, где теперь уголок (его отодвинули, пока тащили мини-браузер).</summary>
    public void SaveMiniPlace()
    {
        UpdateMiniSide();
        if (!IsMini || _hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var r) || (Cfg.MiniX == r.Left && Cfg.MiniY == r.Top)) return;
        Cfg.MiniX = r.Left;
        Cfg.MiniY = r.Top;
        App.Instance.SaveConfig();
    }

    /// <summary>Мини-браузер появился или вырос поверх уголка — уголок отходит туда, куда ему ближе.</summary>
    public void AvoidPip()
    {
        if (MiniRect is not { } me || App.Instance.Pip?.PanelBounds is not { } pip) return; // браузер вместе с его панелью
        if (WindowPush.Separate(pip, me, Gap, verticalOnly: true) is { } free) MoveMini(free.Left, free.Top);
    }

    // ---------- уголок уступает место боковой панели мини-браузера ----------
    // Панель появилась и задевает уголок — уголок плавно уходит вбок (от браузера вместе с панелью), панель
    // спряталась — плавно возвращается. Куда возвращаться, помнится одно место (_sideHome) — откуда ушёл в первый раз,
    // поэтому панель может появляться и прятаться хоть посреди движения: уголок едет от того места, где он сейчас, к нужной
    // точке, без рывков. Своё перетаскивание уголка или толчок самим браузером это место забывают — уголок остаётся, где его оставили.
    // Место — по прижатому краю (правому у уголка справа), а не по левому: левый край у уголка справа сдвигается, пока он
    // сворачивается или разворачивается (наведение мыши), — по нему уголок возвращался не туда.
    private NativeMethods.POINT? _sideHome; // X — прижатый край, Y — верх

    private int AnchorOf(NativeMethods.RECT r) => _sideRight ? r.Right : r.Left;

    private NativeMethods.RECT RectAt(int anchor, int top, NativeMethods.RECT size) =>
        _sideRight ? new() { Left = anchor - size.Width, Top = top, Right = anchor, Bottom = top + size.Height }
                   : new() { Left = anchor, Top = top, Right = anchor + size.Width, Bottom = top + size.Height };

    /// <summary>Панель браузера показалась (её прямоугольник вместе с браузером) или спряталась (null).</summary>
    internal void YieldToPipPanel(NativeMethods.RECT? panel)
    {
        if (!IsMini || !IsShown || _dragging || _dragArmed || MiniRect is not { } cur) return;
        var home = _sideHome ?? new NativeMethods.POINT { X = AnchorOf(cur), Y = cur.Top };
        var homeRect = RectAt(home.X, home.Y, cur);
        NativeMethods.RECT target;
        if (panel is { } p && WindowPush.Separate(p, homeRect, Gap, horizontalOnly: true) is { } free) // от панели — вбок
        {
            _sideHome = home; // уходим — запомнить, откуда
            target = free;
        }
        else
        {
            if (_sideHome is null) return; // не уходили — нечего возвращать
            // возвращаемся — если там не встал сам браузер (его могли передвинуть)
            if (App.Instance.Pip?.Bounds is { } pip && WindowPush.Separate(pip, homeRect, Gap) is not null)
            {
                _sideHome = null;
                return;
            }
            // ⚠ не возвращаться, пока курсор на уголке или на его месте: тянулись к уголку — панель браузера пряталась
            // (курсор на уголке), уголок уезжал домой из-под курсора, курсор оказывался над браузером, панель снова
            // появлялась — уголок «бегал» туда-сюда каждые полсекунды (perf.log 02:03). Ждём, пока курсор уйдёт
            if (CursorNear(cur) || CursorNear(homeRect))
            {
                _homeWait.Stop();
                _homeWait.Start();
                return;
            }
            target = homeRect;
        }
        _homeWait.Stop();
        var back = AnchorOf(target) == home.X && target.Top == home.Y;
        if (panel is { } pp && Overlap(target, pp))
            Core.Logging.PerfLog.Note($"уголок: некуда уступить панели браузера (уголок {target.Left},{target.Top} {target.Width}×{target.Height}, " +
                                      $"панель {pp.Left},{pp.Top} {pp.Width}×{pp.Height})");
        else if (AnchorOf(target) != AnchorOf(cur) || target.Top != cur.Top)
            Core.Logging.PerfLog.Note($"уголок: {(back ? "возвращается" : "уступает панели браузера")} → {target.Left},{target.Top}");
        SlideCorner(AnchorOf(target), target.Top, back);
    }

    // ждём, пока курсор уйдёт с уголка, чтобы вернуть его на место (тикает только пока ждём)
    private DispatcherTimer? _homeWaitTimer;

    private DispatcherTimer _homeWait
    {
        get
        {
            if (_homeWaitTimer is not null) return _homeWaitTimer;
            _homeWaitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _homeWaitTimer.Tick += (_, _) =>
            {
                _homeWaitTimer.Stop();
                // панель браузера снова на экране — пусть решает её появление; нет — пробуем вернуться
                if (App.Instance.Pip?.PanelBounds is { } p && App.Instance.Pip.Bounds is { } b && (p.Width != b.Width || p.Height != b.Height)) return;
                YieldToPipPanel(null);
            };
            return _homeWaitTimer;
        }
    }

    private bool CursorNear(NativeMethods.RECT r)
    {
        NativeMethods.GetCursorPos(out var p);
        var m = Gap * 2;
        return p.X >= r.Left - m && p.X < r.Right + m && p.Y >= r.Top - m && p.Y < r.Bottom + m;
    }

    private bool Overlap(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Left - Gap < b.Right && a.Right + Gap > b.Left && a.Top - Gap < b.Bottom && a.Bottom + Gap > b.Top;

    /// <summary>
    /// Панель браузера на экране, а уголок на неё заходит (развернулся на наведение, вернулся, его положили рядом) —
    /// уступить. Зовётся каждым тактом наведения браузера, пока его панель видна; уже едет — не трогаем.
    /// </summary>
    internal void KeepClearOfPip(NativeMethods.RECT panel)
    {
        if (!IsMini || !IsShown || _dragging || _dragArmed || Anim.IsRunning(this, "yield") || MiniRect is not { } cur) return;
        if (Overlap(cur, panel)) YieldToPipPanel(panel);
    }

    /// <summary>Плавно сдвинуть уголок: прижатый край — в anchor, верх — в top; home — это возврат (в конце место ухода забывается).</summary>
    private void SlideCorner(int anchor, int top, bool home)
    {
        if (MiniRect is not { } cur) return;
        Anim.Stop(this, "yield");
        void Place(int a, int t)
        {
            if (MiniRect is not { } now) return;
            var r = RectAt(a, t, now); // ширина — какая сейчас (уголок мог как раз сворачиваться)
            MoveCorner(r.Left, r.Top);
        }
        int a0 = AnchorOf(cur), t0 = cur.Top;
        if (a0 == anchor && t0 == top || !Motion.Enabled)
        {
            Place(anchor, top);
            if (home) _sideHome = null;
            return;
        }
        Anim.Run(this, "yield", 110, k => Place((int)Math.Round(a0 + (anchor - a0) * k), (int)Math.Round(t0 + (top - t0) * k)),
            Motion.EaseOut(), () =>
            {
                if (home) _sideHome = null;
            });
    }

    /// <summary>Место, откуда уголок ушёл от панели, забыть (его перетащили сами или толкнул браузер).</summary>
    private void ForgetSideHome()
    {
        _sideHome = null;
        Anim.Stop(this, "yield");
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // полную панель тянут за любое место шапки, кроме кнопок и поиска; уголок — за любую точку (MiniPress)
        if (IsMini) return;
        if (e.OriginalSource is DependencyObject d && (IsInsideButton(d) || d is TextBox || IsInside<TextBox>(d))) return;
        StartWindowDrag(PressPoint(sender, e));
    }

    /// <summary>Где нажали (пиксели экрана) — по самому нажатию, а не где курсор сейчас: интерфейс мог быть занят.</summary>
    private static NativeMethods.POINT? PressPoint(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Visual v || sender is not IInputElement ie || PresentationSource.FromVisual(v) is null) return null;
        var p = v.PointToScreen(e.GetPosition(ie));
        return new NativeMethods.POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) };
    }

    private void MiniPress(object sender, MouseButtonEventArgs e)
    {
        if (!IsMini || _dragging) return;
        // всплывашка у кнопки (яркость SDR) — не уголок: её ползунок двигают, а не тащат уголок (и мышь у неё не отнимаем,
        // иначе она переставала закрываться по щелчку мимо)
        if (e.OriginalSource is DependencyObject d && PresentationSource.FromDependencyObject(d)?.RootVisual is not Window) return;
        // уголок тащится за любую точку, даже за кнопку: курсор отошёл на 4 px с нажатой кнопкой — перетаскивание, а не щелчок
        StartWindowDrag(PressPoint(sender, e), waitMove: true);
    }

    /// <summary>
    /// Тащим окно сами (с частотой монитора, WindowDrag): не за край экрана. Мини-браузер для уголка — как край экрана:
    /// уголок в него упирается и скользит вдоль, а сам браузер не сдвигается (наоборот можно: браузер отталкивает уголок).
    /// Место запоминается только у уголка — полная панель в следующий раз снова откроется по центру экрана.
    /// </summary>
    private bool _dragArmed; // нажали — ждём, щелчок это или перетаскивание

    private void StartWindowDrag(NativeMethods.POINT? from, bool waitMove = false)
    {
        if (_dragging || _dragArmed || _hwnd == IntPtr.Zero) return;
        if (IsMini) ForgetSideHome(); // взялись за уголок сами — он больше никуда не возвращается
        _dragArmed = true;
        var prev = default(NativeMethods.RECT);
        void Begin()
        {
            Anim.Finish(this, "collapse"); // размер уголка на ходу не меняется
            if (waitMove) Mouse.Capture(null); // кнопка под курсором отпускается без щелчка
            NativeMethods.GetWindowRect(_hwnd, out prev);
            _dragging = true;
            // пока окно тащат, кнопки под курсором мышь «не видят»: иначе подсветки наведения перетекали бы туда-сюда каждый кадр
            Body.IsHitTestVisible = false;
            HeaderButtons.IsHitTestVisible = false; // в уголке кнопки — в другом окне
        }
        WindowDrag.Start(_hwnd, r =>
        {
            var wa = WindowDrag.WorkAreaOf(r);
            r = WindowDrag.Clamp(r, wa);
            if (IsMini && App.Instance.Pip?.PanelBounds is { } pb) r = WindowPush.KeepOut(r, prev, pb, Gap, wa); // и в панель браузера не заезжает
            prev = r;
            return r;
        }, () =>
        {
            _dragArmed = false;
            _dragging = false;
            Body.IsHitTestVisible = true;
            HeaderButtons.IsHitTestVisible = true;
            if (!IsMini) return;
            SaveMiniPlace();
            UpdateMiniSide();
            if (Cfg.MiniCollapsed && !MiniMouseOver) _collapseDelay.Start();
        }, from, IsMini ? r => MoveCorner(r.Left, r.Top) : null,
            waitMove ? (int)Math.Round(4 * VisualTreeHelper.GetDpi(this).DpiScaleX) : 0, Begin, () => _dragArmed = false);
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null;
             cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T) return true;
        return false;
    }

    /// <summary>Нажали по кнопке, а не по пустому месту шапки.</summary>
    private static bool IsInsideButton(DependencyObject d)
    {
        for (var cur = d; cur is not null;
             cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    // ---------- шапка ----------
    // подложка шапки (как в разметке): одна замороженная кисть на всё время, а не новая на каждое обновление шапки
    private static readonly Brush HeaderFill = FrozenBrush(0x14), HeaderEdge = FrozenBrush(0x26);

    private static Brush FrozenBrush(byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
        b.Freeze();
        return b;
    }

    /// <summary>Подписи и видимость кнопок шапки (HDR, Zapret, мини-браузер и т.д.).</summary>
    public void UpdateHeader()
    {
        // подписи только у состояний (HDR, Zapret) — остальное значками (подпись во всплывающей подсказке),
        // чтобы плашка в уголке была компактной; «только значки» убирает и эти подписи
        foreach (var b in HeaderButtons.Children.OfType<IconButton>())
            b.Compact = IsMini || (b != HdrButton && b != ZapretButton && b != TgButton); // в уголке — только значки
        // уголок компактнее: квадратные кнопки поменьше, между ними (и после ручки) ровно по 4 px
        foreach (var b in HeaderButtons.Children.OfType<IconButton>())
        {
            b.FixedSize = IsMini ? 28 : 0;
            b.ButtonHeight = IsMini ? 0 : 32; // высота как у поля поиска
        }
        // кромка под шапкой — только у полной панели
        // фон и кромку рисует подложка HeaderBg; сама шапка прозрачная (но с фоном — чтобы за неё можно было тащить)
        HeaderBg.Background = IsMini ? Brushes.Transparent : HeaderFill;
        HeaderBg.BorderBrush = IsMini ? Brushes.Transparent : HeaderEdge;
        // в уголке от края окна до кнопок со всех сторон ровно 6 px (рамка шапки 1 + поле 5; у Body в уголке полей нет)
        HeaderPlate.Padding = IsMini ? new Thickness(5) : new Thickness(7);
        DesktopPage.UpdateTrashVisibility();

        SettingsButton.Visibility = IsMini ? Visibility.Collapsed : Visibility.Visible;
        MinimizeButton.Visibility = IsMini ? Visibility.Collapsed : Visibility.Visible;
        ExpandButton.Visibility = IsMini ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.IsOn = !IsMini && _page is PageSettings or PageZapret;
        TasksButton.IsOn = !IsMini && _page == PageTasks;
        PipButton.Visibility = Cfg.PipEnabled ? Visibility.Visible : Visibility.Collapsed;
        PipButton.IsOn = App.Instance.Pip?.IsOpen == true;
        var iconsVisible = DesktopIcons.AreVisible();
        IconsButton.Text = Loc.T(iconsVisible ? "overlay.hide_icons" : "overlay.show_icons");
        IconsButton.Glyph = iconsVisible ? "\uED1A" : "\uE7B3";
        UpdateHdrButton();
        UpdateZapretButton();
        UpdateTgButton();
        CollapseButton.Visibility = IsMini ? Visibility.Visible : Visibility.Collapsed;
        ButtonsSpace.Visibility = IsMini ? Visibility.Collapsed : Visibility.Visible; // в уголке не нужна (и отставала бы на кадр)
        ArrangeMini();
        if (IsMini)
        {
            UpdateCollapseGlyph();
            ApplyCollapse(_collapseK); // поля и ширины кнопок уголка, размер окна
            return;
        }
        ResetMiniClip();
        foreach (var b in HeaderButtons.Children.OfType<IconButton>())
        {
            b.Margin = new Thickness(5, 0, 0, 0);
            b.Opacity = 1;
            b.IsHitTestVisible = true;
            b.LayoutTransform = Transform.Identity;
            b.RenderTransform = Transform.Identity;
            b.ClearValue(MinWidthProperty);
        }
    }

    /// <summary>Кнопка прокси для Telegram: щелчок — вкл/выкл, правая кнопка — его настройки.</summary>
    public void UpdateTgButton()
    {
        var tg = App.Instance.TgProxy;
        TgButton.Visibility = tg.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!tg.Enabled) return;
        TgButton.Text = Loc.T(tg.Busy is not null ? "tgproxy.header.busy"
                            : tg.Error is not null ? "tgproxy.header.error"
                            : tg.Running ? "tgproxy.header.on" : "tgproxy.header.off");
        TgButton.IsOn = tg.Running && tg.Busy is null;
        TgButton.IsEnabled = tg.Busy is null;
        TgButton.Tip = tg.Error ?? (Tips.Minimal ? $"127.0.0.1:{tg.Port}"
            : Loc.T("tgproxy.header.tip", tg.Port) + "\n" + Loc.T("tgproxy.header.tip_rmb"));
        Tips.SetInfo(TgButton, true);
    }

    private void TgButton_Click(object sender, RoutedEventArgs e)
    {
        var tg = App.Instance.TgProxy;
        if (tg.Error is not null && !IsMini) OpenSettingsSection("telegram");
        tg.Toggle();
    }

    private void TgButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (IsMini) GoFull(PageSettings);
        OpenSettingsSection("telegram");
    }

    /// <summary>Открыть настройки на нужном разделе.</summary>
    public void OpenSettingsSection(string section)
    {
        ShowPage(PageSettings);
        SettingsPage.ShowSection(section);
    }

    /// <summary>Перечитать настройки, если они на экране (состояние прокси поменялось в фоне).</summary>
    public void RefreshSettings()
    {
        if (_page == PageSettings && !IsMini) SettingsPage.RefreshTg();
    }

    public void UpdateHdrButton()
    {
        var (supported, on) = App.Instance.HdrState;
        HdrButton.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        HdrButton.Text = "HDR"; // включён ли — видно по подсветке
        HdrButton.IsOn = on;
        if (!on) SdrPopup.IsOpen = false;
        HdrButton.IsEnabled = !App.Instance.HdrBusy;
    }

    public void UpdateZapretButton()
    {
        var z = App.Instance.Zapret;
        ZapretButton.Visibility = z.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!z.Enabled)
        {
            if (_page == PageZapret) ShowPage(PageSettings);
            return;
        }
        string text;
        if (z.Busy is not null) text = Loc.T(z.Testing ? "zapret.header.testing" : "zapret.header.busy");
        else if (z.Error is not null) text = Loc.T("zapret.header.error");
        else text = Loc.T(z.Running ? "zapret.header.on" : "zapret.header.off");
        ZapretButton.Text = text;
        ZapretButton.IsOn = z.Running && z.Busy is null;
        ZapretButton.IsEnabled = z.Busy is null;
        var strategy = Path.GetFileNameWithoutExtension(z.Strategy);
        ZapretButton.Tip = z.Error ?? (z.Root.Length == 0 ? Loc.T("zapret.not_downloaded")
            : Tips.Minimal ? strategy // минималистичные подсказки — только суть
            : Loc.T("zapret.header.tip", z.Version, strategy) + "\n" + Loc.T("zapret.header.tip_rmb"));
        Tips.SetInfo(ZapretButton, true);
    }

    private void Hdr_Click(object sender, RoutedEventArgs e) => App.Instance.ToggleHdr();

    // ---------- яркость SDR ----------
    private bool _sdrLoading;
    private int _sdrPending = -1;
    private bool _sdrApplying;

    private List<Features.Display.Hdr.Display>? _sdrTargets;

    // В уголке яркость SDR закрывается сама, когда мышь ушла и от неё, и от уголка (по курсору: пока окошко открыто,
    // мышь «принадлежит» ему, и события ухода мыши не приходят). Пока кнопка мыши нажата (тянут ползунок) — не закрываем.
    private readonly DispatcherTimer _sdrWatch = new() { Interval = TimeSpan.FromMilliseconds(25) }; // часто: ловит и короткий щелчок мимо
    private double _sdrAwayMs;
    private bool _sdrDown, _sdrPressInside;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    private const double SdrCloseMs = 500;

    private void WatchSdrPopup()
    {
        if (!SdrPopup.IsOpen || !IsMini || SdrPopup.Child is not FrameworkElement card || !card.IsVisible)
        {
            _sdrWatch.Stop();
            return;
        }
        NativeMethods.GetCursorPos(out var p);
        var m = 10 * VisualTreeHelper.GetDpi(this).DpiScaleX; // зазор между уголком и окошком — не «ушла»
        var a = card.PointToScreen(new Point(0, 0));
        var b = card.PointToScreen(new Point(card.ActualWidth, card.ActualHeight));
        var overCard = p.X >= a.X - m && p.X <= b.X + m && p.Y >= a.Y - m && p.Y <= b.Y + m;
        // щелчок мимо окошка — закрыть (в уголке окошко мышь у себя не держит — иначе кнопки уголка не подсвечивались);
        // нажали в окошке и тянут ползунок — не закрываем, даже если курсор ушёл в сторону
        var down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (down && !_sdrDown) _sdrPressInside = p.X >= a.X && p.X <= b.X && p.Y >= a.Y && p.Y <= b.Y;
        _sdrDown = down;
        if (down)
        {
            if (!_sdrPressInside)
            {
                SdrPopup.IsOpen = false;
                return;
            }
            _sdrAwayMs = 0;
            return;
        }
        var overCorner = MiniRect is { } r && p.X >= r.Left - m && p.X <= r.Right + m && p.Y >= r.Top - m && p.Y <= r.Bottom + m;
        _sdrAwayMs = overCard || overCorner ? 0 : _sdrAwayMs + _sdrWatch.Interval.TotalMilliseconds;
        if (_sdrAwayMs >= SdrCloseMs) SdrPopup.IsOpen = false;
    }

    private void Hdr_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!App.Instance.HdrState.On) return; // HDR выключен — ползунку делать нечего
        _sdrTargets = Features.Display.Hdr.SdrTargets();
        var v = Features.Display.Hdr.SdrBrightness();
        _sdrLoading = true;
        SdrSlider.IsEnabled = v is not null;
        SdrHint.Visibility = v is null ? Visibility.Visible : Visibility.Collapsed;
        SdrSlider.Value = v ?? 0;
        SdrValue.Text = v is null ? "" : $"{v}%";
        _sdrLoading = false;
        ApplySdrLook();
        // в уголке окошко не держит мышь у себя (StaysOpen): иначе, пока оно открыто, кнопки уголка не подсвечивались;
        // закрывается само — щелчком мимо или когда мышь ушла (WatchSdrPopup). В полной панели — как обычное всплывающее
        SdrPopup.StaysOpen = IsMini;
        SdrPopup.IsOpen = true;
    }

    /// <summary>В уголке — фон как у самого уголка (размытие + «Затемнение», та же рамка); в полной панели — как у меню.</summary>
    private void ApplySdrLook()
    {
        if (IsMini)
        {
            SdrCard.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Glass.DarkOpacity(Cfg.Darkness)), 0, 0, 0));
            SdrCard.SetResourceReference(Border.BorderBrushProperty, "WindowFrameBrush"); // рамка — как у самого уголка
        }
        else
        {
            SdrCard.SetResourceReference(Border.BackgroundProperty, "MenuBrush");
            SdrCard.SetResourceReference(Border.BorderBrushProperty, "PopupEdgeBrush");
        }
    }

    /// <summary>Двигают ползунок — применяем в фоне, последнее значение (Windows думает десятки миллисекунд).</summary>
    private void SdrSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sdrLoading || SdrValue is null) return;
        var v = (int)Math.Round(e.NewValue);
        SdrValue.Text = $"{v}%";
        _sdrPending = v;
        if (_sdrApplying) return;
        _sdrApplying = true;
        Task.Run(() =>
        {
            var targets = _sdrTargets ?? Features.Display.Hdr.SdrTargets();
            var last = -1;
            while (true)
            {
                var p = Interlocked.Exchange(ref _sdrPending, -1);
                if (p < 0) break;
                // не чаще ~15 раз в секунду: каждый вызов Windows перерисовывает экран, чаще — лаги
                Features.Display.Hdr.SetSdrBrightness(p, targets, final: false);
                last = p;
                Thread.Sleep(65);
            }
            // ползунок остановился — последнее значение фиксируем
            if (last >= 0) Features.Display.Hdr.SetSdrBrightness(last, targets, final: true);
            _sdrApplying = false;
            if (_sdrPending >= 0) Dispatcher.BeginInvoke(() => SdrSlider_ValueChanged(SdrSlider, new RoutedPropertyChangedEventArgs<double>(SdrSlider.Value, SdrSlider.Value)));
        });
    }

    /// <summary>Щелчок — включить/выключить, правая кнопка — страница Zapret.</summary>
    private void ZapretButton_Click(object sender, RoutedEventArgs e)
    {
        var z = App.Instance.Zapret;
        if (!IsMini && (z.Error is not null || z.Root.Length == 0)) ShowPage(PageZapret); // ошибка — сразу видно какая
        z.Toggle();
    }

    private void ZapretButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (IsMini) GoFull(PageZapret);
        else ShowPage(_page == PageZapret ? PageDesktop : PageZapret);
    }

    private void Tasks_Click(object sender, RoutedEventArgs e)
    {
        if (IsMini) GoFull(PageTasks);
        else ShowPage(_page == PageTasks ? PageDesktop : PageTasks);
    }

    private void Pip_Click(object sender, RoutedEventArgs e)
    {
        var pip = App.Instance.Pip;
        if (pip is null) return;
        if (pip.IsOpen) pip.Hide();
        else pip.Show(); // без режима взаимодействия; панель остаётся как была — не прячется и не уходит в уголок
        UpdateHeader();
    }

    private void IconsButton_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.ToggleDesktopIcons();
        // Проводнику нужно мгновение, чтобы спрятать или показать значки
        Task.Delay(150).ContinueWith(_ => Dispatcher.InvokeAsync(UpdateHeader));
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(_page is PageSettings or PageZapret ? PageDesktop : PageSettings);

    private void Minimize_Click(object sender, RoutedEventArgs e) => GoMini();

    /// <summary>«Упорядочить»: вернуть порядок значков по алфавиту, как в Проводнике.</summary>

    public void SortDesktop() => DesktopPage.AutoSort();
    private void Expand_Click(object sender, RoutedEventArgs e) => GoFull();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideOverlay();

    // ---------- страницы ----------
    private string? _shownPage; // какая страница сейчас на экране (для перехода между вкладками)

    public void ShowPage(string page)
    {
        Core.Logging.PerfLog.Note($"ДЕЙСТВИЕ: страница {page}");
        if (page == PageZapret && !Cfg.ZapretEnabled) page = PageSettings;
        if (page == PageConfirm && _page != PageConfirm) _pageBeforeConfirm = _page;
        _page = page;
        if (IsMini) return; // откроется, когда плашку развернут
        var changed = _shownPage != page;
        _shownPage = page;

        DesktopPage.Visibility = Vis(page == PageDesktop);
        SettingsPage.Visibility = Vis(page == PageSettings);
        ZapretPage.Visibility = Vis(page == PageZapret);
        TasksPage.Visibility = Vis(page == PageTasks);
        TrashPage.Visibility = Vis(page == PageTrash);
        ConfirmPage.Visibility = Vis(page == PageConfirm);
        UpdateSearchBox();
        TitleText.Text = Loc.T(page switch
        {
            PageSettings => "overlay.settings",
            PageZapret => "overlay.zapret",
            PageTasks => "overlay.tasks",
            PageTrash => "overlay.trash",
            PageConfirm => "overlay.confirm",
            _ => "overlay.desktop",
        });
        DesktopPage.SetTrashBack(page != PageDesktop); // на других вкладках вместо корзины — «Назад»
        if (page == PageSettings) SettingsPage.Refresh();
        TasksPage.SetActive(page == PageTasks && IsVisible);
        TrashPage.SetActive(page == PageTrash && IsVisible);
        UpdateHeader();
        if (changed && IsVisible && !_hiding) EnterPage(page);
    }

    /// <summary>
    /// Переход на вкладку: её строки появляются «лесенкой» сверху вниз — тем же ходом, что плитки рабочего стола
    /// (рабочий стол — целиком, картинкой: значков много).
    /// </summary>
    private void EnterPage(string page)
    {
        switch (page)
        {
            case PageDesktop: Fx.FadeIn(DesktopPage); break;
            case PageSettings: SettingsPage.Enter(); break;
            case PageZapret: ZapretPage.Enter(); break;
            case PageTasks: TasksPage.Enter(); break;
            case PageTrash: TrashPage.Enter(); break;
            case PageConfirm: ConfirmPage.Enter(); break;
        }
    }

    private static Visibility Vis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>«Назад» (плашка слева от шапки или Esc): из подтверждения — туда, откуда пришли, из Zapret — в настройки, иначе — на рабочий стол.</summary>
    private void GoBack()
    {
        if (_page == PageConfirm) ShowPage(_pageBeforeConfirm);
        else if (_page == PageZapret) ShowPage(PageSettings);
        else ShowPage(PageDesktop);
    }

    /// <summary>Спросить «точно?» на отдельной странице. Отмена — назад, откуда пришли.</summary>
    public void Confirm(string title, string text, string okText, Action<bool> onOk,
        (string Title, string Sub, bool Default)? option = null)
    {
        if (IsMini) SetMini(false);
        if (!IsShown) ShowOverlay(PageSettings);
        ConfirmPage.Setup(title, text, okText, onOk, () => ShowPage(_pageBeforeConfirm), option);
        ShowPage(PageConfirm);
    }

    // ---------- клавиатура ----------
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift) && _page == PageDesktop
            && Keyboard.FocusedElement is not TextBox { Name: not "SearchBox" })
        {
            if (SearchBox.Text.Length == 0) { DesktopPage.Undo(redo: (Keyboard.Modifiers & ModifierKeys.Shift) != 0); e.Handled = true; } // и когда отменять нечего: иначе Ctrl+Z превращался в символ и открывал поиск
            return;
        }
        // Esc в поле записи сочетания — это отмена записи, её обрабатывает само поле
        if (e.Key != Key.Escape || Keyboard.FocusedElement is HotkeyBox) return;
        e.Handled = true;
        if (IsMini) return;
        if (DesktopPage.CancelDrag()) { }
        else if (_page != PageDesktop) GoBack();
        else if (DesktopPage.CloseGroup()) { }
        else if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        else if (_searchOpen) SetSearchOpen(false);
        else HideOverlay();
    }

    /// <summary>
    /// Щелчок в любом месте мимо поиска: поле теряет фокус клавиатуры (подсветка гаснет), пустое — сворачивается в лупу.
    /// С текстом остаётся развёрнутым: иначе найденное было бы отфильтровано невидимым запросом.
    /// (Набор букв на рабочем столе поиск больше не открывает — только щелчок по лупе.)
    /// </summary>
    private void OnPreviewMouseDownAnywhere(object sender, MouseButtonEventArgs e)
    {
        if (!_searchOpen || e.OriginalSource is not DependencyObject d || IsInsideElement(d, SearchHost)) return;
        if (SearchBox.IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, null);
        }
        if (SearchBox.Text.Length == 0) SetSearchOpen(false);
    }

    private static bool IsInsideElement(DependencyObject d, DependencyObject host)
    {
        for (var cur = d; cur is not null;
             cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur == host) return true;
        return false;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DesktopPage.LaunchFirst();
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            DesktopPage.FocusFirst();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        DesktopPage.SetFilter(SearchBox.Text.Trim());
    }

    // ---------- рабочий стол ----------
    public void ReloadDesktop(bool animate = false, bool ifChanged = false) => DesktopPage.Reload(animate, ifChanged);

    public void ResizeDesktopIcons() => DesktopPage.ResizeIcons();
}
