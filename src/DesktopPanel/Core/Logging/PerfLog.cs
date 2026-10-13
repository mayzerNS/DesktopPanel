using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPanel.Core.Logging;

/// <summary>
/// ВРЕМЕННЫЙ подробный лог производительности для стресс-теста: logs\perf.log (при 20 МБ — в perf.1.log).
/// Выключается одной константой Enabled. Чтобы сам лог не тормозил программу: в потоке интерфейса — только счётчики
/// (без строк и без файлов), строки собираются и пишутся в фоновом потоке.
///
/// Что пишет:
/// — раз в секунду (пока панель открыта; закрыта — раз в 10 с): процессор, память, сборка мусора, потоки и дескрипторы,
///   кадры (сколько, средний и худший интервал, сколько дольше 20/33/50 мс) — отдельно для кадров во время анимаций,
///   задержка отклика потока интерфейса (сколько ждёт задание с приоритетом ввода), какие анимации шли;
/// — каждое нажатие (мышь, колесо, клавиши): ожидание в очереди Windows (точность ~16 мс — так меряет Windows),
///   время обработки, через сколько начался кадр с результатом;
/// — метки «действие → первый кадр» (горячая клавиша → панель на экране и т.п.);
/// — каждое задание потока интерфейса дольше 25 мс — с именем метода;
/// — каждое действие: щелчок по кнопке/переключателю/пункту меню (что именно и где), итог ползунка, горячая клавиша,
///   переход между страницами панели. Текст из полей ввода не пишется.
/// Кадры меряются по CompositionTarget.Rendering — только пока панель открыта и есть что рисовать (идут анимации,
/// нажатие или отметка ждут кадра): сама подписка заставляет WPF рисовать каждый кадр монитора, в простое — не держим.
/// </summary>
public static class PerfLog
{
    public const bool Enabled = false; // включить для замеров: perf.log

    private const double LongOpMs = 25;
    private const long MaxSize = 20L * 1024 * 1024;

    private static readonly ConcurrentQueue<string> Lines = new();
    private static readonly object Gate = new();
    private static Dispatcher? _ui;
    private static Thread? _writer;
    private static Timer? _sampler;
    private static bool _watching;
    private static long _lastFrame;
    private static TimeSpan _lastRenderTime;

    // --- счётчики за текущий интервал (пишутся в потоке интерфейса, забираются под Gate) ---
    private sealed class FrameStats
    {
        public int N, N20, N33, N50;
        public double Sum, Max;

        public void Add(double ms)
        {
            N++;
            Sum += ms;
            if (ms > Max) Max = ms;
            if (ms > 20) N20++;
            if (ms > 33) N33++;
            if (ms > 50) N50++;
        }

        public string Text => N == 0 ? "—" : $"{N} (сред {Sum / N:0.0} мс, худший {Max:0.0}, >20мс {N20}, >33мс {N33}, >50мс {N50})";
    }

    private static FrameStats _frames = new(), _animFrames = new();
    private static double _uiLagMax, _uiLagSum;
    private static int _uiLagN, _moves;
    private static readonly HashSet<string> AnimInInterval = new();
    private static long _animLast;

    // --- ввод, ждущий кадра ---
    private sealed record PendingInput(string Name, long Start, int QueueMs);
    private static PendingInput? _pressed;   // нажатие: от Preview до обработки
    private static readonly List<(PendingInput In, double HandleMs)> WaitingFrame = new();
    private static readonly List<(string What, long Start)> Marks = new();

    // --- задания потока интерфейса ---
    private static readonly Dictionary<DispatcherOperation, long> OpStart = new();
    private static readonly FieldInfo? OpMethod = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? OpArgs = typeof(DispatcherOperation).GetField("_args", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TimerTick = typeof(DispatcherTimer).GetField("Tick", BindingFlags.Instance | BindingFlags.NonPublic);

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private static string Now => DateTime.Now.ToString("HH:mm:ss.fff");

    public static void Start(Dispatcher ui)
    {
        if (!Enabled || _ui is not null) return;
        _ui = ui;
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "perf-log", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
        var fps = App.Instance?.Config.AnimFps ?? 0;
        Lines.Enqueue($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} старт, ядер {Environment.ProcessorCount}, " +
                      $"кадров анимаций (настройка): {(fps > 0 ? fps.ToString() : "как у монитора")} ====");

        InputManager.Current.PreProcessInput += OnPreInput;
        InputManager.Current.PostProcessInput += OnPostInput;
        ui.Hooks.OperationStarted += (_, e) => OpStart[e.Operation] = Stopwatch.GetTimestamp();
        ui.Hooks.OperationAborted += (_, e) => OpStart.Remove(e.Operation);
        ui.Hooks.OperationCompleted += OnOpCompleted;
        _sampler = new Timer(_ => Sample(), null, 10000, 10000); // панель закрыта — раз в 10 с (SetWatching меняет)

        // действия: что нажали (а не только «мышь нажата»)
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase),
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, _) => Action("щелчок", s)), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.MenuItem),
            System.Windows.Controls.MenuItem.ClickEvent, new RoutedEventHandler((s, _) => Action("пункт меню", s)), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.Thumb),
            System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, new RoutedEventHandler((s, _) =>
            {
                for (DependencyObject? d = s as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
                    if (d is System.Windows.Controls.Slider sl)
                    {
                        Action($"ползунок = {sl.Value:0.##}", sl);
                        return;
                    }
            }), true);
    }

    /// <summary>Действие пользователя: что за элемент (имя, подпись, подсказка) и где (страница/окно).</summary>
    public static void Action(string what, object? source)
    {
        if (!Enabled) return;
        try
        {
            Lines.Enqueue($"{Now} ДЕЙСТВИЕ: {what} {Describe(source)}");
        }
        catch { }
    }

    private static string Describe(object? source)
    {
        if (source is not FrameworkElement el) return source?.GetType().Name ?? "";
        string? label = el switch
        {
            DesktopPanel.UI.Controls.IconButton ib => First(ib.Text, ib.Tip as string, ib.ToolTip as string),
            System.Windows.Controls.HeaderedItemsControl h => h.Header as string,
            System.Windows.Controls.ContentControl c => First(c.Content as string, (c.Content as System.Windows.Controls.TextBlock)?.Text, c.ToolTip as string),
            _ => el.ToolTip as string,
        };
        var state = el is System.Windows.Controls.Primitives.ToggleButton { IsChecked: { } on } && el is not DesktopPanel.UI.Controls.IconButton
            ? (on ? " → вкл" : " → выкл") : "";
        string? where = null;
        for (DependencyObject? d = VisualTreeHelper.GetParent(el); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.UserControl or Window)
            {
                where = d.GetType().Name;
                break;
            }
        var name = string.IsNullOrEmpty(el.Name) ? el.GetType().Name : el.Name;
        var text = label is null ? "" : $"«{Short(label)}» ";
        return $"{text}[{name}]{state}{(where is null ? "" : " · " + where)}";
    }

    private static string? First(params string?[] s) => s.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    private static string Short(string s)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= 60 ? s : s[..57] + "…";
    }

    /// <summary>Панель показали/спрятали: кадры меряем только пока она на экране.</summary>
    public static void SetWatching(bool on)
    {
        if (!Enabled || _ui is null || on == _watching) return;
        _watching = on;
        UpdateHook();
        // панель открыта — строка раз в секунду, закрыта — раз в 10 с (и замер не будит потоки зря каждую секунду)
        _sampler?.Change(on ? 1000 : 10000, on ? 1000 : 10000);
        Lines.Enqueue($"{Now} панель {(on ? "показана — меряем кадры" : "скрыта")}");
    }

    private static bool _hooked;

    /// <summary>
    /// На кадры подписываемся, только пока их есть кому рисовать: идут анимации (FrameClock) или ждут кадра нажатие
    /// и отметка. Подписка на CompositionTarget.Rendering сама заставляет WPF рисовать каждый кадр монитора — раньше
    /// лог держал её всё время, пока панель открыта, и в простое программа рисовала 60–200 кадров/с только ради замера
    /// (это же попадало в «ЦП» лога). Простой теперь в логе — «кадры —».
    /// </summary>
    private static void UpdateHook()
    {
        var want = _watching && (UI.Theme.FrameClock.Active || WaitingFrame.Count > 0 || Marks.Count > 0);
        if (want == _hooked) return;
        _hooked = want;
        _lastFrame = 0; // промежуток простоя кадром не считаем
        if (want) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>FrameClock начал считать кадры (пошла анимация) — меряем и мы.</summary>
    public static void ClockStarted()
    {
        if (Enabled && _watching && !_hooked) UpdateHook();
    }

    /// <summary>Покадровая анимация name сделала шаг (зовётся из её кадра; «шла» = был шаг за последние 50 мс).</summary>
    public static void AnimTick(string name)
    {
        if (!Enabled) return;
        var now = Stopwatch.GetTimestamp();
        _animLast = now;
        lock (Gate) AnimInInterval.Add(name);
    }

    /// <summary>Анимация WPF (DoubleAnimation и т.п.) на ms миллисекунд — считаем её идущей это время.</summary>
    public static void AnimFor(string name, double ms)
    {
        if (!Enabled) return;
        var until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
        _animLast = Math.Max(_animLast, until);
        lock (Gate) AnimInInterval.Add(name);
    }

    /// <summary>Действие началось — в лог попадёт, через сколько после него начался первый кадр.</summary>
    public static void Mark(string what)
    {
        if (!Enabled) return;
        Marks.Add((what, Stopwatch.GetTimestamp()));
        if (!_watching) Lines.Enqueue($"{Now} отметка «{what}» (кадры пока не меряются)");
        else UpdateHook();
    }

    /// <summary>Строка в лог как есть.</summary>
    public static void Note(string text)
    {
        if (Enabled) Lines.Enqueue($"{Now} {text}");
    }

    // ---------- поток интерфейса: только счётчики ----------
    private static void OnFrame(object? sender, EventArgs e)
    {
        // одно и то же время отрисовки бывает в нескольких вызовах подряд — считаем кадр один раз
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime == _lastRenderTime) return;
            _lastRenderTime = r.RenderingTime;
        }
        var now = Stopwatch.GetTimestamp();
        if (_lastFrame != 0)
        {
            var ms = Ms(now - _lastFrame);
            var anim = Ms(now - _animLast) < 50 || _animLast > now;
            lock (Gate)
            {
                _frames.Add(ms);
                if (anim) _animFrames.Add(ms);
            }
        }
        _lastFrame = now;
        if (WaitingFrame.Count > 0)
        {
            foreach (var (inp, handle) in WaitingFrame)
                Lines.Enqueue($"{Now} ввод {inp.Name}: очередь Windows {inp.QueueMs} мс, обработка {handle:0.0} мс, " +
                              $"до кадра {Ms(now - inp.Start):0.0} мс (+очередь)");
            WaitingFrame.Clear();
        }
        if (Marks.Count > 0)
        {
            foreach (var (what, start) in Marks) Lines.Enqueue($"{Now} «{what}» → первый кадр через {Ms(now - start):0.0} мс");
            Marks.Clear();
        }
        if (!UI.Theme.FrameClock.Active) UpdateHook(); // анимации кончились, ждать нечего — не держим кадры
    }

    private static string? InputName(RoutedEvent? ev, InputEventArgs args) => args switch
    {
        _ when ev == Mouse.PreviewMouseDownEvent => $"мышь {((MouseButtonEventArgs)args).ChangedButton} нажата",
        _ when ev == Mouse.PreviewMouseUpEvent => $"мышь {((MouseButtonEventArgs)args).ChangedButton} отпущена",
        _ when ev == Mouse.PreviewMouseWheelEvent => "колесо",
        _ when ev == Keyboard.PreviewKeyDownEvent => $"клавиша {((KeyEventArgs)args).Key}",
        _ => null,
    };

    private static void OnPreInput(object sender, PreProcessInputEventArgs e)
    {
        var args = e.StagingItem.Input;
        var ev = args.RoutedEvent;
        if (ev == Mouse.PreviewMouseMoveEvent)
        {
            _moves++;
            return;
        }
        if (InputName(ev, args) is not { } name) return;
        var queue = Math.Max(0, Environment.TickCount - args.Timestamp);
        _pressed = new PendingInput(name, Stopwatch.GetTimestamp(), queue);
    }

    private static void OnPostInput(object sender, ProcessInputEventArgs e)
    {
        var ev = e.StagingItem.Input.RoutedEvent;
        if (_pressed is null) return;
        if (ev != Mouse.MouseDownEvent && ev != Mouse.MouseUpEvent && ev != Mouse.MouseWheelEvent && ev != Keyboard.KeyDownEvent) return;
        var handle = Ms(Stopwatch.GetTimestamp() - _pressed.Start);
        if (_watching)
        {
            WaitingFrame.Add((_pressed, handle));
            UpdateHook();
        }
        else Lines.Enqueue($"{Now} ввод {_pressed.Name}: очередь Windows {_pressed.QueueMs} мс, обработка {handle:0.0} мс");
        _pressed = null;
    }

    private static void OnOpCompleted(object? sender, DispatcherHookEventArgs e)
    {
        if (!OpStart.Remove(e.Operation, out var start)) return;
        var ms = Ms(Stopwatch.GetTimestamp() - start);
        if (ms < LongOpMs) return;
        string what;
        try
        {
            what = OpMethod?.GetValue(e.Operation) is Delegate d ? $"{d.Method.DeclaringType?.Name}.{d.Method.Name}" : "?";
            // таймер WPF (DispatcherTimer.Restart): чей — по его обработчикам Tick
            if (OpArgs?.GetValue(e.Operation) is DispatcherTimer timer && TimerTick?.GetValue(timer) is Delegate tick)
                what += " → таймер " + string.Join(", ", tick.GetInvocationList()
                    .Select(h => $"{h.Method.DeclaringType?.Name}.{h.Method.Name}")) + $" ({timer.Interval.TotalMilliseconds:0} мс)";
        }
        catch { what = "?"; }
        Lines.Enqueue($"{Now} ДОЛГО: задание интерфейса {ms:0.0} мс, приоритет {e.Operation.Priority}, {what}");
    }

    // ---------- фоновый поток: замеры раз в секунду ----------
    private static TimeSpan _cpuPrev;
    private static long _cpuWall;
    private static int _gc0, _gc1, _gc2;
    private static TimeSpan _gcPause;

    private static void Sample()
    {
        try
        {
            // задержка отклика: задание с приоритетом ввода — когда до него дойдёт поток интерфейса
            var t0 = Stopwatch.GetTimestamp();
            _ui?.BeginInvoke(DispatcherPriority.Input, () =>
            {
                var lag = Ms(Stopwatch.GetTimestamp() - t0);
                lock (Gate)
                {
                    _uiLagN++;
                    _uiLagSum += lag;
                    if (lag > _uiLagMax) _uiLagMax = lag;
                }
            });

            using var p = Process.GetCurrentProcess();
            var cpu = p.TotalProcessorTime;
            var wall = Stopwatch.GetTimestamp();
            var cpuPct = _cpuWall == 0 ? 0 : (cpu - _cpuPrev).TotalMilliseconds / Ms(wall - _cpuWall) * 100;
            _cpuPrev = cpu;
            _cpuWall = wall;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            var pause = GC.GetTotalPauseDuration();

            FrameStats frames, anim;
            double lagMax, lagAvg;
            string anims;
            int moves;
            lock (Gate)
            {
                frames = _frames;
                anim = _animFrames;
                _frames = new FrameStats();
                _animFrames = new FrameStats();
                lagMax = _uiLagMax;
                lagAvg = _uiLagN == 0 ? 0 : _uiLagSum / _uiLagN;
                _uiLagMax = _uiLagSum = 0;
                _uiLagN = 0;
                anims = AnimInInterval.Count == 0 ? "нет" : string.Join(", ", AnimInInterval);
                AnimInInterval.Clear();
                moves = _moves;
                _moves = 0;
            }
            var sb = new StringBuilder(320);
            sb.Append(Now)
              .Append($" | ЦП {cpuPct:0} % одного ядра ({cpuPct / Environment.ProcessorCount:0.0} % всего)")
              .Append($" | память {p.WorkingSet64 >> 20} МБ (своя {p.PrivateMemorySize64 >> 20}, куча .NET {GC.GetTotalMemory(false) >> 20})")
              .Append($" | сборки мусора {g0 - _gc0}/{g1 - _gc1}/{g2 - _gc2}, пауза {(pause - _gcPause).TotalMilliseconds:0.0} мс")
              .Append($" | потоков {p.Threads.Count}, дескрипторов {p.HandleCount}")
              .Append($" | отклик интерфейса сред {lagAvg:0.0} мс, худший {lagMax:0.0}")
              .Append($" | кадры {frames.Text}")
              .Append($" | в анимациях {anim.Text}")
              .Append($" | анимации: {anims}")
              .Append($" | движений мыши {moves}");
            Lines.Enqueue(sb.ToString());
            (_gc0, _gc1, _gc2, _gcPause) = (g0, g1, g2, pause);
        }
        catch (Exception ex) { Lines.Enqueue($"{Now} замер не удался: {ex.Message}"); }
    }

    private static void WriterLoop()
    {
        var path = Path.Combine(Log.Folder, "perf.log");
        while (true)
        {
            Thread.Sleep(250);
            if (Lines.IsEmpty) continue;
            try
            {
                Directory.CreateDirectory(Log.Folder);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxSize)
                {
                    File.Copy(path, Path.Combine(Log.Folder, "perf.1.log"), overwrite: true);
                    File.WriteAllText(path, "");
                }
                using var w = new StreamWriter(path, append: true, Encoding.UTF8);
                while (Lines.TryDequeue(out var line)) w.WriteLine(line);
            }
            catch { /* лог не должен ронять программу */ }
        }
    }
}
