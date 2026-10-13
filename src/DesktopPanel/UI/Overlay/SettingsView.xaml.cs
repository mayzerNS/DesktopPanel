using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopPanel.Core.Config;
using DesktopPanel.Core.Hotkeys;
using DesktopPanel.Core.Localization;
using DesktopPanel.Features.Pip;
using DesktopPanel.UI.Controls;
using DesktopPanel.UI.Theme;

namespace DesktopPanel.UI.Overlay;

/// <summary>Страница «Настройки»: слева разделы (как в «Параметрах» Windows), справа — их содержимое.</summary>
public partial class SettingsView : UserControl
{
    /// <summary>Разделы: ключ, значок, панель с содержимым.</summary>
    private readonly (string Key, string Glyph, FrameworkElement Panel)[] _sections;

    private readonly Dictionary<string, ToggleButton> _navButtons = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Dictionary<string, HotkeyBox> _pipBoxes = new();
    private bool _loading;
    private Hotkey? _busy; // выбранное, но занятое сочетание панели
    private DispatcherTimer? _resetConfirm;

    public SettingsView()
    {
        InitializeComponent();
        _sections = new (string, string, FrameworkElement)[]
        {
            ("look", "", SecLook),
            ("hotkeys", "", SecHotkeys),
            ("panel", "", SecPanel),
            ("desktop", "", SecDesktop),
            ("pip", "\uF6FA", SecPip),
            ("telegram", "", SecTelegram),
            ("zapret", "", SecZapret),
            ("system", "", SecSystem),
        };
        foreach (var (key, glyph, _) in _sections)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var g = new TextBlock { Text = glyph, FontSize = 15, Width = 28, VerticalAlignment = VerticalAlignment.Center };
            g.SetResourceReference(TextBlock.FontFamilyProperty, "GlyphFont");
            sp.Children.Add(g);
            sp.Children.Add(new TextBlock { Text = Loc.T("settings.section." + key), VerticalAlignment = VerticalAlignment.Center });
            var b = new ToggleButton { Content = sp };
            b.SetResourceReference(StyleProperty, "NavToggle");
            var k = key;
            b.Click += (_, _) => ShowSection(k);
            _navButtons[key] = b;
            Nav.Children.Add(b);
        }

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            App.Instance.SaveConfig();
        };
        HotkeyInput.RecordingChanged += recording =>
        {
            App.Instance.SuspendHotkeys(recording);
            if (!recording) UpdateShortcutDots();
        };
        HotkeyInput.HotkeyChosen += OnHotkeyChosen;
        HotkeyInput.Check = hk => CheckShortcut(hk, "main");
        HotkeyInput.ConfirmAnchor = (FrameworkElement)HotkeyInput.Parent; // плашка — левее точки и кнопки «Win»

        BuildAccentSwatches();
        TipsSeg.SetItems(new[]
        {
            (Theme.Tips.Full, Loc.T("settings.tips.full")), (Theme.Tips.Min, Loc.T("settings.tips.minimal")),
            (Theme.Tips.Off, Loc.T("settings.tips.off")),
        });
        TipsSeg.Picked += SetTipsMode;
        PipAspectSeg.SetItems(new[] { ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9") });
        PipAspectSeg.Picked += a =>
        {
            var cfg = App.Instance.Config;
            cfg.PipAspect = a;
            cfg.PipHeight = (int)Math.Round(cfg.PipWidth / AppConfig.AspectRatio(a));
            App.Instance.SaveConfig();
            App.Instance.Pip?.ApplySize();
            Refresh();
        };
        HotkeyInput.CanClear = false; // без сочетания панель не открыть (только из трея)
        BuildShortcuts();
        BuildPipHotkeys();
        ShowSection("look");
    }

    private string _section = "";

    public void ShowSection(string key)
    {
        var changed = _section != key;
        _section = key;
        foreach (var (k, _, panel) in _sections) panel.Visibility = k == key ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (k, b) in _navButtons) b.IsChecked = k == key;
        if (changed && key is "hotkeys" or "pip") UpdateShortcutDots();
        Scroll.ScrollToTop();
        if (changed && IsVisible) Enter();
    }

    /// <summary>Открытый раздел (его строки).</summary>
    private Panel Section => (Panel)_sections.First(x => x.Key == _section).Panel;

    /// <summary>Строки раздела появляются «лесенкой» (переход на вкладку и смена раздела).</summary>
    public void Enter()
    {
        Scroll.UpdateLayout();
        Fx.Enter(Section.Children.OfType<FrameworkElement>(), Scroll);
    }

    /// <summary>Подтянуть текущие значения (при каждом открытии страницы).</summary>
    public void Refresh()
    {
        _loading = true;
        var cfg = App.Instance.Config;
        HotkeyInput.Hotkey = _busy ?? Hotkey.Parse(cfg.Hotkey);
        WinToggle.IsChecked = HotkeyInput.ForceWin = HotkeyInput.Hotkey.Modifiers.HasFlag(ModifierKeys.Windows);
        TakeoverSwitch.IsChecked = cfg.HotkeyTakeover;
        UpdateHotkeyStatus();

        AccentSystemSwitch.IsChecked = cfg.AccentColor.Length == 0;
        UpdateSwatches();
        TipsSeg.Selected = cfg.TipsMode;
        // частота монитора — запомненная (Motion сам перечитывает её при смене режима экрана): EnumDisplaySettings медленный
        FpsSlider.Value = Math.Clamp(cfg.AnimFps > 0 ? cfg.AnimFps : Theme.Motion.MonitorFps, Theme.Motion.MinFps, Theme.Motion.MaxFps);
        FpsValue.Text = (int)FpsSlider.Value + " " + Loc.T("settings.perf.unit");
        AnimSwitch.IsChecked = !cfg.AnimationsOff;
        DimFps(!cfg.AnimationsOff);
        TipsOpacitySlider.Value = cfg.TipsOpacity;
        TipsOpacityValue.Text = $"{cfg.TipsOpacity}%";
        var lvl = DesktopView.IconLevel(cfg.DesktopIconSize);
        IconSizeSlider.Value = lvl;
        IconSizeValue.Text = lvl.ToString();
        DarknessSlider.Value = cfg.Darkness;
        MusicSwitch.IsChecked = cfg.MusicVisualizer;
        DarknessValue.Text = $"{cfg.Darkness}%";

        GameMinimizeSwitch.IsChecked = cfg.GameMinimize;
        GameRestoreSwitch.IsChecked = cfg.GameRestore;

        SearchSwitch.IsChecked = cfg.SearchEnabled;
        SearchSystemSwitch.IsChecked = cfg.SearchSystem;
        SearchSystemSwitch.IsEnabled = cfg.SearchEnabled;
        RecycleSwitch.IsChecked = cfg.ShowRecycleBin;
        DoubleClickSwitch.IsChecked = cfg.DesktopDoubleClick;

        AutostartSwitch.IsChecked = cfg.Autostart;

        ZapretSwitch.IsChecked = cfg.ZapretEnabled;
        ZapretRow.Sub = Loc.T(cfg.ZapretEnabled ? "settings.zapret.sub_on" : "settings.zapret.sub_off");
        var zv = cfg.ZapretEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (ZapretOpenRow.Visibility != zv)
            Fx.Flip(SecZapret, () => ZapretOpenRow.Visibility = ZapretResetRow.Visibility = zv);

        RefreshTg();
        PipSwitch.IsChecked = cfg.PipEnabled;
        PipBody.IsEnabled = cfg.PipEnabled;
        var pipOpen = cfg.PipEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (PipOpenButton.Visibility != pipOpen)
        {
            PipOpenButton.Visibility = pipOpen;
            if (cfg.PipEnabled && PipOpenButton.IsVisible) Fx.PopIn(PipOpenButton, rise: 0);
        }
        var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1040);
        PipSizeSlider.Maximum = Math.Max(400, PipWindow.MaxWidth(new Core.Native.NativeMethods.RECT
            { Left = wa.Left, Top = wa.Top, Right = wa.Right, Bottom = wa.Bottom }, AppConfig.AspectRatio(cfg.PipAspect)));
        PipSizeSlider.Value = Math.Clamp(cfg.PipWidth, 320, PipSizeSlider.Maximum);
        PipSizeValue.Text = $"{cfg.PipWidth}×{cfg.PipHeight}";
        PipAspectSeg.Selected = cfg.PipAspect;
        PipPauseSwitch.IsChecked = cfg.PipAutoPause;
        PipFreeMemSwitch.IsChecked = cfg.PipFreeMemory;
        PipAutoSwitch.IsChecked = cfg.PipAutoInteract;
        PipDoubleSwitch.IsChecked = cfg.PipDoubleClick;
        foreach (var (action, box) in _pipBoxes)
            if (!box.IsKeyboardFocused) box.Hotkey = Hotkey.Parse(PipController.HotkeyOf(cfg, action));
        if (_section is "hotkeys" or "pip") UpdateShortcutDots(); // проверка «занято» — только когда раздел на экране
        _loading = false;
    }

    // ---------- вид ----------
    private void Look_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        var cfg = App.Instance.Config;
        cfg.Darkness = (int)Math.Round(DarknessSlider.Value);
        DarknessValue.Text = $"{cfg.Darkness}%";
        App.Instance.Overlay?.ApplyLook();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ---------- прокси для Telegram ----------
    public void RefreshTg()
    {
        var tg = App.Instance.TgProxy;
        var loading = _loading;
        _loading = true;
        TgSwitch.IsChecked = tg.Enabled;
        TgRow.Sub = tg.Error ?? tg.Notice ?? Loc.T(tg.Enabled ? "settings.tg.sub_on" : "settings.tg.sub_off");
        Fx.SetVisible(TgBody, tg.Enabled); // строки прокси выезжают, а не возникают
        TgStateRow.Sub = tg.Busy ?? Loc.T(tg.Running ? "settings.tg.running" : "settings.tg.stopped", tg.Port);
        TgToggleButton.Content = Loc.T(tg.Running ? "settings.tg.stop" : "settings.tg.start");
        TgToggleButton.IsEnabled = TgRestartButton.IsEnabled = TgSwitch.IsEnabled = tg.Busy is null;
        var restart = tg.Running ? Visibility.Visible : Visibility.Collapsed;
        if (TgRestartButton.Visibility != restart)
        {
            TgRestartButton.Visibility = restart;
            if (tg.Running && TgRestartButton.IsVisible) Fx.PopIn(TgRestartButton, rise: 0);
        }
        if (!TgPortBox.IsKeyboardFocusWithin) TgPortBox.Text = tg.Port.ToString();
        TgIpv6Switch.IsChecked = tg.Ipv6Off;
        TgIpv6Switch.IsEnabled = tg.Busy is null;
        TgVersionRow.Sub = tg.Version.Length > 0 ? Loc.T("settings.tg.version.sub", tg.Version) : Loc.T("settings.tg.not_downloaded");
        _loading = loading;
    }

    private void TgSwitch_Click(object sender, RoutedEventArgs e) =>
        App.Instance.TgProxy.SetEnabled(TgSwitch.IsChecked == true);

    private void TgToggle_Click(object sender, RoutedEventArgs e) => App.Instance.TgProxy.Toggle();
    private void TgIpv6_Click(object sender, RoutedEventArgs e) => App.Instance.TgProxy.SetIpv6Off(TgIpv6Switch.IsChecked == true);
    private void TgRestart_Click(object sender, RoutedEventArgs e) => App.Instance.TgProxy.Restart();
    private void TgConnect_Click(object sender, RoutedEventArgs e) => App.Instance.TgProxy.ConnectTelegram();
    private void TgUpdate_Click(object sender, RoutedEventArgs e) => App.Instance.TgProxy.CheckUpdates(force: true);

    private void TgCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(App.Instance.TgProxy.Link);
            TgRow.Sub = Loc.T("settings.tg.copied");
        }
        catch { }
    }

    private void TgLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Features.TgProxy.TgProxyService.LogFile))
            Core.Native.Shell.OpenLog(Features.TgProxy.TgProxyService.LogFile);
        else TgRow.Sub = Loc.T("settings.tg.no_log");
    }

    private void TgPort_Apply(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TgPortBox.Text.Trim(), out var port) && port is >= 1024 and <= 65535)
            App.Instance.TgProxy.SetPort(port);
        TgPortBox.Text = App.Instance.TgProxy.Port.ToString();
    }

    private void TgPort_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        TgPort_Apply(sender, e);
        Keyboard.ClearFocus();
    }

    // ---------- сетка значков ----------
    private DispatcherTimer? _gridTimer;

    /// <summary>Размеры сетки и значков: подписи — сразу, перерисовка значков — когда ползунок отпустили.</summary>
    private void Grid_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        var cfg = App.Instance.Config;
        var lvl = Math.Clamp((int)Math.Round(IconSizeSlider.Value), 1, DesktopView.IconSteps.Length);
        cfg.DesktopIconSize = DesktopView.IconSteps[lvl - 1];
        IconSizeValue.Text = lvl.ToString();
        DesktopView.ApplySizes(cfg); // сетка меняется на лету
        App.Instance.Overlay?.DesktopPage.FitColumnsNow();
        _gridTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _gridTimer.Stop();
        _gridTimer.Tick -= GridTimer_Tick;
        _gridTimer.Tick += GridTimer_Tick;
        _gridTimer.Start();
    }

    private void GridTimer_Tick(object? sender, EventArgs e)
    {
        _gridTimer?.Stop();
        App.Instance.SaveConfig();
        App.Instance.Overlay?.ResizeDesktopIcons(); // значки нужного разрешения
    }

    private void Music_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.Config.MusicVisualizer = MusicSwitch.IsChecked == true;
        App.Instance.SaveConfig();
        App.Instance.Overlay?.ApplyLook();
    }

    // ---------- цвет акцента ----------
    private void BuildAccentSwatches()
    {
        foreach (var hex in Theme.Accent.Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var b = new ToggleButton { Tag = hex, Background = new SolidColorBrush(color) };
            b.SetResourceReference(StyleProperty, "Swatch");
            b.Click += (_, _) => SetAccent(hex);
            AccentSwatches.Children.Add(b);
        }
    }

    private void UpdateSwatches()
    {
        var cur = App.Instance.Config.AccentColor;
        foreach (var b in AccentSwatches.Children.OfType<ToggleButton>())
            b.IsChecked = string.Equals((string)b.Tag, cur, StringComparison.OrdinalIgnoreCase);
    }

    private void SetAccent(string hex)
    {
        App.Instance.Config.AccentColor = hex;
        App.Instance.Config.AccentLastCustom = hex;
        App.Instance.SaveConfig();
        Theme.Accent.SetCustom(hex);
        AccentSystemSwitch.IsChecked = false;
        UpdateSwatches();
    }

    private void AccentSystem_Click(object sender, RoutedEventArgs e)
    {
        var system = AccentSystemSwitch.IsChecked == true;
        // выключили «системный» — свой цвет, что был выбран раньше; не выбирали (или его нет в палитре) — ближайший
        // к нынешнему системному (раньше всегда брался синий)
        var cfg = App.Instance.Config;
        var last = cfg.AccentLastCustom;
        var known = last.Length > 0 && Theme.Accent.Palette.Contains(last, StringComparer.OrdinalIgnoreCase);
        var hex = system ? "" : known ? last : Theme.Accent.Nearest(Theme.Accent.Color);
        if (!system) cfg.AccentLastCustom = hex;
        App.Instance.Config.AccentColor = hex;
        App.Instance.SaveConfig();
        Theme.Accent.SetCustom(hex);
        UpdateSwatches();
    }

    // ---------- подсказки ----------
    private void Fps_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FpsValue is null) return; // событие приходит во время загрузки разметки
        var fps = (int)Math.Round(e.NewValue);
        FpsValue.Text = fps + " " + Loc.T("settings.perf.unit");
        var cfg = App.Instance.Config;
        if (_loading || cfg.AnimFps == fps) return;
        cfg.AnimFps = fps;
        Theme.Motion.Configure(cfg.AnimFps, !cfg.AnimationsOff); // действует сразу: все анимации идут через FrameClock
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Anim_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Instance.Config;
        cfg.AnimationsOff = AnimSwitch.IsChecked != true;
        App.Instance.SaveConfig();
        Theme.Motion.Configure(cfg.AnimFps, !cfg.AnimationsOff);
        DimFps(!cfg.AnimationsOff);
    }

    /// <summary>Без анимаций предел кадров не нужен — строка гаснет целиком (и подпись, и ползунок).</summary>
    private void DimFps(bool on)
    {
        FpsSlider.IsEnabled = on;
        Theme.Fx.SetDim(FpsRow, on);
    }
    private void SetTipsMode(string mode)
    {
        var cfg = App.Instance.Config;
        cfg.TipsMode = mode;
        App.Instance.SaveConfig();
        Theme.Tips.Apply(cfg.TipsMode, cfg.TipsOpacity);
        TipsSeg.Selected = mode;
        App.Instance.Overlay?.UpdateHeader(); // подсказки Zapret и TG зависят от режима
    }

    private void TipsOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        var cfg = App.Instance.Config;
        cfg.TipsOpacity = (int)Math.Round(e.NewValue);
        TipsOpacityValue.Text = $"{cfg.TipsOpacity}%";
        Theme.Tips.Apply(cfg.TipsMode, cfg.TipsOpacity);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ---------- сочетание панели ----------
    private void WinToggle_Click(object sender, RoutedEventArgs e)
    {
        HotkeyInput.ForceWin = WinToggle.IsChecked == true;
        HotkeyInput.Focus(); // сразу ждём клавишу
    }

    private void OnHotkeyChosen(Hotkey hotkey)
    {
        if (OwnerOf(hotkey, "main") is { } other)
        {
            HotkeyRow.Sub = Loc.T("shortcut.dup", hotkey.Display, other);
            HotkeyInput.Hotkey = Hotkey.Parse(App.Instance.Config.Hotkey);
            return;
        }
        _busy = App.Instance.TryChangeHotkey(hotkey) ? null : hotkey;
        HotkeyInput.Hotkey = hotkey;
        WinToggle.IsChecked = HotkeyInput.ForceWin = hotkey.Modifiers.HasFlag(ModifierKeys.Windows);
        UpdateHotkeyStatus();
    }

    private void UpdateHotkeyStatus()
    {
        var cfg = App.Instance.Config;
        string? warn = null;
        if (_busy is { } b) warn = Loc.T(cfg.HotkeyTakeover ? "settings.hotkey.busy_keep" : "settings.hotkey.busy_takeover",
            b.ToString(), cfg.Hotkey);
        else if (!App.Instance.HotkeyRegistered)
            warn = Loc.T(cfg.HotkeyTakeover ? "settings.hotkey.busy" : "settings.hotkey.busy_takeover", cfg.Hotkey, cfg.Hotkey);
        string? note = warn is null && App.Instance.HotkeyShared ? Loc.T("hotkey.shared", cfg.Hotkey) : null;
        HotkeyRow.Sub = warn ?? note ?? Loc.T("settings.hotkey.sub");
        HotkeyRow.SubBrush = warn is null ? null : new SolidColorBrush(Color.FromRgb(0xF0, 0xB0, 0x70));
    }

    private void Takeover_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.SetHotkeyTakeover(TakeoverSwitch.IsChecked == true);
        if (_busy is { } hk && App.Instance.TryChangeHotkey(hk)) _busy = null;
        Refresh();
    }

    // ---------- панель ----------

    private void Games_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.Config.GameMinimize = GameMinimizeSwitch.IsChecked == true;
        App.Instance.Config.GameRestore = GameRestoreSwitch.IsChecked == true;
        App.Instance.SaveConfig();
    }

    // ---------- рабочий стол ----------
    private void Desktop_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Instance.Config;
        cfg.SearchEnabled = SearchSwitch.IsChecked == true;
        cfg.SearchSystem = SearchSystemSwitch.IsChecked == true;
        cfg.ShowRecycleBin = RecycleSwitch.IsChecked == true;
        cfg.DesktopDoubleClick = DoubleClickSwitch.IsChecked == true;
        App.Instance.SaveConfig();
        App.Instance.Overlay?.ApplyLook();
        App.Instance.Overlay?.ReloadDesktop();
        Refresh();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.Overlay?.ShowPage(OverlayWindow.PageDesktop);
        App.Instance.Overlay?.SortDesktop();
    }

    private void AutostartSwitch_Click(object sender, RoutedEventArgs e) =>
        App.Instance.SetAutostart(AutostartSwitch.IsChecked == true);

    // ---------- Zapret ----------
    /// <summary>Выключение «Подгружать Zapret» удаляет его целиком — сначала спрашиваем.</summary>
    private void ZapretSwitch_Click(object sender, RoutedEventArgs e)
    {
        var on = ZapretSwitch.IsChecked == true;
        if (on)
        {
            App.Instance.Zapret.SetEnabled(true);
            Refresh();
            App.Instance.Overlay?.UpdateZapretButton();
            return;
        }
        ZapretSwitch.IsChecked = true; // пока не подтвердили
        App.Instance.Overlay?.Confirm(Loc.T("uninstall.zapret.title"), Loc.T("uninstall.zapret.text", Features.Zapret.ZapretPaths.Dir),
            Loc.T("uninstall.zapret.ok"), _ =>
            {
                App.Instance.Zapret.SetEnabled(false);
                App.Instance.Overlay?.ShowPage(OverlayWindow.PageSettings);
                ShowSection("zapret");
                Refresh();
                App.Instance.Overlay?.UpdateZapretButton();
            });
    }

    private void ZapretOpen_Click(object sender, RoutedEventArgs e) =>
        App.Instance.Overlay?.ShowPage(OverlayWindow.PageZapret);

    /// <summary>Сброс — в два нажатия: первое спрашивает «точно?».</summary>
    private void ZapretReset_Click(object sender, RoutedEventArgs e)
    {
        if (_resetConfirm is null)
        {
            ZapretResetButton.Content = Loc.T("zapret.uninstall.confirm");
            _resetConfirm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _resetConfirm.Tick += (_, _) => ResetConfirmOff();
            _resetConfirm.Start();
            return;
        }
        ResetConfirmOff();
        App.Instance.Zapret.Reset();
        App.Instance.Overlay?.ShowPage(OverlayWindow.PageZapret);
    }

    private void ResetConfirmOff()
    {
        _resetConfirm?.Stop();
        _resetConfirm = null;
        ZapretResetButton.Content = Loc.T("zapret.uninstall.button");
    }

    // ---------- мини-браузер ----------
    private void BuildPipHotkeys()
    {
        foreach (var (action, _) in PipController.Actions)
        {
            var a = action;
            _pipBoxes[a] = AddShortcutRow(PipHotkeyRows, PipHotkeyStatus, "pip:" + a, Loc.T("pip.action." + a),
                () => Hotkey.Parse(PipController.HotkeyOf(App.Instance.Config, a)),
                hk =>
                {
                    if (App.Instance.Pip is { } pip) pip.Assign(a, hk);
                    else
                    {
                        App.Instance.Config.PipHotkeys[a] = hk.ToString();
                        App.Instance.SaveConfig();
                    }
                }, "settings.pip.hotkeys.sub");
        }
    }

    // ---------- сочетания: строка с полем и точкой состояния ----------
    // Все сочетания программы (панель, действия, браузер) — одной строкой: подпись, точка, поле. Точка: зелёная — работает,
    // жёлтая — занято Windows и программа его забирает (или заберёт), красная — занято и не работает, нет точки — не назначено.
    // Одинаковые сочетания у разных действий не назначаются — в строке над списком пишется, у кого оно уже.
    private readonly List<(string Id, string Label, Func<Hotkey> Get, Ellipse Dot)> _bindings = new();

    private HotkeyBox AddShortcutRow(Panel host, TextBlock status, string id, string label, Func<Hotkey> get, Action<Hotkey> set,
        string statusKey)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(StyleProperty, "RowSubText");
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var box = new HotkeyBox { Width = 170, HorizontalContentAlignment = HorizontalAlignment.Center, Hotkey = get() };
        box.SetResourceReference(StyleProperty, "InputBox");
        box.Check = hk => CheckShortcut(hk, id);
        box.ConfirmAnchor = dot; // плашка левее точки, не поверх неё
        box.RecordingChanged += recording =>
        {
            App.Instance.SuspendHotkeys(recording);
            if (!recording) UpdateShortcutDots(); // сочетания заняты снова — теперь видно, какие работают
        };
        box.HotkeyChosen += hk =>
        {
            if (OwnerOf(hk, id) is { } other)
            {
                status.Text = Loc.T("shortcut.dup", hk.Display, other);
                box.Hotkey = get();
                return;
            }
            set(hk);
            box.Hotkey = get();
            status.Text = Loc.T(statusKey);
        };
        Grid.SetColumn(dot, 1);
        Grid.SetColumn(box, 2);
        g.Children.Add(text);
        g.Children.Add(dot);
        g.Children.Add(box);
        host.Children.Add(g);
        _bindings.Add((id, label, get, dot));
        return box;
    }

    /// <summary>
    /// Плашка у поля до подтверждения: «Не занято» (зелёным), «Заберём у Windows» (жёлтым — работает через «Забирать
    /// у Windows»), занято Windows или другим действием программы (красным, применить нельзя).
    /// </summary>
    private (string Text, Color Color, bool Ok) CheckShortcut(Hotkey hk, string id)
    {
        if (OwnerOf(hk, id) is { } other) return (Loc.T("hotkey.taken_by", other), StatusDot.Red, false);
        return App.Instance.HotkeyState(hk) switch
        {
            HotkeyManager.State.Busy => (Loc.T("hotkey.busy"), StatusDot.Red, false),
            HotkeyManager.State.BusyTakeover or HotkeyManager.State.OursShared => (Loc.T("hotkey.takeover"), StatusDot.Yellow, true),
            _ => (Loc.T("hotkey.free"), StatusDot.Green, true),
        };
    }

    /// <summary>У какого другого действия уже это сочетание (подпись) — null, если ни у какого.</summary>
    private string? OwnerOf(Hotkey hk, string exceptId)
    {
        if (hk.IsEmpty) return null;
        foreach (var (id, label, get, _) in _bindings)
            if (id != exceptId && get() == hk) return label;
        return null;
    }

    private void BuildShortcuts()
    {
        _bindings.Add(("main", Loc.T("settings.hotkey.title"), () => Hotkey.Parse(App.Instance.Config.Hotkey), HotkeyDot));
        foreach (var action in App.ShortcutActions)
        {
            var a = action;
            AddShortcutRow(ShortcutRows, ShortcutStatus, "sc:" + a, Loc.T("shortcut." + a),
                () => App.Instance.Config.Shortcuts.TryGetValue(a, out var v) ? Hotkey.Parse(v) : default,
                hk =>
                {
                    if (hk.IsEmpty) App.Instance.Config.Shortcuts.Remove(a);
                    else App.Instance.Config.Shortcuts[a] = hk.ToString();
                    App.Instance.SaveConfig();
                    App.Instance.RegisterShortcuts();
                }, "settings.shortcuts.sub");
        }
    }

    private static readonly Brush DotGreen = FrozenBrush(StatusDot.Green), DotYellow = FrozenBrush(StatusDot.Yellow),
        DotRed = FrozenBrush(StatusDot.Red);

    private static Brush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Точки состояния у всех сочетаний (после назначения, открытия страницы, смены «Забирать у Windows»).</summary>
    private void UpdateShortcutDots()
    {
        foreach (var (_, _, get, dot) in _bindings)
        {
            var hk = get();
            if (!hk.IsValid)
            {
                dot.Visibility = Visibility.Hidden;
                continue;
            }
            var st = App.Instance.HotkeyState(hk);
            var shared = st is HotkeyManager.State.OursShared or HotkeyManager.State.BusyTakeover;
            dot.Visibility = Visibility.Visible;
            dot.Fill = st == HotkeyManager.State.Busy ? DotRed : shared ? DotYellow : DotGreen;
            dot.ToolTip = Loc.T(st == HotkeyManager.State.Busy ? "shortcut.state.busy"
                : shared ? "shortcut.state.shared"
                : st == HotkeyManager.State.Mouse ? "shortcut.state.mouse" : "shortcut.state.ok");
            Theme.Tips.SetInfo(dot, true);
        }
    }

    private void PipSwitch_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.Config.PipEnabled = PipSwitch.IsChecked == true;
        App.Instance.SaveConfig();
        App.Instance.Pip?.ApplySettings();
        App.Instance.Overlay?.UpdateHeader();
        Refresh();
    }

    private void PipOpen_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.Pip?.Show(); // без режима взаимодействия (включается тройным щелчком, кнопкой или сочетанием)
    }



    /// <summary>Размер окна: ширина с ползунка, высота — по соотношению сторон (панель лежит поверх страницы).</summary>
    private void PipSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        var cfg = App.Instance.Config;
        cfg.PipWidth = (int)Math.Round(e.NewValue / 10) * 10;
        cfg.PipHeight = (int)Math.Round(cfg.PipWidth / AppConfig.AspectRatio(cfg.PipAspect));
        PipSizeValue.Text = $"{cfg.PipWidth}×{cfg.PipHeight}";
        App.Instance.Pip?.ApplySize();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void PipOptions_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.Config.PipAutoPause = PipPauseSwitch.IsChecked == true;
        App.Instance.Config.PipFreeMemory = PipFreeMemSwitch.IsChecked == true;
        App.Instance.Config.PipAutoInteract = PipAutoSwitch.IsChecked == true;
        App.Instance.Config.PipDoubleClick = PipDoubleSwitch.IsChecked == true;
        App.Instance.SaveConfig();
        App.Instance.Pip?.ApplySettings();
    }

    private void PipResetPlace_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Instance.Config;
        cfg.PipX = cfg.PipY = null;
        cfg.PipWidth = 480;
        cfg.PipHeight = (int)Math.Round(480 / AppConfig.AspectRatio(cfg.PipAspect));
        App.Instance.SaveConfig();
        App.Instance.Pip?.ResetPlacement();
        Refresh();
    }

    // ---------- система ----------
    private void Wizard_Click(object sender, RoutedEventArgs e) => App.Instance.ShowWizard();

    private void Logs_Click(object sender, RoutedEventArgs e) => App.Instance.OpenLogs();

    private void LogsClear_Click(object sender, RoutedEventArgs e)
    {
        Core.Logging.Log.Clear();
        Core.Logging.Log.Info("логи очищены");
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Instance.StopProgram();

    private void UninstallApp_Click(object sender, RoutedEventArgs e) => App.Instance.AskUninstallApp();
}
