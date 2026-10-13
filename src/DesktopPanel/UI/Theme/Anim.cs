using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopPanel.UI.Theme;

/// <summary>
/// Плавные изменения свойств — замена BeginAnimation/Storyboard. Все идут в одном обработчике кадра (FrameClock):
/// подчиняются пределу частоты кадров и выключателю анимаций, и не создают часов WPF на каждую анимацию.
/// Значение ставится прямо в свойство (как обычное присваивание), поэтому после анимации его можно читать и менять.
/// Новая анимация того же свойства заменяет старую (с текущего значения — без рывка).
/// </summary>
public static class Anim
{
    private sealed class Tween
    {
        public object Owner = null!;
        public object? Prop;
        public double T, Delay, Ms;
        public int Frames;
        public IEasingFunction? Ease;
        public Action<double> Apply = null!;
        public Action? Done;
        public bool Dead;
    }

    private static readonly List<Tween> List = new();
    private static readonly Dictionary<(object, object?), Tween> ByKey = new();
    private static Tween[] _iter = new Tween[16];
    private static bool _hooked;
    private static double _last;

    /// <summary>
    /// Анимация общего вида: apply(k) получает долю хода 0…1 (после кривой ease; у кривых с перелётом бывает больше 1).
    /// owner+prop — ключ: новая анимация с тем же ключом заменяет старую. delay — задержка старта (на ней стоит k = 0).
    /// </summary>
    public static void Run(object owner, object? prop, double ms, Action<double> apply, IEasingFunction? ease = null,
        Action? done = null, double delay = 0)
    {
        Stop(owner, prop);
        if (!Motion.Enabled || ms <= 0)
        {
            apply(1);
            done?.Invoke();
            return;
        }
        var t = new Tween { Owner = owner, Prop = prop, Ms = ms, Delay = delay, Ease = ease, Apply = apply, Done = done };
        apply(ease?.Ease(0) ?? 0); // сразу в начальной точке — ни кадра в конечной
        ByKey[(owner, prop)] = t;
        List.Add(t);
        if (_hooked) return;
        _hooked = true;
        _last = FrameClock.NowMs;
        FrameClock.Rendering += OnFrame;
    }

    /// <summary>Плавно к значению to (с текущего, или с from).</summary>
    public static void To(DependencyObject o, DependencyProperty p, double to, double ms = Motion.UiMs,
        IEasingFunction? ease = null, Action? done = null, double? from = null, double delay = 0)
    {
        var f = from ?? (double)o.GetValue(p);
        if (from is null && delay <= 0 && Math.Abs(f - to) < 1e-4)
        {
            Stop(o, p);
            if (!Equals(o.GetValue(p), to)) o.SetValue(p, to);
            done?.Invoke();
            return;
        }
        Run(o, p, ms, k => o.SetValue(p, f + (to - f) * k), ease, done, delay);
    }

    /// <summary>Плавно к цвету (кисть должна быть своей, не замороженной).</summary>
    public static void Color(SolidColorBrush b, Color to, double ms, IEasingFunction? ease = null, Action? done = null)
    {
        var f = b.Color;
        Run(b, SolidColorBrush.ColorProperty, ms, k =>
        {
            var c = Math.Clamp(k, 0, 1);
            b.Color = System.Windows.Media.Color.FromArgb(
                (byte)Math.Round(f.A + (to.A - f.A) * c), (byte)Math.Round(f.R + (to.R - f.R) * c),
                (byte)Math.Round(f.G + (to.G - f.G) * c), (byte)Math.Round(f.B + (to.B - f.B) * c));
        }, ease, done);
    }

    /// <summary>Остановить (значение остаётся текущим, done не зовётся).</summary>
    public static void Stop(object owner, object? prop = null)
    {
        if (!ByKey.Remove((owner, prop), out var t)) return;
        t.Dead = true;
        List.Remove(t);
    }

    public static bool IsRunning(object owner, object? prop = null) => ByKey.ContainsKey((owner, prop));

    /// <summary>Довести до конца сразу (конечное значение и done).</summary>
    public static void Finish(object owner, object? prop = null)
    {
        if (!ByKey.TryGetValue((owner, prop), out var t)) return;
        End(t);
    }

    /// <summary>Все идущие анимации — сразу в конец (выключили анимации).</summary>
    public static void FinishAll()
    {
        foreach (var t in List.ToArray()) End(t);
    }

    private static void End(Tween t)
    {
        if (t.Dead) return;
        t.Dead = true;
        ByKey.Remove((t.Owner, t.Prop));
        List.Remove(t);
        Guard(t, () => t.Apply(1), "конец");
        if (t.Done is { } done) Guard(t, done, "после конца");
    }

    /// <summary>
    /// Шаг анимации с защитой: ошибка в одной анимации останавливает только её. Раньше исключение уходило в общий
    /// обработчик кадра, тот отписывал весь Anim — и замирали ВСЕ анимации программы (панель не появлялась, размытие
    /// уголка «ползло»). В лог — полное место ошибки.
    /// </summary>
    private static void Guard(Tween t, Action a, string where)
    {
        try { a(); }
        catch (Exception ex) { Fail(t, ex, where); }
    }

    private static void Fail(Tween t, Exception ex, string where)
    {
        Core.Logging.Log.Error($"анимация {t.Owner.GetType().Name}/{t.Prop}: ошибка ({where}), анимация остановлена", ex);
        if (t.Dead) return;
        t.Dead = true;
        // ⚠ убирать только свою запись: ключ мог уже занять новый твин того же свойства (запущен из apply)
        if (ByKey.TryGetValue((t.Owner, t.Prop), out var cur) && cur == t) ByKey.Remove((t.Owner, t.Prop));
        List.Remove(t);
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = FrameClock.NowMs;
        var dt = now - _last;
        _last = now;
        var n = List.Count;
        if (n == 0)
        {
            FrameClock.Rendering -= OnFrame;
            _hooked = false;
            return;
        }
        // Кадр может начаться ВНУТРИ кадра: анимация в конце показывает окно (переход в уголок — ShowCorner), Windows
        // при этом прокачивает сообщения, и WPF успевает отрисовать следующий кадр вложенно. Общий список-копия тогда
        // затирался вложенным кадром — внешний натыкался на пустые места (NullReference) и весь Anim вставал.
        // Поэтому вложенный кадр берёт свою копию.
        // ⚠ общий буфер _iter — только во внешнем кадре: во вложенном он затирался и внешний цикл падал (null)
        Tween[] iter;
        if (_inFrame) iter = List.ToArray();
        else
        {
            if (_iter.Length < n) _iter = new Tween[Math.Max(n, _iter.Length * 2)];
            List.CopyTo(_iter);
            iter = _iter;
        }
        var outer = !_inFrame;
        _inFrame = true;
        try { Step(iter, n, dt); }
        finally
        {
            if (outer) _inFrame = false;
        }
    }

    private static bool _inFrame;

    private static void Step(Tween[] iter, int n, double dt)
    {
        for (var i = 0; i < n; i++)
        {
            var t = iter[i];
            iter[i] = null!;
            if (t is null || t.Dead) continue;
            // первые кадры анимации бывают тяжёлыми (страница только что перестроилась) — долгий кадр считаем коротким,
            // иначе анимация начиналась бы с прыжка
            t.T += ++t.Frames <= 2 ? Math.Min(dt, Motion.FirstFrameCapMs) : dt;
            if (!Motion.Enabled) t.T = double.MaxValue / 4;
            var local = t.T - t.Delay;
            if (local <= 0) continue;
            var p = Math.Min(1, local / t.Ms);
            if (p >= 1)
            {
                End(t);
                continue;
            }
            var k = t.Ease?.Ease(p) ?? p;
            // не через Guard: лямбда на каждый шаг каждой анимации — лишняя память в каждом кадре
            try { t.Apply(k); }
            catch (Exception ex) { Fail(t, ex, "кадр"); }
        }
    }
}

/// <summary>
/// Секундомер анимаций, которые считают время сами (плитки рабочего стола и т.п.): как Stopwatch, но при выключенных
/// анимациях сразу показывает «прошло очень много» — любая анимация заканчивается в первом же кадре.
/// </summary>
public sealed class AnimWatch
{
    private static readonly TimeSpan Skip = TimeSpan.FromHours(1);
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public static AnimWatch StartNew() => new();

    public TimeSpan Elapsed => Motion.Enabled ? _sw.Elapsed : _sw.Elapsed + Skip;

    public void Restart() => _sw.Restart();
}
