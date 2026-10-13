using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopPanel.Core.Hotkeys;
using DesktopPanel.Core.Localization;
using DesktopPanel.UI.Theme;

namespace DesktopPanel.UI.Wizard;

/// <summary>
/// Мастер первого запуска (можно запустить снова из настроек): язык, нужен ли Zapret,
/// сочетание для панели (с проверкой, свободно ли оно), автозапуск и вид «уголка».
/// Всё применяется сразу; «Пропустить» оставляет настройки по умолчанию.
/// </summary>
public partial class WizardWindow : Window
{
    private readonly UIElement[] _pages;
    private int _step;
    private bool? _zapret;
    private Hotkey? _busy; // выбранное, но занятое сочетание (его можно забрать у Windows)

    public WizardWindow()
    {
        InitializeComponent();
        _pages = new UIElement[] { PageLanguage, PageZapret, PageHotkey, PageAutostart, PageDone };
        SourceInitialized += (_, _) =>
        {
            Glass.Prepare(this);
            Glass.Apply(this);
        };
        OuterFrame.CornerRadius = new CornerRadius(Corners.WindowDip); // как скругляет окно Windows 11
        Tint.Opacity = Glass.DarkOpacity(App.Instance.Config.Darkness);

        foreach (var lang in Loc.All)
        {
            var b = new ToggleButton
            {
                Content = Loc.T("lang." + lang),
                MinWidth = 140,
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = Loc.Available.Contains(lang),
                IsChecked = lang == Loc.Current,
                Tag = lang,
            };
            b.SetResourceReference(StyleProperty, "PanelToggle");
            b.Click += (_, _) =>
            {
                foreach (ToggleButton x in LanguageButtons.Children) x.IsChecked = x == b;
                App.Instance.Config.Language = lang;
                App.Instance.SaveConfig();
            };
            LanguageButtons.Children.Add(b);
        }

        var cfg = App.Instance.Config;
        _zapret = cfg.ZapretEnabled ? true : null;
        AutostartSwitch.IsChecked = cfg.Autostart;
        TakeoverSwitch.IsChecked = cfg.HotkeyTakeover;
        HotkeyInput.Hotkey = Hotkey.Parse(cfg.Hotkey);
        WinToggle.IsChecked = HotkeyInput.ForceWin = HotkeyInput.Hotkey.Modifiers.HasFlag(ModifierKeys.Windows);
        HotkeyInput.RecordingChanged += recording => App.Instance.SuspendHotkeys(recording);
        HotkeyInput.HotkeyChosen += OnHotkeyChosen;
        HotkeyInput.CanClear = false; // без сочетания панель не открыть
        HotkeyInput.ConfirmRight = true; // поле у левого края страницы — плашка справа
        HotkeyInput.Check = hk => App.Instance.HotkeyState(hk) switch
        {
            HotkeyManager.State.Busy => (Loc.T("hotkey.busy"), Controls.StatusDot.Red, true),
            HotkeyManager.State.BusyTakeover or HotkeyManager.State.OursShared => (Loc.T("hotkey.takeover"), Controls.StatusDot.Yellow, true),
            _ => (Loc.T("hotkey.free"), Controls.StatusDot.Green, true),
        };
        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        var changed = _step != Math.Clamp(step, 0, _pages.Length - 1);
        _step = Math.Clamp(step, 0, _pages.Length - 1);
        for (var i = 0; i < _pages.Length; i++) _pages[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        if (changed && IsVisible && _pages[_step] is System.Windows.Controls.Panel panel)
            DesktopPanel.UI.Theme.Fx.Enter(panel.Children.OfType<FrameworkElement>());
        StepText.Text = Loc.T("wizard.step", _step + 1, _pages.Length);
        BackButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Hidden;
        SkipButton.Visibility = _step < _pages.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = Loc.T(_step == _pages.Length - 1 ? "wizard.finish" : "wizard.next");
        if (_pages[_step] == PageZapret)
        {
            ZapretYes.IsChecked = _zapret == true;
            ZapretNo.IsChecked = _zapret == false;
        }
        if (_pages[_step] == PageHotkey) UpdateHotkeyStatus();
        if (_pages[_step] == PageDone) DoneText.Text = Summary();
    }

    private string Summary()
    {
        var cfg = App.Instance.Config;
        var lines = new List<string>
        {
            App.Instance.HotkeyRegistered ? Loc.T("wizard.done.hotkey", cfg.Hotkey) : Loc.T("wizard.done.hotkey_busy"),
            Loc.T(cfg.ZapretEnabled ? "wizard.done.zapret_on" : "wizard.done.zapret_off"),
            Loc.T(cfg.Autostart ? "wizard.done.autostart_on" : "wizard.done.autostart_off"),
            Loc.T("wizard.done.again"),
        };
        return "• " + string.Join("\n• ", lines);
    }

    // ---------- Zapret ----------
    private void ZapretChoice_Click(object sender, RoutedEventArgs e)
    {
        _zapret = sender == ZapretYes;
        ZapretYes.IsChecked = _zapret == true;
        ZapretNo.IsChecked = _zapret == false;
    }

    // ---------- сочетание ----------
    private void WinToggle_Click(object sender, RoutedEventArgs e)
    {
        HotkeyInput.ForceWin = WinToggle.IsChecked == true;
        HotkeyInput.Focus();
    }

    // занятое тоже можно выбрать: ниже его можно забрать у Windows (Takeover_Click)
    private void OnHotkeyChosen(Hotkey hk)
    {
        _busy = App.Instance.TryChangeHotkey(hk) ? null : hk;
        HotkeyInput.Hotkey = hk;
        WinToggle.IsChecked = HotkeyInput.ForceWin = HotkeyInput.Hotkey.Modifiers.HasFlag(ModifierKeys.Windows);
        UpdateHotkeyStatus();
    }

    private void UpdateHotkeyStatus()
    {
        var hk = _busy ?? Hotkey.Parse(App.Instance.Config.Hotkey);
        var ok = _busy is null && App.Instance.HotkeyRegistered;
        var shared = ok && App.Instance.HotkeyShared;
        HotkeyStatus.Text = !ok ? Loc.T("wizard.hotkey.busy", hk.ToString())
            : shared ? Loc.T("hotkey.shared", hk.ToString())
            : Loc.T("wizard.hotkey.ok", hk.ToString());
        HotkeyStatus.Foreground = ok && !shared ? new SolidColorBrush(Color.FromRgb(0x8C, 0xE0, 0x9C))
                                                : new SolidColorBrush(Color.FromRgb(0xF0, 0xB0, 0x70));
    }

    private void Takeover_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.SetHotkeyTakeover(TakeoverSwitch.IsChecked == true);
        if (_busy is { } hk && App.Instance.TryChangeHotkey(hk)) _busy = null;
        UpdateHotkeyStatus();
    }

    // ---------- навигация ----------
    private void Apply()
    {
        var cfg = App.Instance.Config;
        if (_zapret is { } z && z != cfg.ZapretEnabled) App.Instance.Zapret.SetEnabled(z);
        if ((AutostartSwitch.IsChecked == true) != cfg.Autostart) App.Instance.SetAutostart(AutostartSwitch.IsChecked == true);
        App.Instance.SaveConfig();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        Apply();
        if (_step == _pages.Length - 1) Finish();
        else ShowStep(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void Skip_Click(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        Apply();
        App.Instance.Config.FirstRunDone = true;
        App.Instance.SaveConfig();
        Close();
        App.Instance.Overlay?.ShowOverlay();
    }

    /// <summary>
    /// Тянут за шапку — своим ходом, как панель и браузер (WindowDrag): не за край экрана и без Aero Snap
    /// (DragMove у верхнего края предлагал развернуть окно на весь экран).
    /// </summary>
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var p = PointToScreen(e.GetPosition(this));
        WindowDrag.Start(hwnd, r => WindowDrag.Clamp(r, WindowDrag.WorkAreaOf(r)),
            from: new Core.Native.NativeMethods.POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not Controls.HotkeyBox)
        {
            e.Handled = true;
            Finish();
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
