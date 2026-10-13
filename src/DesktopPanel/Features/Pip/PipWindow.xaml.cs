using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using DesktopPanel.Core;
using DesktopPanel.Core.Config;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;
using DesktopPanel.Features.Games;

namespace DesktopPanel.Features.Pip;

/// <summary>
/// Мини-браузер «картинка в картинке»: окно поверх всех, YouTube и любые сайты. Окно — только страница
/// с фиксированным соотношением сторон; кнопки — в боковой панели (PipSidebar) рядом с ним, она появляется,
/// пока мышь над браузером. Обычно окно не мешает: не забирает фокус, клики проходят сквозь него.
/// Режим взаимодействия — окно принимает мышь и клавиатуру и тянется за края; выход возвращает фокус туда,
/// где он был (например, в игру).
/// Два окна. Картинка — окно PipSurface ровно над этим: страница (PipWeb) и рамка — слоями механизма композиции Windows,
/// их собирает сама Windows (цвета при HDR и без него — как у Edge), прозрачность — по-настоящему; это окно всегда
/// пропускает мышь и никогда не меняет стиль — картинка одна и та же в любом режиме. Это окно — мышь и клавиатура:
/// прозрачное (AllowsTransparency, фон #01 — почти невидимый, но ловит мышь), сквозной клик — WS_EX_TRANSPARENT у него.
/// Не WebView2CompositionControl — тот снимал страницу в 8-битную картинку (при HDR видео пересвечено, после выключения HDR
/// ломались цвета); и не окно-страница WebView2 — у такого не работала прозрачность. Мышь странице передаётся только
/// в режиме взаимодействия.
/// </summary>
public partial class PipWindow : Window
{
    private readonly AppConfig _cfg;
    // не readonly: после сбоя движка пересоздаём, см. RecreateWebAsync
    private PipWeb Web;
    private readonly PipSurface _surface; // окно-картинка над этим: страница и рамка
    private CoreWebView2Environment? _env;
    private bool _recreating;
    private readonly PipSidebar _sidebar = new();
    private readonly PipSearchBox _search = new();
    private IntPtr _hwnd;
    private bool _ready;
    private bool _interact;
    private IntPtr _returnFocusTo;
    private readonly DispatcherTimer _saveBounds = new() { Interval = TimeSpan.FromMilliseconds(600) };
    // боковая панель: видна, пока мышь над браузером или над ней (и полсекунды после)
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private DateTime _lastOver;
    private bool _menuOpen, _dragging;
    private bool _sidebarRight = true; // панель справа от браузера

    /// <summary>Масштаб страницы: ширина окна делится на эту «ширину сайта». Обычный — крупнее, «всё» — мелко.</summary>
    private const double NormalSiteWidth = 720, FitSiteWidth = 1280;

    public PipWindow(AppConfig cfg)
    {
        _cfg = cfg;
        InitializeComponent();
        _surface = new PipSurface(this);
        Web = new PipWeb(this, WebHost, _surface);
        // панель всегда поверх мини-браузера: страница, боковая панель и поле адреса встают под неё (перехват — раньше,
        // чем окно впервые займёт место: иначе первое «поверх всех» проходило мимо и свежий браузер ложился на панель)
        DesktopPanel.UI.Theme.BelowOverlay.Attach(this);
        DesktopPanel.UI.Theme.BelowOverlay.Attach(_sidebar);
        DesktopPanel.UI.Theme.BelowOverlay.Attach(_search);
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x10, 0x10, 0x14);
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // перетаскивание и растягивание: соотношение сторон, край экрана, «магнит» к краям и углам
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            RestorePlacement();
            ApplyStyles();
            // окно-картинка (страница и рамка) — сразу, ещё скрытое: покажется вместе с этим окном
            try { _surface.Ensure(); }
            catch (Exception ex) { Log.Error("pip: окно картинки не создано", ex); }
        };
        // вне режима взаимодействия страница мышь не получает — щелчки остаются окну: перетащить, тройной щелчок
        // (у самого окна, а не у Root: почти невидимый фон, который ловит мышь, — у окна)
        PreviewMouseLeftButtonDown += Page_MouseDown;
        Root.SizeChanged += (_, _) => RoundCorners();
        LocationChanged += (_, _) =>
        {
            QueueSaveBounds();
            if (_sidebar.IsVisible) PositionSidebar();
        };
        SizeChanged += (_, _) =>
        {
            QueueSaveBounds();
            ApplyZoom();
            if (_sidebar.IsVisible) PositionSidebar();
            if (IsVisible) App.Instance.Overlay?.AvoidPip();
        };
        _saveBounds.Tick += (_, _) =>
        {
            _saveBounds.Stop();
            SaveBounds();
        };
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        };
        DpiChanged += (_, _) =>
        {
            RoundCorners();
            _surface.Relayout();
        };
        IsVisibleChanged += (_, _) =>
        {
            Web.HostVisible = IsVisible; // скрыли — подсказки страницы и невидимое окно движка тоже прячутся
            if (IsVisible)
            {
                _hover.Start();
                UpdateOutsidePoll();
                Web.TameEngineWindows();
            }
            else
            {
                _hover.Stop();
                _sidebar.Hide();
                _search.Hide();
                App.Instance.Overlay?.YieldToPipPanel(null); // браузер скрыли — уголок, если уступал место панели, обратно
            }
        };
        DesktopPanel.UI.Theme.Accent.Changed += ApplyFrame; // событие общее и живёт дольше окна — отписка в Closed
        Closed += (_, _) =>
        {
            DesktopPanel.UI.Theme.Accent.Changed -= ApplyFrame;
            if (_saveBounds.IsEnabled) { _saveBounds.Stop(); SaveBounds(); }
            try { Web.Dispose(); } catch { }
            _surface.Dispose();
            _hover.Stop();
            _sidebar.Close();
            _search.Close();
        };
        _hover.Tick += (_, _) => UpdateHover();
        // «Авто взаимодействие»: щёлкнули куда-то кроме браузера — режим выключается (фокус там, куда щёлкнули)
        Deactivated += (_, _) => CheckLeftInteract();
        // клик по боковой панели переводит активность на неё; потом клик мимо деактивирует уже её, а не окно браузера
        _sidebar.Deactivated += (_, _) => CheckLeftInteract();
        WireSidebar();
        _search.Submitted += text =>
        {
            Navigate(text);
            if (_interact) TakeForeground();
        };
        // Сначала окно, потом движок: браузер подключаем, когда окно уже отрисовано (раньше подключение и запись файлов
        // шли до показа — первое открытие замораживало программу на ~0,4 с). Пока движок грузится, окно просто тёмное.
        ContentRendered += StartEngine;
    }

    private void StartEngine(object? sender, EventArgs e)
    {
        ContentRendered -= StartEngine;
        EngineStarted = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = InitWebAsync());
    }

    public bool Interacting => _interact;

    /// <summary>Окно уже показывали и движок запущен (заранее созданное, но не показанное окно памяти почти не держит).</summary>
    public bool EngineStarted { get; private set; }

    public IntPtr Handle => _hwnd;

    /// <summary>На странице сейчас играет звук (тогда скрытый браузер не закрываем).</summary>
    public bool PlayingAudio
    {
        get { try { return _ready && Web.CoreWebView2.IsDocumentPlayingAudio; } catch { return false; } }
    }

    // ---------- WebView2 ----------
    private async Task InitWebAsync()
    {
        try
        {
            var dataDir = Path.Combine(Paths.LocalData, "WebView2");
            await Task.Run(() =>
            {
                SecureDns.Enable(dataDir); // файлы — в фоне, поток интерфейса их не ждёт
                StartPage.Write(_cfg);
            });
            // цвета страниц — как в обычном (не HDR) режиме: при HDR Windows сама показывает их с яркостью SDR из настроек.
            // Видео — в общей картинке страницы, а не отдельным слоем Windows (video overlay): тот после выключения HDR
            // однажды показал видео с неверными цветами (розовые лица)
            var options = new CoreWebView2EnvironmentOptions("--force-color-profile=srgb --disable-direct-composition-video-overlays");
            _env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
            await AttachAsync(StartPage.Url); // всегда начинаем со стартовой страницы: поиск и избранное
        }
        catch (Exception ex)
        {
            Log.Error("pip: WebView2 не запустился", ex);
            Message.Text = Loc.T("pip.no_runtime");
            Message.Visibility = Visibility.Visible;
            Web.Visible = false;
        }
    }

    /// <summary>Пересоздать браузер и открыть ту же страницу (после сбоя движка).</summary>
    private async Task RecreateWebAsync()
    {
        if (_recreating || _env is null) return;
        _recreating = true;
        try
        {
            var url = _ready && !string.IsNullOrEmpty(Web.CoreWebView2?.Source) ? Web.CoreWebView2.Source : StartPage.Url;
            _ready = false;
            var old = Web;
            try { old.Dispose(); } catch { }
            WebHost.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14)); // пока новая не загрузилась
            Web = new PipWeb(this, WebHost, _surface)
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x10, 0x10, 0x14),
                HostVisible = IsVisible,
            };
            await AttachAsync(url);
            if (_interact) Web.Focus();
        }
        catch (Exception ex) { Log.Error("pip: не пересоздать браузер", ex); }
        finally { _recreating = false; }
    }

    /// <summary>Подключить движок к элементу Web, настроить и открыть адрес.</summary>
    private async Task AttachAsync(string url)
    {
        {
            await Web.EnsureCoreWebView2Async(_env);
            var core = Web.CoreWebView2!;
            Log.Info($"pip: браузер подключён слоем (WebView2 {_env!.BrowserVersionString})");
            WebHost.Background = Brushes.Transparent; // дальше на этом месте — страница (в окне-картинке над этим)
            ApplyOpacity();
            // процесс браузера/видео/страницы упал — пересоздаём (иначе чёрный экран и тишина)
            core.ProcessFailed += (_, e) =>
            {
                Log.Warn($"pip: процесс браузера упал ({e.ProcessFailedKind}, {e.Reason})");
                if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
                    or CoreWebView2ProcessFailedKind.GpuProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited
                    or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                    Dispatcher.InvokeAsync(RecreateWebAsync);
            };
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true; // Ctrl+колесо, Ctrl+плюс/минус — как в обычном браузере
            core.SetVirtualHostNameToFolderMapping(StartPage.Host, StartPage.Folder, CoreWebView2HostResourceAccessKind.Allow);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(KeepCursorScript);
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true; // ссылки «в новом окне» открываем здесь же
                core.Navigate(e.Uri);
            };
            core.SourceChanged += (_, _) =>
            {
                if (!StartPage.IsStart(core.Source)) _cfg.PipUrl = core.Source;
                _saveBounds.Stop();
                _saveBounds.Start(); // настройки сохранятся вместе с положением
                UpdateSidebar();
            };
            core.HistoryChanged += (_, _) => UpdateSidebar(); // назад/вперёд стали (не)доступны
            core.NavigationStarting += (_, _) => ErrorBar.Visibility = Visibility.Collapsed;
            core.NavigationCompleted += (_, e) =>
            {
                Web.TameEngineWindows(); // невидимое окно движка могло появиться только сейчас
                if (e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                Log.Warn($"pip: страница не открылась: {e.WebErrorStatus} ({core.Source})");
                ShowError(e.WebErrorStatus);
            };
            _ready = true;
            ApplyWebInput();
            ApplyZoom();
            Navigate(url);
            UpdateSidebar();
        }
    }

    /// <summary>
    /// Курсор не пропадает над окном: плееры (YouTube, Twitch…) прячут его над видео (cursor: none). На каждое
    /// движение мыши смотрим, что под ней, и если курсор спрятан — возвращаем обычный (свои курсоры ссылок не трогаем).
    /// </summary>
    private const string KeepCursorScript = """
        (() => {
          let x = -1, y = -1;
          const fixFrom = start => {
            for (let el = start; el && el.nodeType === 1; el = el.parentElement) {
              if (getComputedStyle(el).cursor === 'none') el.style.setProperty('cursor', 'default', 'important');
            }
          };
          addEventListener('mousemove', e => { x = e.clientX; y = e.clientY; fixFrom(e.target); }, true);
          addEventListener('mouseover', e => fixFrom(e.target), true);
          // плеер прячет курсор, когда мышь как раз НЕ двигается, — проверяем и без событий
          setInterval(() => { if (x >= 0) fixFrom(document.elementFromPoint(x, y)); }, 500);
        })();
        """;

    /// <summary>
    /// Сайт не открылся. Чаще всего — блокировка (YouTube в России без обхода не открывается):
    /// тогда подсказываем включить Zapret.
    /// </summary>
    private void ShowError(CoreWebView2WebErrorStatus status)
    {
        var host = Uri.TryCreate(CurrentUrl, UriKind.Absolute, out var u) ? u.Host : CurrentUrl;
        var blocked = status is CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset
            or CoreWebView2WebErrorStatus.Timeout or CoreWebView2WebErrorStatus.Disconnected
            or CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ServerUnreachable
            or CoreWebView2WebErrorStatus.HostNameNotResolved;
        var z = App.Instance.Zapret;
        var offerZapret = blocked && !z.Running;
        ErrorText.Text = offerZapret ? Loc.T(z.Enabled ? "pip.error.blocked" : "pip.error.blocked_no_zapret", host)
                                     : Loc.T("pip.error.other", host, status);
        ZapretButton.Visibility = offerZapret && z.Enabled ? Visibility.Visible : Visibility.Collapsed;
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void Zapret_Click(object sender, RoutedEventArgs e)
    {
        var z = App.Instance.Zapret;
        if (!z.Running) z.Toggle();
        ErrorText.Text = Loc.T("pip.error.zapret_starting");
        ZapretButton.Visibility = Visibility.Collapsed;
        // как только Zapret заработает — перезагрузить страницу
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var tries = 0;
        t.Tick += (_, _) =>
        {
            if (z.Running && z.Busy is null)
            {
                t.Stop();
                if (_ready) Web.CoreWebView2.Reload();
            }
            else if (++tries > 30) t.Stop();
        };
        t.Start();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_ready) Web.CoreWebView2.Reload();
    }

    public void Navigate(string text)
    {
        if (!_ready || string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        string url;
        if (Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) url = u.ToString();
        else if (!text.Contains(' ') && text.Contains('.')) url = "https://" + text;
        else url = "https://www.google.com/search?q=" + Uri.EscapeDataString(text); // не адрес — обычный поиск
        try { Web.CoreWebView2.Navigate(url); }
        catch (Exception ex) { Log.Warn($"pip: не открыть {url}: {ex.Message}"); }
    }

    /// <summary>Выполнить действие с видео на странице (самое большое &lt;video&gt;).</summary>
    public void Player(string action)
    {
        if (!_ready) return;
        var js = action switch
        {
            "playpause" => "v.paused ? v.play() : v.pause()",
            "pause" => "v.pause()",
            "back" => "v.currentTime = Math.max(0, v.currentTime - 10)",
            "forward" => "v.currentTime = v.currentTime + 10",
            "volup" => "v.muted = false; v.volume = Math.min(1, v.volume + 0.1)",
            "voldown" => "v.volume = Math.max(0, v.volume - 0.1)",
            "mute" => "v.muted = !v.muted",
            _ => "",
        };
        if (js.Length == 0) return;
        const string find = "const v = [...document.querySelectorAll('video')]" +
                            ".sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight)[0]; if (!v) return;";
        _ = Web.CoreWebView2.ExecuteScriptAsync($"(() => {{ {find} {js}; }})()");
    }

    /// <summary>
    /// Масштаб страницы под размер окна: сайт «думает», что окно шириной 720 (обычный режим) или 1280 точек
    /// («показать всё» — мелко, зато видно всю страницу). Ctrl+колесо потом можно подправить вручную.
    /// </summary>
    private void ApplyZoom()
    {
        if (!_ready || ActualWidth <= 0) return;
        var site = _cfg.PipZoomFit ? FitSiteWidth : NormalSiteWidth;
        Web.ZoomFactor = Math.Clamp(ActualWidth / site, 0.25, 1.0);
    }

    // ---------- боковая панель ----------
    private void WireSidebar()
    {
        // «Авто взаимодействие»: нажали кнопку панели (кроме закрыть / в избранное / прозрачность) — режим включается
        Action Auto(Action a) => () =>
        {
            if (_cfg.PipAutoInteract && !_interact) SetInteract(true);
            a();
        };
        _sidebar.InteractClicked += NextMode;
        _sidebar.SearchClicked += Auto(OpenSearch);
        _sidebar.BackClicked += Auto(() =>
        {
            if (_ready && Web.CoreWebView2.CanGoBack) Web.CoreWebView2.GoBack();
        });
        _sidebar.ForwardClicked += Auto(() =>
        {
            if (_ready && Web.CoreWebView2.CanGoForward) Web.CoreWebView2.GoForward();
        });
        _sidebar.ReloadClicked += Auto(() => Reload_Click(this, new RoutedEventArgs()));
        _sidebar.HomeClicked += Auto(() => Navigate(StartPage.Url));
        _sidebar.FavoritesClicked += Auto(ShowFavorites);
        _sidebar.StarClicked += ToggleFavorite;
        _sidebar.ZoomClicked += Auto(() =>
        {
            _cfg.PipZoomFit = !_cfg.PipZoomFit;
            App.Instance.SaveConfig();
            ApplyZoom();
            UpdateSidebar();
        });
        _sidebar.OpacityChanged += v =>
        {
            _cfg.PipOpacity = v;
            ApplyOpacity(); // только прозрачность: полный ApplyStyles пересоздавал рамку окна на каждом шаге — удержание шло рывками
            _saveBounds.Stop();
            _saveBounds.Start(); // сохранится вместе с положением
        };
        _sidebar.CloseClicked += () => App.Instance.Pip?.Hide();
        UpdateSidebar();
    }

    private void UpdateSidebar()
    {
        // три режима: обычный (тащить за страницу; значок цветом акцента) — взаимодействие (подсветка кнопки) — сквозной клик
        var b = _sidebar.InteractButton;
        b.IsOn = _interact;
        b.Glyph = _interact ? "\uF271" : _cfg.PipClickThrough ? "\uE81E" : "\uE7C2";
        b.Text = Loc.T(_interact ? "pip.mode.interact" : _cfg.PipClickThrough ? "pip.mode.through" : "pip.mode.normal");
        if (!_interact && !_cfg.PipClickThrough) b.SetResourceReference(ForegroundProperty, "AccentBrush");
        else b.ClearValue(ForegroundProperty);
        var fav = CurrentFavorite() is not null;
        _sidebar.StarButton.Glyph = fav ? "" : "";
        _sidebar.StarButton.Text = Loc.T(fav ? "pip.remove_favorite" : "pip.add_favorite");
        // стрелки под 45°: наружу — крупнее, внутрь — показать всю страницу мелко
        _sidebar.ZoomButton.Glyph = _cfg.PipZoomFit ? "" : "";
        _sidebar.ZoomButton.Text = Loc.T(_cfg.PipZoomFit ? "pip.zoom_in" : "pip.zoom_fit");
        _sidebar.BackButton.IsEnabled = _ready && Web.CoreWebView2.CanGoBack;
        _sidebar.ForwardButton.IsEnabled = _ready && Web.CoreWebView2.CanGoForward; // некуда — кнопка неактивна
    }

    /// <summary>
    /// Панель видна, только пока курсор прямо над браузером или над самой панелью (или в зазоре между ними — по пути
    /// с одного на другую). Ушёл — прячется сразу (с запасом в долю секунды на дрожь руки); над панелью-оверлеем или уголком,
    /// даже если они лежат поверх браузера или его панели, — тоже прячется.
    /// </summary>
    private void UpdateHover()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible || _dragging) return;
        NativeMethods.GetCursorPos(out var pt);
        static bool Inside(NativeMethods.RECT r, NativeMethods.POINT p) =>
            p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        var sideOn = _sidebar.IsVisible && _sidebar.Handle != IntPtr.Zero;
        var hasR = NativeMethods.GetWindowRect(_hwnd, out var r);
        NativeMethods.RECT sr = default;
        var hasSr = sideOn && NativeMethods.GetWindowRect(_sidebar.Handle, out sr);
        // под курсором большая панель или уголок (они поверх браузера) — это не «над браузером». Окно под курсором
        // спрашиваем, только пока панель кнопок на экране или курсор над браузером: иначе от ответа ничего не зависит
        // (таймер тикает 20 раз в секунду всё время, пока браузер на экране)
        var ov = App.Instance.Overlay;
        var overOverlay = ov is { IsShown: true } && ov.Handle != IntPtr.Zero
                          && (sideOn || (hasR && Inside(r, pt)))
                          && GetAncestor(WindowFromPoint(pt), GA_ROOTOWNER) == ov.Handle; // и окно уголка (его владелец — панель)
        // по прямоугольникам, а не по окну под курсором: вне режима взаимодействия со сквозным кликом окно браузера
        // для мыши «прозрачно», и Windows под курсором его не находила — панель не открывалась
        var inPage = hasR && Inside(r, pt) && !overOverlay;
        var over = inPage;
        if (!over && !overOverlay && hasSr)
        {
            over = Inside(sr, pt);
            // зазор между браузером и панелью (6 px) — по пути с одного на другую
            int gl = Math.Min(r.Right, sr.Right), gr = Math.Max(r.Left, sr.Left);
            int gt = Math.Max(r.Top, sr.Top), gb = Math.Min(r.Bottom, sr.Bottom);
            over = over || (gl < gr && gt < gb && pt.X >= gl && pt.X < gr && pt.Y >= gt && pt.Y < gb);
        }
        UpdateClickPoll(inPage);
        var busy = _menuOpen || _search.IsVisible || _dragging || _sidebar.PopupOpen;
        if (over || busy) _lastOver = DateTime.Now;
        // запас на дрожь руки только держит уже открытую панель, но не открывает её снова (спрятали над уголком —
        // через тик курсор ушёл с уголка, а 120 мс ещё не прошло: панель мигала)
        var show = over || busy || (_sidebar.IsVisible && !overOverlay && DateTime.Now - _lastOver < TimeSpan.FromMilliseconds(120));
        if (show && !_sidebar.IsVisible)
        {
            UpdateSidebar();
            _sidebar.SetOpacity(_cfg.PipOpacity);
            _sidebar.Show();
            _sidebar.UpdateLayout();
            PositionSidebar();
            DesktopPanel.UI.Theme.BelowOverlay.Apply(_sidebar.Handle);
            _sidebar.Refresh();
            _sidebar.Appear(_sidebarRight); // выезжает сбоку — со стороны браузера
        }
        else if (!show && _sidebar.IsVisible)
        {
            _sidebar.Hide();
            App.Instance.Overlay?.YieldToPipPanel(null); // уголок, если уступал место панели, — обратно
        }
        else if (show && hasR && hasSr)
            // панель на экране — уголок не должен на неё заходить никогда (развернулся, вернулся, его положили рядом):
            // проверка дешёвая, раз в такт наведения; уступать — тем же ходом, что при появлении панели
            App.Instance.Overlay?.KeepClearOfPip(DesktopPanel.UI.Theme.WindowPush.Union(r, sr));
    }

    /// <summary>Браузер вместе с боковой панелью, если она на экране (пиксели экрана).</summary>
    internal NativeMethods.RECT? PanelRect
    {
        get
        {
            if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var r)) return null;
            if (_sidebar.IsVisible && _sidebar.Handle != IntPtr.Zero && NativeMethods.GetWindowRect(_sidebar.Handle, out var s))
                r = DesktopPanel.UI.Theme.WindowPush.Union(r, s);
            return r;
        }
    }

    /// <summary>Панель — сбоку от браузера, с той стороны, где до края экрана дальше (страницу не закрывает).</summary>
    private void PositionSidebar()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var r)) return;
        var wa = WorkAreaOf(r);
        // один и тот же отступ (6 px) — от браузера, от верха и низа экрана; от размера браузера не зависит
        var gap = (int)Math.Round(6 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        _sidebar.FitHeight(Math.Min(r.Height, wa.Height - 2 * gap));
        var (sw, sh) = _sidebar.PixelSize;
        // со стороны, где до края экрана дальше, почти вплотную; места нет — поверх края браузера
        var right = wa.Right - r.Right >= r.Left - wa.Left;
        _sidebarRight = right;
        var x = right ? r.Right + gap : r.Left - gap - sw;
        if (x < wa.Left || x + sw > wa.Right) x = right ? r.Right - sw : r.Left;
        var y = Math.Max(wa.Top + gap, Math.Min(r.Top, wa.Bottom - gap - sh));
        _sidebar.MoveTo(x, y);
        // уголок не под панелью: уходит вверх или вниз от браузера вместе с ней
        if (!_dragging)
            App.Instance.Overlay?.YieldToPipPanel(DesktopPanel.UI.Theme.WindowPush.Union(r,
                new NativeMethods.RECT { Left = x, Top = y, Right = x + sw, Bottom = y + sh }));
    }

    /// <summary>Поле поиска — над или под браузером: с той стороны, где до края экрана дальше.</summary>
    private void OpenSearch()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var r)) return;
        var wa = WorkAreaOf(r);
        var h = _search.PixelHeight;
        var gap = (int)(6 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var below = wa.Bottom - r.Bottom >= r.Top - wa.Top;
        var y = below ? r.Bottom + gap : r.Top - gap - h;
        if (y < wa.Top || y + h > wa.Bottom) y = below ? r.Bottom - h : r.Top; // места нет — по краю браузера
        var url = CurrentUrl;
        _search.Open(r.Left, y, r.Width, StartPage.IsStart(url) ? "" : url);
        DesktopPanel.UI.Theme.BelowOverlay.Apply(new WindowInteropHelper(_search).Handle);
    }

    /// <summary>
    /// Тянут за ручку или за страницу — двигаем окно сами, по таймеру, пока нажата левая кнопка. Системное перетаскивание
    /// (HTCAPTION) включало бы Aero Snap: у верхнего края Windows предлагала развернуть окно на весь экран.
    /// </summary>
    private void StartMove(NativeMethods.POINT? from = null)
    {
        if (_hwnd == IntPtr.Zero || _dragTimer) return;
        _dragTimer = true;
        // ровно 60 кадров/с (WindowDrag), не за край экрана; уголок на ходу отодвигается (WindowPush)
        var push = new DesktopPanel.UI.Theme.WindowPush();
        var gap = (int)Math.Round(6 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        DesktopPanel.UI.Theme.WindowDrag.Start(_hwnd, r =>
        {
            r = Clamp(r, WorkAreaOf(r));
            if (App.Instance.Overlay is { } ov && ov.MiniRect is { } mini && push.Resolve(r, mini, gap, verticalOnly: true) is { } nb)
                ov.MoveMini(nb.Left, nb.Top);
            return r;
        }, () =>
        {
            _dragTimer = false;
            _dragging = false;
            Web.Moving = false;
            _lastOver = DateTime.Now;
            App.Instance.Overlay?.SaveMiniPlace();
        }, from, threshold: (int)Math.Round(4 * VisualTreeHelper.GetDpi(this).DpiScaleX), begin: () =>
        {
            // мышь сдвинулась с нажатой кнопкой — это перетаскивание (а не щелчок)
            _dragging = true;
            Web.Moving = true;
            _sidebar.Hide(); // размытие под панелью на ходу тормозит — вернётся, когда отпустят
            App.Instance.Overlay?.YieldToPipPanel(null); // панели нет — уголок обратно (дальше его, если надо, толкнёт сам браузер)
        }, click: () => _dragTimer = false);
    }

    // ---------- тройной щелчок и сквозной клик ----------
    // Тройной щелчок по странице включает и выключает режим взаимодействия (каждое — тумблером в настройках).
    // Щелчки ловим опросом кнопки мыши, пока курсор над страницей: так это работает в любом режиме — и когда щелчки
    // получает само окно, и когда страница (в режиме взаимодействия), и при сквозном клике (окно щелчков не получает вовсе).
    // Два нажатия подряд — в пределах времени двойного щелчка Windows и рядом друг с другом.
    private bool ClickThrough => _cfg.PipClickThrough && !_interact;
    private System.Windows.Threading.DispatcherTimer? _clickPoll;
    private bool _wasDown;
    private long _lastDown;
    private int _clicks;
    private NativeMethods.POINT _lastDownAt;

    private bool TripleWanted => _cfg.PipDoubleClick;

    private void UpdateClickPoll(bool overPage)
    {
        var need = TripleWanted && overPage && IsVisible;
        if (!need)
        {
            _clickPoll?.Stop();
            return;
        }
        if (_clickPoll is null)
        {
            _clickPoll = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal)
                { Interval = TimeSpan.FromMilliseconds(10) };
            _clickPoll.Tick += (_, _) => PollClick();
        }
        if (!_clickPoll.IsEnabled)
        {
            _wasDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0; // нажатие, начатое не над окном, не считаем
            _clickPoll.Start();
        }
    }

    private void PollClick()
    {
        if (!TripleWanted || !IsVisible)
        {
            _clickPoll?.Stop();
            return;
        }
        var down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (down && !_wasDown)
        {
            NativeMethods.GetCursorPos(out var p);
            var now = Environment.TickCount64;
            var near = Math.Abs(p.X - _lastDownAt.X) <= 6 && Math.Abs(p.Y - _lastDownAt.Y) <= 6;
            _clicks = now - _lastDown <= GetDoubleClickTime() && near ? _clicks + 1 : 1;
            _lastDown = now;
            _lastDownAt = p;
            if (_clicks >= 2)
            {
                _clicks = 0;
                _lastDown = 0;
                NextMode();
            }
        }
        _wasDown = down;
    }

    private const int VK_LBUTTON = 0x01, GA_ROOT = 2, GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);

    /// <summary>Сдвинуть окно (его расталкивает уголок).</summary>
    public void MoveTo(int x, int y)
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private bool _dragTimer;

    /// <summary>
    /// Вне режима взаимодействия (сквозной клик выключен) — за страницу окно перетаскивается (когда мышь сдвинулась с
    /// нажатой кнопкой). Странице мышь в это время не передаётся (ApplyWebInput).
    /// Тройной щелчок — см. PollClick.
    /// </summary>
    private void Page_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_interact || (e.OriginalSource is DependencyObject d && IsIn(d, ErrorBar))) return; // кнопки полосы ошибки работают
        e.Handled = true;
        NativeMethods.GetCursorPos(out var p);
        StartMove(p);
    }

    private static bool IsIn(DependencyObject d, DependencyObject host)
    {
        for (var cur = d; cur is not null; cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur == host) return true;
        return false;
    }

    /// <summary>Мышь — странице только в режиме взаимодействия (иначе щелчки остаются окну).</summary>
    private void ApplyWebInput() => Web.Interactive = _interact;

    /// <summary>То, что рисует WPF (полоса ошибки, тёмный фон до загрузки), — с тем же скруглением, что у страницы.</summary>
    private void RoundCorners()
    {
        var r = DesktopPanel.UI.Theme.Corners.WindowDip;
        Root.Clip = new RectangleGeometry(new Rect(Root.RenderSize), r, r);
    }

    // ---------- режимы окна ----------
    /// <summary>
    /// Кнопка режима на боковой панели — по кругу: обычный → взаимодействие → сквозной клик → обычный.
    /// Сквозной клик запоминается: выход из взаимодействия (щелчок мимо) возвращает в тот режим,
    /// что был до него.
    /// </summary>
    // по кругу: сквозной клик → взаимодействие → перемещение → сквозной клик
    private void NextMode() => SetMode(Mode switch
    {
        PipMode.Through => PipMode.Interact,
        PipMode.Interact => PipMode.Normal,
        _ => PipMode.Through,
    });

    private enum PipMode { Normal, Interact, Through }

    private PipMode Mode => _interact ? PipMode.Interact : _cfg.PipClickThrough ? PipMode.Through : PipMode.Normal;

    /// <summary>Обычный (тащить за страницу), взаимодействие, сквозной клик.</summary>
    private void SetMode(PipMode m, bool returnFocus = true)
    {
        if (m == PipMode.Interact)
        {
            SetClickThrough(false);
            SetInteract(true);
        }
        else
        {
            SetClickThrough(m == PipMode.Through);
            SetInteract(false, returnFocus);
        }
        UpdateOutsidePoll();
        UpdateSidebar();
    }

    /// <summary>Сочетание клавиш «режим окна» — по кругу, как кнопка на панели и двойной щелчок.</summary>
    public void CycleMode() => NextMode();

    private void SetClickThrough(bool on)
    {
        if (_cfg.PipClickThrough == on) return;
        _cfg.PipClickThrough = on;
        App.Instance.SaveConfig();
        ApplyStyles();
        Log.Info($"pip: сквозной клик {(on ? "вкл" : "выкл")}");
    }

    /// <summary>Режим взаимодействия: окно принимает мышь и клавиатуру. Иначе — не мешает (сквозной клик).</summary>
    public void SetInteract(bool on, bool returnFocus = true)
    {
        if (_interact == on) return;
        _interact = on;
        if (on)
        {
            _returnFocusTo = NativeMethods.GetForegroundWindow();
            GameWindow.ReleaseCursor(); // игры запирают курсор в своём окне
        }
        ApplyStyles();
        UpdateOutsidePoll();
        Web.TameEngineWindows();
        if (on)
        {
            // окно под браузером (игра) должно перестать получать мышь и клавиатуру — забираем фокус у него
            TakeForeground();
            Web.Focus();
        }
        else if (returnFocus && _returnFocusTo != IntPtr.Zero && _returnFocusTo != _hwnd && NativeMethods.IsWindow(_returnFocusTo))
        {
            NativeMethods.SetForegroundWindow(_returnFocusTo);
        }
        if (!on) ClearSelection();
        UpdateSidebar();
        Log.Info($"pip: режим взаимодействия {(on ? "вкл" : "выкл")}");
    }

    // «Авто взаимодействие»: нажали мимо браузера — режим выключается. Не только по потере фокуса (CheckLeftInteract):
    // уголок, панель и многие окна фокус не забирают — щелчок по ним окно браузера не «деактивировал». Поэтому, пока
    // режим включён, смотрим на кнопки мыши (опрос, как у тройного щелчка): новое нажатие не над своими окнами — мимо.
    private DispatcherTimer? _outsidePoll;
    private bool _outsideWasDown;

    private void UpdateOutsidePoll()
    {
        if (Mode == PipMode.Through || !IsVisible)
        {
            _outsidePoll?.Stop();
            return;
        }
        if (_outsidePoll is null)
        {
            _outsidePoll = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(15) };
            _outsidePoll.Tick += (_, _) => PollOutside();
        }
        if (_outsidePoll.IsEnabled) return;
        _outsideWasDown = AnyButtonDown(); // нажатие, которым режим включили, не считаем
        _outsidePoll.Start();
    }

    private void PollOutside()
    {
        if (Mode == PipMode.Through || !IsVisible)
        {
            _outsidePoll?.Stop();
            return;
        }
        var down = AnyButtonDown();
        if (down && !_outsideWasDown && PressIsOutside())
        {
            Log.Info("pip: щелчок мимо браузера — сквозной клик");
            SetMode(PipMode.Through, returnFocus: false); // фокус — там, куда щёлкнули
        }
        _outsideWasDown = down;
    }

    private static bool AnyButtonDown() =>
        (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0
        || (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

    /// <summary>
    /// Нажали не над браузером и не над его окошками (панель кнопок, поиск, меню, ползунок; выпадающие списки и меню
    /// самой страницы — окна движка). Большая панель и уголок — «мимо», даже если лежат поверх браузера.
    /// </summary>
    private bool PressIsOutside()
    {
        NativeMethods.GetCursorPos(out var pt);
        var hit = WindowFromPoint(pt);
        if (hit == IntPtr.Zero) return true;
        var ov = App.Instance.Overlay;
        if (ov is not null && ov.Handle != IntPtr.Zero && GetAncestor(hit, GA_ROOTOWNER) == ov.Handle) return true;
        NativeMethods.GetWindowThreadProcessId(hit, out var pid);
        if (pid == (uint)Environment.ProcessId) return false;
        uint engine = 0;
        try { engine = _ready ? Web.CoreWebView2!.BrowserProcessId : 0; } catch { }
        return pid != engine;
    }

    private const int VK_RBUTTON = 0x02, VK_MBUTTON = 0x04;

    /// <summary>«Авто взаимодействие»: щёлкнули куда-то кроме браузера и его панелей — режим выключается.</summary>
    private void CheckLeftInteract()
    {
        if (!_interact) return;
        Dispatcher.InvokeAsync(() =>
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (!_interact || fg == _hwnd || fg == IntPtr.Zero) return;
            // свои окошки браузера (панель кнопок, ползунок, меню, поиск) — не «мимо»; большая панель — мимо
            NativeMethods.GetWindowThreadProcessId(fg, out var pid);
            var overlay = App.Instance.Overlay is { } o ? new WindowInteropHelper(o).Handle : IntPtr.Zero;
            if (pid == (uint)Environment.ProcessId && fg != overlay) return;
            SetMode(PipMode.Through, returnFocus: false);
        }, DispatcherPriority.Background);
    }

    /// <summary>Снять выделение текста и фокус в поле страницы (при выходе из режима взаимодействия).</summary>
    private void ClearSelection()
    {
        if (!_ready) return;
        _ = Web.CoreWebView2.ExecuteScriptAsync(
            "(() => { const s = getSelection(); if (s) s.removeAllRanges(); const a = document.activeElement; if (a && a.blur) a.blur(); })()");
    }

    /// <summary>
    /// Стать активным окном. Простой SetForegroundWindow Windows часто не выполняет (окно не наше) — тогда
    /// ненадолго «прицепляемся» к потоку активного окна. Без этого игра под браузером продолжала бы ловить мышь.
    /// </summary>
    private void TakeForeground()
    {
        Activate();
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.BringWindowToTop(_hwnd);
        if (NativeMethods.SetForegroundWindow(_hwnd) && NativeMethods.GetForegroundWindow() == _hwnd) return;
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

    // ---------- перетаскивание и растягивание ----------
    private const int WM_SIZING = 0x0214, WM_MOVING = 0x0216;
    private const int WMSZ_LEFT = 1, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5,
        WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7;

    private double Aspect => AppConfig.AspectRatio(_cfg.PipAspect);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOVING && msg != WM_SIZING) return IntPtr.Zero;
        var r = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
        var wa = WorkAreaOf(r);
        if (msg == WM_SIZING) r = KeepAspect(r, wParam.ToInt32(), wa);
        r = Clamp(r, wa);
        Marshal.StructureToPtr(r, lParam, false);
        handled = true;
        return new IntPtr(1);
    }

    /// <summary>Тянут за край или угол — вторая сторона подстраивается под соотношение сторон.</summary>
    private NativeMethods.RECT KeepAspect(NativeMethods.RECT r, int edge, NativeMethods.RECT wa)
    {
        var ratio = Aspect;
        var minW = (int)(240 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        int w = r.Width, h = r.Height;
        if (edge is WMSZ_TOP or WMSZ_BOTTOM) w = (int)Math.Round(h * ratio); // тянут по высоте — ширина следом
        w = Math.Clamp(w, minW, Math.Max(minW, MaxWidth(wa, ratio)));
        h = (int)Math.Round(w / ratio);
        // неподвижна та сторона, противоположная той, за которую тянут
        if (edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT) r.Left = r.Right - w;
        else r.Right = r.Left + w;
        if (edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT) r.Top = r.Bottom - h;
        else r.Bottom = r.Top + h;
        return r;
    }

    /// <summary>Самая большая ширина окна: не больше 2/3 рабочей области ни по ширине, ни по высоте.</summary>
    internal static int MaxWidth(NativeMethods.RECT wa, double ratio) =>
        (int)Math.Min(wa.Width * 2 / 3.0, wa.Height * 2 / 3.0 * ratio);

    /// <summary>
    /// Двигается свободно, только не за край экрана. Без «магнита»: Windows присылает новое положение относительно
    /// уже поправленного, мелкими шагами, — притяжение не давало оторвать окно от угла.
    /// </summary>
    private static NativeMethods.RECT Clamp(NativeMethods.RECT r, NativeMethods.RECT wa)
    {
        int w = r.Width, h = r.Height;
        var x = r.Left;
        var y = r.Top;
        x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
        y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
        return new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    private static NativeMethods.RECT WorkAreaOf(NativeMethods.RECT r)
    {
        var mon = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        return NativeMethods.GetMonitorInfo(mon, ref mi) ? mi.rcWork : r;
    }

    /// <summary>
    /// Непрозрачность окна и боковой панели по настройке (0 % на ползунке — это 50 % на деле): у страницы и рамки
    /// (окно-картинка) и у того, что рисует WPF (полоса ошибки).
    /// </summary>
    private void ApplyOpacity()
    {
        var op = 0.5 + 0.5 * Math.Clamp(_cfg.PipOpacity, 0, 100) / 100.0;
        _surface.Opacity = op;
        Root.Opacity = op;
        _sidebar.SetPanelOpacity(_cfg.PipOpacity);
    }

    private bool _resizeOn, _chromeSet;

    /// <summary>Прозрачность, сквозной клик, «не забирать фокус», рамка и края для растягивания — по настройкам и режиму.</summary>
    public void ApplyStyles()
    {
        // края тянутся в режиме взаимодействия (6 px по краю — и над страницей: окно-картинка мышь не забирает)
        if (!_chromeSet || _resizeOn != _interact)
        {
            _chromeSet = true;
            _resizeOn = _interact;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
                ResizeBorderThickness = new Thickness(_interact ? 6 : 0),
            });
        }
        ApplyFrame();
        ApplyWebInput();
        ApplyOpacity();
        if (_hwnd == IntPtr.Zero) return;
        var ex = NativeMethods.ExStyle(_hwnd);
        // сквозной клик (настройка, вне режима взаимодействия): окно «прозрачно» для мыши — щелчки уходят тому, что под ним.
        // Окно и так многослойное (AllowsTransparency — WS_EX_LAYERED не трогать), картинка от этого не меняется:
        // страница — в окне-картинке, которое пропускает мышь всегда
        // ⚠ WS_EX_LAYERED здесь не снимать (окно с AllowsTransparency), а сквозной клик — только этому окну, не PipSurface
        ex = ClickThrough ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~NativeMethods.WS_EX_TRANSPARENT;
        ex = _interact ? ex & ~NativeMethods.WS_EX_NOACTIVATE : ex | NativeMethods.WS_EX_NOACTIVATE;
        ex |= NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetExStyle(_hwnd, ex);
    }

    /// <summary>
    /// Рамка — слоем поверх страницы (в окне-картинке), страницу не сжимает: взаимодействие — сплошная акцентная,
    /// обычный (перетаскивание) — акцентный пунктир, сквозной клик — без рамки. Зовётся и при смене цвета акцента
    /// (раньше рамка оставалась старого цвета, пока не сменят режим).
    /// </summary>
    private void ApplyFrame()
    {
        var accent = DesktopPanel.UI.Theme.Accent.Color;
        if (_interact) _surface.SetFrame(accent, 2);
        else if (ClickThrough) _surface.SetFrame(Colors.Transparent, 1);
        else _surface.SetFrame(Color.FromArgb(0xC0, accent.R, accent.G, accent.B), 2, dashed: true);
    }

    /// <summary>Размер из настроек (ширина; высота — по соотношению сторон).</summary>
    public void ApplySize()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var r)) return;
        var w = _cfg.PipWidth;
        var h = (int)Math.Round(w / Aspect);
        // правый нижний угол остаётся на месте — окно обычно стоит в углу экрана
        Place(r.Right - w, r.Bottom - h, w, h);
    }

    // ---------- положение ----------
    /// <summary>Вернуть окно на место по умолчанию (правый нижний угол) и размер из настроек.</summary>
    public void ResetPlacement()
    {
        if (_hwnd != IntPtr.Zero) RestorePlacement();
    }

    private void RestorePlacement()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = _cfg.PipWidth, h = (int)Math.Round(_cfg.PipWidth / Aspect);
        int x, y;
        if (_cfg.PipX is { } px && _cfg.PipY is { } py)
        {
            x = px;
            y = py;
        }
        else
        {
            // по умолчанию — правый верхний угол основного экрана, вплотную
            var wa = SystemParameters.WorkArea;
            x = (int)(wa.Right * scale) - w;
            y = (int)(wa.Top * scale);
        }
        Place(x, y, w, h);
    }

    /// <summary>Поставить окно, не вылезая за край монитора (например, если монитор отключили).</summary>
    private void Place(int x, int y, int w, int h)
    {
        var mon = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x + w / 2, Y = y + h / 2 },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (NativeMethods.GetMonitorInfo(mon, ref mi))
        {
            var r = mi.rcWork;
            var maxW = MaxWidth(r, Aspect); // не больше 2/3 экрана
            if (w > maxW)
            {
                w = maxW;
                h = (int)Math.Round(w / Aspect);
            }
            x = Math.Max(r.Left, Math.Min(x, r.Right - w));
            y = Math.Max(r.Top, Math.Min(y, r.Bottom - h));
        }
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_NOACTIVATE);
    }

    private void QueueSaveBounds()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        _saveBounds.Stop();
        _saveBounds.Start();
    }

    private void SaveBounds()
    {
        if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var r))
        {
            _cfg.PipX = r.Left;
            _cfg.PipY = r.Top;
            _cfg.PipWidth = r.Width;
            _cfg.PipHeight = r.Height;
        }
        App.Instance.SaveConfig();
    }

    // ---------- избранное ----------
    private string CurrentUrl => _ready ? Web.CoreWebView2.Source : "";

    private PipFavorite? CurrentFavorite() =>
        StartPage.IsStart(CurrentUrl) ? null
            : _cfg.PipFavorites.FirstOrDefault(f => string.Equals(f.Url.TrimEnd('/'), CurrentUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    private void ToggleFavorite()
    {
        var url = CurrentUrl;
        if (url.Length == 0 || StartPage.IsStart(url)) return;
        if (CurrentFavorite() is { } f) _cfg.PipFavorites.Remove(f);
        else
        {
            var title = _ready ? Web.CoreWebView2.DocumentTitle : "";
            if (string.IsNullOrWhiteSpace(title)) title = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
            if (title.Length > 40) title = title[..39] + "…";
            _cfg.PipFavorites.Add(new PipFavorite { Name = title, Url = url });
        }
        App.Instance.SaveConfig();
        StartPage.Write(_cfg); // плитки на стартовой странице
        UpdateSidebar();
    }

    private void ShowFavorites()
    {
        var menu = new ContextMenu { PlacementTarget = _sidebar.FavoritesButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Left };
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        if (_cfg.PipFavorites.Count == 0)
            menu.Items.Add(new MenuItem { Header = Loc.T("pip.no_favorites"), IsEnabled = false });
        foreach (var f in _cfg.PipFavorites.ToList())
        {
            var mi = new MenuItem { Header = f.Name, ToolTip = f.Url };
            var fav = f;
            mi.Click += (_, _) => Navigate(fav.Url);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }
}
