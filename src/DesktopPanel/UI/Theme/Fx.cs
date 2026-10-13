using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopPanel.UI.Theme;

/// <summary>
/// Живые элементы интерфейса — одним языком движения с рабочим столом (Motion: BackEase 0,45, «лесенка»).
/// Всё подключается присоединёнными свойствами в шаблонах Theme.xaml через привязки к состояниям элемента —
/// без подписок на события у каждой кнопки и без утечек:
///   Fx.Show   — слой плавно появляется/гаснет (подсветка наведения, нажатия, «включено»);
///   Fx.Press  — «вдавливание» при нажатии и пружинка обратно;
///   Fx.Dim    — плавное затемнение недоступного элемента;
///   Fx.Knob   — бегунок тумблера едет с перелётом, при нажатии вытягивается;
///   Fx.Grow   — точка ползунка подрастает при наведении и перетаскивании;
///   Fx.Mark   — метка выбранного раздела вырастает;
///   Fx.Swell  — подложка-«пузырь» под значком (папка-приёмник);
///   Fx.PopIn  — подсказки, меню и всплывающие плашки появляются, а не возникают.
/// И в коде: Fx.Enter — строки появляются «лесенкой», Fx.Flip — соседи плавно доезжают, когда что-то появилось или пропало.
/// Пока элемент не на экране, всё ставится сразу, без анимации (и без лишней работы).
/// </summary>
public static class Fx
{
    private static bool Live(DependencyObject d) => Motion.Enabled && d is FrameworkElement { IsLoaded: true, IsVisible: true };

    // ---------- Show: слой появляется/гаснет ----------
    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached(
        "Show", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnShow));

    public static void SetShow(DependencyObject d, bool v) => d.SetValue(ShowProperty, v);
    public static bool GetShow(DependencyObject d) => (bool)d.GetValue(ShowProperty);

    /// <summary>Непрозрачность «включённого» слоя (по умолчанию 1).</summary>
    public static readonly DependencyProperty ShowLevelProperty = DependencyProperty.RegisterAttached(
        "ShowLevel", typeof(double), typeof(Fx), new PropertyMetadata(1.0));

    public static void SetShowLevel(DependencyObject d, double v) => d.SetValue(ShowLevelProperty, v);
    public static double GetShowLevel(DependencyObject d) => (double)d.GetValue(ShowLevelProperty);

    private static void OnShow(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        var on = (bool)e.NewValue;
        var to = on ? GetShowLevel(d) : 0;
        if (!Live(d))
        {
            Anim.Stop(el, UIElement.OpacityProperty);
            el.Opacity = to;
            return;
        }
        Anim.To(el, UIElement.OpacityProperty, to, on ? Motion.HoverInMs : Motion.HoverOutMs, Motion.EaseOut());
    }

    // ---------- Dim: недоступный элемент ----------
    /// <summary>Привязывается к IsEnabled: false — элемент плавно бледнеет до 45 %.</summary>
    public static readonly DependencyProperty DimProperty = DependencyProperty.RegisterAttached(
        "Dim", typeof(bool), typeof(Fx), new PropertyMetadata(true, OnDim));

    public static void SetDim(DependencyObject d, bool v) => d.SetValue(DimProperty, v);
    public static bool GetDim(DependencyObject d) => (bool)d.GetValue(DimProperty);

    private static void OnDim(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        var to = (bool)e.NewValue ? 1 : 0.45;
        if (!Live(d))
        {
            Anim.Stop(el, UIElement.OpacityProperty);
            el.Opacity = to;
            return;
        }
        Anim.To(el, UIElement.OpacityProperty, to, 180, Motion.EaseOut());
    }

    // ---------- Press: вдавливание и пружинка ----------
    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnPress));

    public static void SetPress(DependencyObject d, bool v) => d.SetValue(PressProperty, v);
    public static bool GetPress(DependencyObject d) => (bool)d.GetValue(PressProperty);

    /// <summary>Своё масштабирование элемента (по центру) — создаётся при первой анимации.</summary>
    private static ScaleTransform OwnScale(FrameworkElement el)
    {
        if (el.RenderTransform is ScaleTransform { IsFrozen: false } s) return s;
        // из шаблона преобразование может прийти «замороженным» (общим на все копии) — заменяем своим с тем же размером
        s = el.RenderTransform is ScaleTransform old ? new ScaleTransform(old.ScaleX, old.ScaleY) : new ScaleTransform(1, 1);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = s;
        return s;
    }

    private static void OnPress(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var down = (bool)e.NewValue;
        if (!Live(d))
        {
            if (el.RenderTransform is ScaleTransform st0 && !st0.IsFrozen)
            {
                Anim.Stop(st0, ScaleTransform.ScaleXProperty);
                st0.ScaleX = st0.ScaleY = 1;
            }
            return;
        }
        var st = OwnScale(el);
        // маленькие кнопки вдавливаются заметнее, широкие — на те же 2–3 пикселя с каждой стороны
        var k = down ? 1 - Math.Clamp(3.0 / Math.Max(1, el.ActualWidth), 0.02, 0.07) : 1;
        var from = st.ScaleX;
        // обратно — плавно, без перелёта (с пружинкой кнопки «дрожали»)
        Anim.Run(st, ScaleTransform.ScaleXProperty, down ? Motion.PressMs : 260, p =>
        {
            st.ScaleX = st.ScaleY = from + (k - from) * p;
        }, down ? Motion.QuickEase() : Motion.UiEaseDamped());
    }

    // ---------- Knob: бегунок тумблера ----------
    private sealed class KnobState
    {
        public readonly TranslateTransform Tt = new();
        public readonly ScaleTransform St = new(1, 1);
        public double P, Stretch = 1; // P: 0 — выкл, 1 — вкл (с перелётом)
        public bool On, Down;
    }

    private static readonly DependencyProperty KnobStateProperty = DependencyProperty.RegisterAttached(
        "KnobState", typeof(KnobState), typeof(Fx));

    /// <summary>Привязывается к IsChecked тумблера.</summary>
    public static readonly DependencyProperty KnobProperty = DependencyProperty.RegisterAttached(
        "Knob", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnKnob));

    public static void SetKnob(DependencyObject d, bool v) => d.SetValue(KnobProperty, v);
    public static bool GetKnob(DependencyObject d) => (bool)d.GetValue(KnobProperty);

    /// <summary>Привязывается к IsPressed тумблера: бегунок вытягивается в сторону хода.</summary>
    public static readonly DependencyProperty KnobDownProperty = DependencyProperty.RegisterAttached(
        "KnobDown", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnKnobDown));

    public static void SetKnobDown(DependencyObject d, bool v) => d.SetValue(KnobDownProperty, v);
    public static bool GetKnobDown(DependencyObject d) => (bool)d.GetValue(KnobDownProperty);

    /// <summary>Сколько бегунок проходит от «выкл» до «вкл».</summary>
    public static readonly DependencyProperty KnobTravelProperty = DependencyProperty.RegisterAttached(
        "KnobTravel", typeof(double), typeof(Fx), new PropertyMetadata(20.0));

    public static void SetKnobTravel(DependencyObject d, double v) => d.SetValue(KnobTravelProperty, v);
    public static double GetKnobTravel(DependencyObject d) => (double)d.GetValue(KnobTravelProperty);

    private static KnobState Knob(FrameworkElement el)
    {
        if (el.GetValue(KnobStateProperty) is KnobState s) return s;
        s = new KnobState();
        var g = new TransformGroup();
        g.Children.Add(s.St);
        g.Children.Add(s.Tt);
        el.RenderTransform = g;
        el.SetValue(KnobStateProperty, s);
        return s;
    }

    private static void KnobPose(FrameworkElement el, KnobState s)
    {
        // Вытянутый бегунок растёт от «своего» края: выключен — вправо, включён — влево. Положение считается одной формулой
        // от хода P, поэтому при отпускании ничего не перескакивает (раньше точка растяжения менялась скачком — бегунок дёргался).
        var travel = GetKnobTravel(el);
        var w = el.ActualWidth > 0 ? el.ActualWidth : el.Width;
        var pc = Math.Clamp(s.P, 0, 1);
        s.St.CenterX = 0;
        s.St.ScaleX = s.Stretch;
        s.Tt.X = travel * s.P - (s.Stretch - 1) * w * pc;
        el.Opacity = 0.8 + 0.2 * pc;
    }

    private static void OnKnob(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var s = Knob(el);
        s.On = (bool)e.NewValue;
        var to = s.On ? 1.0 : 0.0;
        if (!Live(d))
        {
            Anim.Stop(s, "p");
            s.P = to;
            KnobPose(el, s);
            return;
        }
        var from = s.P;
        Anim.Run(s, "p", 300, k =>
        {
            s.P = from + (to - from) * k;
            KnobPose(el, s);
        }, Motion.UiEaseDamped());
    }

    private static void OnKnobDown(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var s = Knob(el);
        s.Down = (bool)e.NewValue;
        var to = s.Down && Motion.Enabled ? 1.28 : 1.0; // без анимаций — без деформаций
        if (!Live(d))
        {
            Anim.Stop(s, "stretch");
            s.Stretch = to;
            KnobPose(el, s);
            return;
        }
        var from = s.Stretch;
        Anim.Run(s, "stretch", s.Down ? 120 : 260, k =>
        {
            s.Stretch = from + (to - from) * k;
            KnobPose(el, s);
        }, s.Down ? Motion.QuickEase() : Motion.UiEaseDamped());
    }

    // ---------- Grow: точка ползунка ----------
    public static readonly DependencyProperty GrowHoverProperty = DependencyProperty.RegisterAttached(
        "GrowHover", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnGrow));

    public static void SetGrowHover(DependencyObject d, bool v) => d.SetValue(GrowHoverProperty, v);
    public static bool GetGrowHover(DependencyObject d) => (bool)d.GetValue(GrowHoverProperty);

    public static readonly DependencyProperty GrowDragProperty = DependencyProperty.RegisterAttached(
        "GrowDrag", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnGrow));

    public static void SetGrowDrag(DependencyObject d, bool v) => d.SetValue(GrowDragProperty, v);
    public static bool GetGrowDrag(DependencyObject d) => (bool)d.GetValue(GrowDragProperty);

    private static void OnGrow(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var to = !Motion.Enabled ? 1.0 : GetGrowDrag(d) ? 1.3 : GetGrowHover(d) ? 1.15 : 1.0;
        var st = OwnScale(el);
        if (!Live(d))
        {
            Anim.Stop(st, ScaleTransform.ScaleXProperty);
            st.ScaleX = st.ScaleY = to;
            return;
        }
        var from = st.ScaleX;
        Anim.Run(st, ScaleTransform.ScaleXProperty, to > from ? 160 : 260,
            k => st.ScaleX = st.ScaleY = from + (to - from) * k, Motion.UiEaseDamped());
    }

    // ---------- Mark: метка выбранного раздела ----------
    public static readonly DependencyProperty MarkProperty = DependencyProperty.RegisterAttached(
        "Mark", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnMark));

    public static void SetMark(DependencyObject d, bool v) => d.SetValue(MarkProperty, v);
    public static bool GetMark(DependencyObject d) => (bool)d.GetValue(MarkProperty);

    private static void OnMark(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var on = (bool)e.NewValue;
        var st = OwnScale(el);
        if (!Live(d))
        {
            Anim.Stop(st, ScaleTransform.ScaleYProperty);
            Anim.Stop(el, UIElement.OpacityProperty);
            st.ScaleY = on ? 1 : 0;
            el.Opacity = on ? 1 : 0;
            return;
        }
        Anim.To(st, ScaleTransform.ScaleYProperty, on ? 1 : 0, on ? 300 : 160, on ? Motion.UiEaseDamped() : Motion.EaseIn());
        Anim.To(el, UIElement.OpacityProperty, on ? 1 : 0, on ? 120 : 160, Motion.EaseOut());
    }

    // ---------- Swell: «пузырь» под значком-приёмником ----------
    public static readonly DependencyProperty SwellProperty = DependencyProperty.RegisterAttached(
        "Swell", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnSwell));

    public static void SetSwell(DependencyObject d, bool v) => d.SetValue(SwellProperty, v);
    public static bool GetSwell(DependencyObject d) => (bool)d.GetValue(SwellProperty);

    private static readonly BackEase SwellEase = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 });

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static void OnSwell(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var on = (bool)e.NewValue;
        var st = OwnScale(el);
        double scale = on ? 1.42 : 0.9, op = on ? 1 : 0;
        if (!Live(d))
        {
            Anim.Stop(st, ScaleTransform.ScaleXProperty);
            Anim.Stop(el, UIElement.OpacityProperty);
            st.ScaleX = st.ScaleY = scale;
            el.Opacity = op;
            return;
        }
        var from = st.ScaleX;
        Anim.Run(st, ScaleTransform.ScaleXProperty, on ? 380 : 200, k => st.ScaleX = st.ScaleY = from + (scale - from) * k,
            on ? SwellEase : Motion.EaseOut());
        Anim.To(el, UIElement.OpacityProperty, op, on ? 180 : 150, Motion.EaseOut());
    }

    // ---------- PopIn: всплывающее появляется ----------
    public static readonly DependencyProperty PopInProperty = DependencyProperty.RegisterAttached(
        "PopIn", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnPopIn));

    public static void SetPopIn(DependencyObject d, bool v) => d.SetValue(PopInProperty, v);
    public static bool GetPopIn(DependencyObject d) => (bool)d.GetValue(PopInProperty);

    private static void OnPopIn(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.IsVisibleChanged -= PopInVisible;
        if ((bool)e.NewValue) el.IsVisibleChanged += PopInVisible;
    }

    private static void PopInVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement el || !(bool)e.NewValue) return;
        PopIn(el);
    }

    /// <summary>Появиться: проявиться и чуть «доехать» (с лёгким перелётом, как всё остальное).</summary>
    public static void PopIn(FrameworkElement el, double rise = 5)
    {
        var tt = OwnShift(el);
        if (!Motion.Enabled || tt is null)
        {
            el.Opacity = 1;
            return;
        }
        Anim.To(el, UIElement.OpacityProperty, 1, 140, Motion.EaseOut(), from: 0);
        Anim.To(tt, TranslateTransform.YProperty, 0, Motion.UiMs * 0.7, Motion.UiEase(), from: -rise);
    }

    // ---------- появление «лесенкой» и плавная перестройка ----------
    /// <summary>
    /// Своё смещение элемента (для появления и Flip). null — у элемента уже есть чужое преобразование, его не трогаем.
    /// </summary>
    private static TranslateTransform? OwnShift(FrameworkElement el)
    {
        if (el.RenderTransform is TranslateTransform { IsFrozen: false } t) return t;
        var cur = el.RenderTransform;
        if (cur is not null && !ReferenceEquals(cur, Transform.Identity) && !(cur is MatrixTransform m && m.Matrix.IsIdentity)) return null;
        t = new TranslateTransform();
        el.RenderTransform = t;
        return t;
    }

    /// <summary>
    /// Строки появляются по очереди сверху вниз — как плитки «паровозиком»: каждая проявляется и поднимается на место
    /// с лёгким перелётом. Строки ниже видимой части (viewport) встают сразу — их всё равно не видно.
    /// </summary>
    public static void Enter(IEnumerable<FrameworkElement> items, FrameworkElement? viewport = null, double rise = 14)
    {
        var list = items.Where(i => i.Visibility == Visibility.Visible).ToList();
        if (!Motion.Enabled)
        {
            foreach (var el in list) Settle(el);
            return;
        }
        var bottom = viewport is { ActualHeight: > 0 } ? viewport.ActualHeight : double.PositiveInfinity;
        var shown = new List<FrameworkElement>();
        foreach (var el in list)
        {
            var y = viewport is not null && el.IsVisible ? SafeY(el, viewport) : 0;
            if (y > bottom) Settle(el);
            else shown.Add(el);
        }
        var step = shown.Count > 1 ? Math.Min(Motion.TrainStepMs, Motion.TrainMaxMs / (shown.Count - 1)) : 0;
        for (var i = 0; i < shown.Count; i++)
        {
            var el = shown[i];
            var tt = OwnShift(el);
            var delay = i * step;
            Anim.To(el, UIElement.OpacityProperty, 1, 260, Motion.EaseOut(), from: 0, delay: delay);
            if (tt is not null) Anim.To(tt, TranslateTransform.YProperty, 0, Motion.UiMs, Motion.UiEase(), from: rise, delay: delay);
        }
    }

    /// <summary>Сразу на место (анимации появления, если шла, — в конец).</summary>
    private static void Settle(FrameworkElement el)
    {
        Anim.Stop(el, UIElement.OpacityProperty);
        el.Opacity = 1;
        if (el.CacheMode is BitmapCache) el.CacheMode = null; // картинка от прерванного FadeIn не остаётся навсегда
        if (el.RenderTransform is TranslateTransform { IsFrozen: false } t)
        {
            Anim.Stop(t, TranslateTransform.YProperty);
            t.Y = 0;
        }
    }

    private static double SafeY(FrameworkElement el, FrameworkElement relativeTo)
    {
        try { return el.TranslatePoint(new Point(), relativeTo).Y; }
        catch { return 0; }
    }

    /// <summary>
    /// Плавная перестройка панели: change() что-то показывает/прячет, а соседи не прыгают на новые места, а доезжают
    /// (тем же ходом, что плитки), появившееся — проявляется «лесенкой». Одна перестройка раскладки, дальше — только
    /// смещения отрисовки (раскладку на ходу не трогаем).
    /// </summary>
    public static void Flip(Panel panel, Action change)
    {
        if (!Live(panel))
        {
            change();
            return;
        }
        var before = new Dictionary<UIElement, double>();
        foreach (UIElement c in panel.Children)
            if (c.IsVisible && c is FrameworkElement fe) before[c] = SafeY(fe, panel) - ((fe.RenderTransform as TranslateTransform)?.Y ?? 0);
        change();
        panel.UpdateLayout();
        var appeared = new List<FrameworkElement>();
        var moved = 0;
        foreach (UIElement c in panel.Children)
        {
            if (c is not FrameworkElement fe || !c.IsVisible) continue;
            if (!before.TryGetValue(c, out var oldY))
            {
                appeared.Add(fe);
                continue;
            }
            var dy = oldY - (SafeY(fe, panel) - ((fe.RenderTransform as TranslateTransform)?.Y ?? 0));
            if (Math.Abs(dy) < 0.5) continue;
            if (OwnShift(fe) is not { } tt) continue;
            var cur = tt.Y + dy; // откуда смотрится сейчас (с учётом ещё идущего хода)
            Anim.To(tt, TranslateTransform.YProperty, 0, Motion.UiMs, Motion.UiEase(), from: cur, delay: Math.Min(moved++ * 12, 120));
        }
        if (appeared.Count > 0) Enter(appeared, rise: 10);
    }

    /// <summary>Показать/спрятать элемент панели с плавной перестройкой соседей (Flip).</summary>
    public static void SetVisible(UIElement el, bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        if (el.Visibility == v) return;
        if (VisualTreeHelper.GetParent(el) is Panel p) Flip(p, () => el.Visibility = v);
        else el.Visibility = v;
    }

    /// <summary>Вся страница/блок появляется: проявляется и чуть поднимается (картинкой — без перерисовки содержимого).</summary>
    public static void FadeIn(FrameworkElement el, double rise = 8, double ms = 220)
    {
        var tt = OwnShift(el);
        if (!Motion.Enabled)
        {
            Settle(el);
            return;
        }
        el.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(el).DpiScaleX);
        Anim.To(el, UIElement.OpacityProperty, 1, ms, Motion.EaseOut(), () => el.CacheMode = null, from: 0);
        if (tt is not null) Anim.To(tt, TranslateTransform.YProperty, 0, Motion.UiMs, Motion.UiEase(), from: rise);
    }
}
