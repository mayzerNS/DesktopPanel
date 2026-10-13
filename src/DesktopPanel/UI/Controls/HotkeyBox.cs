using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DesktopPanel.Core.Hotkeys;
using DesktopPanel.Core.Localization;
using DesktopPanel.UI.Theme;

namespace DesktopPanel.UI.Controls;

/// <summary>
/// Поле записи сочетания: щёлкните и нажмите сочетание клавиш или кнопку мыши с Ctrl/Alt/Shift/Win (нажатие колеса,
/// боковые, прокрутка). Нажатое сочетание сразу не применяется: слева от поля выскакивает плашка — свободно ли оно и
/// галочка «применить» (или Enter). Щелчок мимо, Esc или уход фокуса — отмена, остаётся прежнее. Backspace/Delete —
/// убрать (если CanClear), тоже через галочку.
/// Пока поле в фокусе, программа снимает свои сочетания (RecordingChanged), чтобы их можно было переназначить.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    private Hotkey _hotkey;
    private Hotkey _before;
    private Hotkey? _pending; // нажатое, ещё не подтверждённое
    private bool _committed;
    private object? _tag; // подсказка поля вне записи (её Tag на время записи заменяет «Нажмите сочетание…»)
    private Window? _window;
    private ConfirmAdorner? _confirm;

    /// <summary>true — началась запись, false — закончилась.</summary>
    public event Action<bool>? RecordingChanged;

    /// <summary>Пользователь подтвердил новое сочетание (галочкой или Enter).</summary>
    public event Action<Hotkey>? HotkeyChosen;

    /// <summary>
    /// Добавлять клавишу Win к сочетанию, даже если её не держат. Почти все сочетания Win+буква Windows
    /// перехватывает раньше программ, поэтому их удобнее собирать так: включить «Win» и нажать букву.
    /// </summary>
    public bool ForceWin { get; set; }

    /// <summary>Можно убрать сочетание (Backspace/Delete) — у «Открывать панель» нельзя.</summary>
    public bool CanClear { get; set; } = true;

    /// <summary>
    /// Проверка нажатого сочетания до подтверждения: подпись на плашке, её цвет и можно ли применить.
    /// Без неё — «Не занято».
    /// </summary>
    public Func<Hotkey, (string Text, Color Color, bool Ok)>? Check { get; set; }

    /// <summary>Слева от чего выскакивает плашка (по умолчанию — само поле; у главного сочетания — вместе с кнопкой «Win»).</summary>
    public FrameworkElement? ConfirmAnchor { get; set; }

    /// <summary>Плашка справа от поля (в мастере поле стоит у левого края страницы).</summary>
    public bool ConfirmRight { get; set; }

    /// <summary>Поле, в котором сейчас идёт запись (для Win+клавиши, пойманной HotkeyTakeover).</summary>
    public static HotkeyBox? Current { get; private set; }

    /// <summary>Win+клавиша, которую Windows не дала бы полю (поймал хук «Забирать у Windows» на время записи).</summary>
    public void Inject(Hotkey hk)
    {
        if (!IsKeyboardFocused) return;
        SetPending(new Hotkey(hk.Modifiers | (ForceWin ? ModifierKeys.Windows : ModifierKeys.None), hk.Key));
    }

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ContextMenu = null;
    }

    public Hotkey Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            Text = value.Display;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _before = _hotkey;
        _pending = null;
        _committed = false;
        _tag = Tag;
        Text = "";
        Tag = Loc.T("settings.hotkey.press");
        _window = Window.GetWindow(this);
        if (_window is not null) _window.PreviewMouseDown += Window_PreviewMouseDown;
        Current = this;
        RecordingChanged?.Invoke(true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!_committed) Hotkey = _before; // не подтвердили — как было
        _pending = null;
        Tag = _tag; // подсказка «Нажмите сочетание…» не остаётся в пустом поле после отмены
        if (_window is not null) _window.PreviewMouseDown -= Window_PreviewMouseDown;
        _window = null;
        HideConfirm();
        if (Current == this) Current = null;
        RecordingChanged?.Invoke(false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = CurrentMods();

        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            MoveFocusAway(); // отмена
            return;
        }
        if (key is Key.Return && mods == ModifierKeys.None)
        {
            if (_confirm?.Ok == true) Commit();
            return;
        }
        if (key is Key.Back or Key.Delete && mods == ModifierKeys.None && CanClear)
        {
            SetPending(default); // убрать — тоже через галочку
            return;
        }
        if (Hotkey.IsModifierKey(key))
        {
            // пока зажаты только модификаторы — показываем их, ждём основную клавишу
            Text = mods != ModifierKeys.None ? ModifiersText(mods) + "+…" : PendingText();
            return;
        }
        if (mods == ModifierKeys.None && !Hotkey.CanBeAlone(key))
        {
            // одиночная буква перестала бы печататься во всей системе — так нельзя
            Refuse();
            return;
        }
        SetPending(new Hotkey(mods, key));
    }

    /// <summary>Отпустили модификаторы, так и не нажав клавишу, — в поле снова выбранное сочетание.</summary>
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!Hotkey.IsModifierKey(key)) return;
        var mods = CurrentMods() & ~(ForceWin ? ModifierKeys.Windows : ModifierKeys.None);
        Text = mods != ModifierKeys.None ? ModifiersText(CurrentMods()) + "+…" : PendingText();
    }

    /// <summary>
    /// Щелчок, пока идёт запись. По полю и плашке — их дело. Колесо и боковые кнопки где угодно — запись сочетания
    /// (нужен Ctrl/Alt/Shift/Win: сами по себе эти кнопки нужны в играх и браузере). Любой другой щелчок мимо — отмена.
    /// </summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var button = e.ChangedButton switch
        {
            MouseButton.Middle => MouseKey.Middle,
            MouseButton.XButton1 => MouseKey.X1,
            MouseButton.XButton2 => MouseKey.X2,
            _ => MouseKey.None,
        };
        if (button != MouseKey.None)
        {
            e.Handled = true;
            var mods = CurrentMods();
            if (mods == ModifierKeys.None) Refuse();
            else SetPending(new Hotkey(mods, Key.None, button));
            return;
        }
        if (e.OriginalSource is DependencyObject d && (IsInside(d, this) || _confirm is not null && IsInside(d, _confirm))) return;
        MoveFocusAway(); // щелчок мимо — отмена
    }

    private static bool IsInside(DependencyObject d, DependencyObject root)
    {
        for (var x = d; x is not null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (ReferenceEquals(x, root)) return true;
        return false;
    }

    /// <summary>Прокрутка колеса, пока идёт запись: с модификатором — сочетание, без — прокрутка страницы как обычно.</summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        var mods = CurrentMods();
        if (!IsKeyboardFocused || mods == ModifierKeys.None)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }
        e.Handled = true;
        SetPending(new Hotkey(mods, Key.None, e.Delta > 0 ? MouseKey.WheelUp : MouseKey.WheelDown));
    }

    private ModifierKeys CurrentMods()
    {
        var mods = Keyboard.Modifiers;
        if (ForceWin || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
        return mods;
    }

    private string PendingText() => _pending is { } p ? p.Display : "";

    /// <summary>Нажали сочетание — показываем его и плашку: свободно ли, можно ли применить.</summary>
    private void SetPending(Hotkey hk)
    {
        _pending = hk;
        Text = hk.Display;
        Tag = hk.IsEmpty ? Loc.T("hotkey.none") : Loc.T("settings.hotkey.press");
        var (text, color, ok) = hk.IsEmpty ? (Loc.T("hotkey.clear"), StatusDot.Yellow, true)
            : Check?.Invoke(hk) ?? (Loc.T("hotkey.free"), StatusDot.Green, true);
        ShowConfirm(text, color, ok);
    }

    /// <summary>Так нельзя (буква без модификатора, кнопка мыши без Ctrl/Alt/Shift/Win): объясняем на плашке, без галочки.</summary>
    private void Refuse()
    {
        _pending = null;
        Text = "";
        Tag = Loc.T("settings.hotkey.press");
        ShowConfirm(Loc.T("settings.hotkey.need_modifier"), StatusDot.Red, false);
    }

    private void Commit()
    {
        if (_pending is not { } hk) return;
        _committed = true;
        Hotkey = hk;
        HideConfirm();
        HotkeyChosen?.Invoke(hk);
        MoveFocusAway();
    }

    private void ShowConfirm(string text, Color color, bool ok)
    {
        var anchor = ConfirmAnchor ?? this;
        if (_confirm is null || _confirm.Closing)
        {
            if (AdornerLayer.GetAdornerLayer(anchor) is not { } layer) return;
            _confirm?.RemoveNow();
            _confirm = new ConfirmAdorner(anchor, layer, Commit, ConfirmRight);
            layer.Add(_confirm);
            _confirm.Set(text, color, ok);
            _confirm.Appear();
            return;
        }
        _confirm.Set(text, color, ok);
    }

    private void HideConfirm()
    {
        _confirm?.Close();
        _confirm = null;
    }

    private static string ModifiersText(ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        return string.Join("+", parts);
    }

    private void MoveFocusAway()
    {
        var scope = FocusManager.GetFocusScope(this);
        FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
    }

    /// <summary>
    /// Плашка слева от поля: подпись (цветом — свободно / занято) и галочка «применить». Выскакивает из поля с лёгким
    /// перелётом (ход Motion.UiEase, как у всего интерфейса), гаснет и убирается. Галочка не берёт фокус — запись не прерывается.
    /// </summary>
    private sealed class ConfirmAdorner : Adorner
    {
        private const double Gap = 8;
        private readonly AdornerLayer _layer;
        private readonly Border _plate;
        private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        private readonly Button _ok;
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly TranslateTransform _shift = new();
        private readonly SolidColorBrush _textBrush = new(StatusDot.Green);
        private readonly bool _right;

        private bool _shown;

        public bool Ok { get; private set; }
        public bool Closing { get; private set; }

        public ConfirmAdorner(FrameworkElement anchor, AdornerLayer layer, Action commit, bool right) : base(anchor)
        {
            _layer = layer;
            _right = right;
            _text.Foreground = _textBrush;
            _ok = new Button { Content = "", Focusable = false, Width = 26, Height = 26, Margin = new Thickness(8, 0, 0, 0),
                FontSize = 13 };
            _ok.SetResourceReference(FontFamilyProperty, "GlyphFont");
            // без своей подложки: подсветка наведения (радиус 6) вписана в плашку (радиус 8, отступ 3) — края идут вровень
            _ok.SetResourceReference(StyleProperty, "GhostButton");
            _ok.Click += (_, _) => commit();
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(_text);
            row.Children.Add(_ok);
            _plate = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 0, 3, 0),
                Height = 32, // как поле рядом
                Child = row,
                RenderTransformOrigin = new Point(right ? 0 : 1, 0.5), // растёт из поля
            };
            // чуть светлее окружения, как поля и кнопки панели (стекло под ней видно), а не тёмная подсказка
            _plate.SetResourceReference(Border.BackgroundProperty, "ControlHoverBrush");
            _plate.SetResourceReference(Border.BorderBrushProperty, "PopupEdgeBrush");
            var tg = new TransformGroup();
            tg.Children.Add(_scale);
            tg.Children.Add(_shift);
            _plate.RenderTransform = tg;
            AddVisualChild(_plate);
            _plate.Opacity = 0;
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _plate;

        protected override Size MeasureOverride(Size constraint)
        {
            _plate.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size size)
        {
            var d = _plate.DesiredSize;
            _plate.Arrange(new Rect(_right ? size.Width + Gap : -d.Width - Gap, (size.Height - d.Height) / 2, d.Width, d.Height));
            return size;
        }

        public void Set(string text, Color color, bool ok)
        {
            var changed = _text.Text != text || Ok != ok;
            _text.Text = text;
            Ok = ok;
            _ok.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            _plate.Padding = new Thickness(10, 0, ok ? 3 : 10, 0);
            if (!_shown) _textBrush.Color = color;
            else if (_textBrush.Color != color) Anim.Color(_textBrush, color, Motion.HoverInMs, Motion.EaseOut());
            if (changed && _shown) Nudge();
        }

        public void Appear()
        {
            if (Closing || _shown) return;
            _shown = true;
            if (!Motion.Enabled)
            {
                _plate.Opacity = 1;
                return;
            }
            Anim.To(_plate, OpacityProperty, 1, 140, Motion.EaseOut(), from: 0);
            Anim.Run(_scale, ScaleTransform.ScaleXProperty, Motion.UiMs * 0.8, k => _scale.ScaleX = _scale.ScaleY = 0.6 + 0.4 * k,
                Motion.UiEase());
            Anim.Run(_shift, TranslateTransform.XProperty, Motion.UiMs * 0.8, k => _shift.X = (_right ? -14 : 14) * (1 - k), Motion.UiEase());
        }

        /// <summary>Подпись сменилась (нажали другое сочетание) — плашка чуть «вздрагивает», чтобы было видно, что это новое.</summary>
        private void Nudge()
        {
            if (!Motion.Enabled) return;
            Anim.Run(_scale, ScaleTransform.ScaleXProperty, Motion.UiMs * 0.6, k => _scale.ScaleX = _scale.ScaleY = 0.94 + 0.06 * k,
                Motion.SpringEase());
        }

        public void Close()
        {
            if (Closing) return;
            Closing = true;
            IsHitTestVisible = false;
            if (!Motion.Enabled || !IsVisible)
            {
                RemoveNow();
                return;
            }
            Anim.Run(_scale, ScaleTransform.ScaleXProperty, 160, k => _scale.ScaleX = _scale.ScaleY = 1 - 0.15 * k, Motion.EaseIn());
            Anim.To(_plate, OpacityProperty, 0, 160, Motion.EaseIn(), done: RemoveNow);
        }

        public void RemoveNow()
        {
            Anim.Stop(_plate, OpacityProperty);
            Anim.Stop(_scale, ScaleTransform.ScaleXProperty);
            Anim.Stop(_shift, TranslateTransform.XProperty);
            _layer.Remove(this);
        }
    }
}
