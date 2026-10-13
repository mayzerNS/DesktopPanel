using System.Diagnostics;
using System.Windows.Media;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.UI.Theme;

/// <summary>
/// Единый покадровый счётчик всех анимаций программы — вместо CompositionTarget.Rendering у каждой анимации своего.
/// • Предел частоты кадров (Motion.Fps): если он ниже частоты монитора, лишние кадры пропускаются — меньше работы
///   и процессору, и видеокарте. Кадры выбираются по «расписанию» (каждые FrameMs, с допуском в полкадра монитора),
///   поэтому шаг ровный: 60 из 144 Гц — это 60 кадров/с, а не 48.
/// • Один обработчик у WPF на всех: WPF рисует каждый кадр монитора, пока на Rendering кто-то подписан, — когда
///   анимаций нет, счётчик отписывается сам, и программа в простое ничего не рисует.
/// • WPF иногда зовёт Rendering дважды за кадр — второй вызов (то же RenderingTime) пропускается.
/// </summary>
public static class FrameClock
{
    private static readonly List<EventHandler> Subs = new();
    private static EventHandler[] _snap = Array.Empty<EventHandler>();
    private static bool _dirty, _hooked;
    private static TimeSpan _lastRenderTime = TimeSpan.MinValue;
    private static double _due;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Как CompositionTarget.Rendering, но с пределом частоты кадров.</summary>
    public static event EventHandler Rendering
    {
        add
        {
            if (value is null) return;
            Subs.Add(value);
            _dirty = true;
            if (_hooked) return;
            _hooked = true;
            _due = 0; // первый кадр — сразу
            CompositionTarget.Rendering += OnRendering;
            PerfLog.ClockStarted();
        }
        remove
        {
            if (value is null) return;
            var i = Subs.LastIndexOf(value);
            if (i < 0) return;
            Subs.RemoveAt(i);
            _dirty = true;
        }
    }

    /// <summary>Время (мс) от запуска программы — общее для всех анимаций.</summary>
    public static double NowMs => Clock.Elapsed.TotalMilliseconds;

    /// <summary>Счётчик подписан на кадры WPF (кто-то анимируется) — WPF рисует каждый кадр монитора.</summary>
    public static bool Active => _hooked;

    private static bool _inTick;

    private static void OnRendering(object? sender, EventArgs e)
    {
        // Кадр внутри кадра: обработчик кадра позвал то, что прокачивает сообщения Windows (оболочка — корзина, показ окна),
        // и WPF отрисовал следующий кадр, не дождавшись конца текущего. Шаги анимаций в нём шли вложенно, посреди своего же
        // шага: лента рабочего стола после корзины «сходила с ума» (значки носились туда-сюда), Anim падал. Вложенный кадр
        // пропускаем целиком: он просто показывает то, что уже есть; следующий обычный кадр догонит по времени.
        // ⚠ не убирать: вложенный кадр шёл шагами анимаций посреди их же шага — лента «сходила с ума», Anim падал
        if (_inTick) return;
        _inTick = true;
        try { Tick(sender, e); }
        finally { _inTick = false; }
    }

    private static void Tick(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime == _lastRenderTime) return;
            _lastRenderTime = r.RenderingTime;
        }
        if (Subs.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
            return;
        }
        if (Motion.Enabled && Motion.Limited)
        {
            var now = NowMs;
            var step = Motion.FrameMs;
            if (now < _due - Motion.VsyncMs * 0.5) return; // этот кадр монитора пропускаем
            // по расписанию; если отстали больше чем на кадр (простой, тяжёлый кадр) — расписание заново от сейчас
            _due = now - _due > step ? now + step : _due + step;
        }
        if (_dirty)
        {
            _snap = Subs.ToArray();
            _dirty = false;
        }
        var snap = _snap;
        for (var i = 0; i < snap.Length; i++)
        {
            var h = snap[i];
            // отписавшийся в этом же кадре (например, соседней анимацией) больше не зовётся
            if (_dirty && !Subs.Contains(h)) continue;
            try { h(sender, e); }
            catch (Exception ex)
            {
                // сломавшаяся анимация не должна останавливать остальные (и сыпать ошибками каждый кадр)
                Log.Error($"анимация: ошибка в кадре, обработчик {h.Method.DeclaringType?.Name}.{h.Method.Name} остановлен", ex);
                Rendering -= h;
            }
        }
    }
}
