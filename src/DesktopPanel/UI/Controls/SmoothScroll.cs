using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopPanel.UI.Controls;

/// <summary>
/// Плавная прокрутка колесом: один щелчок колеса — на заданный шаг (на рабочем столе — ровно ряд значков).
/// Подключение в XAML: controls:SmoothScroll.Step="116" на ScrollViewer.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached(
        "Step", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0.0, OnStepChanged));

    public static double GetStep(DependencyObject d) => (double)d.GetValue(StepProperty);
    public static void SetStep(DependencyObject d, double value) => d.SetValue(StepProperty, value);

    private sealed class State
    {
        public double Target;
        public bool Running;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(SmoothScroll));

    private static void OnStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        sv.PreviewMouseWheel -= OnWheel;
        if ((double)e.NewValue > 0) sv.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (sv.ScrollableHeight <= 0) return;
        e.Handled = true;

        if (sv.GetValue(StateProperty) is not State st)
        {
            st = new State();
            sv.SetValue(StateProperty, st);
        }
        var from = st.Running ? st.Target : sv.VerticalOffset;
        st.Target = Math.Clamp(from - e.Delta / 120.0 * GetStep(sv), 0, sv.ScrollableHeight);
        if (!Theme.Motion.Enabled)
        {
            sv.ScrollToVerticalOffset(st.Target);
            return;
        }
        if (st.Running) return;

        st.Running = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = 0.0;
        EventHandler? tick = null;
        var stuck = 0;
        var prev = double.NaN;
        tick = (_, _) =>
        {
            // содержимое могло стать короче (сменили раздел), а страницу — спрятать: тогда цель недостижима, и раньше
            // прокрутка «гналась» за ней бесконечно — каждый кадр пересчитывала раскладку, всё подлагивало
            st.Target = Math.Min(st.Target, sv.ScrollableHeight);
            var cur = sv.VerticalOffset;
            stuck = cur == prev ? stuck + 1 : 0;
            prev = cur;
            var diff = st.Target - cur;
            if (Math.Abs(diff) < 0.5 || !sv.IsVisible || stuck > 3)
            {
                sv.ScrollToVerticalOffset(st.Target);
                Theme.FrameClock.Rendering -= tick;
                st.Running = false;
                return;
            }
            // плавное замедление к цели; доля пути за кадр зависит от времени кадра, а не от частоты монитора (на 60 Гц — 0,25 за кадр)
            var now = clock.Elapsed.TotalSeconds;
            var k = 1 - Math.Pow(0.75, Math.Clamp(now - last, 0.001, 0.1) * 60);
            last = now;
            sv.ScrollToVerticalOffset(cur + diff * k);
        };
        Theme.FrameClock.Rendering += tick;
    }
}
