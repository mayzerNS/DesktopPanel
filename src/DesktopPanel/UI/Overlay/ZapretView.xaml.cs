using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Native;
using DesktopPanel.Features.Zapret;
using DesktopPanel.UI.Controls;

namespace DesktopPanel.UI.Overlay;

/// <summary>Страница «Zapret»: вкладки Основное, Тест стратегий, Списки, Диагностика, Дополнительно.</summary>
public partial class ZapretView : UserControl
{
    private static readonly string[] TabKeys = { "main", "test", "lists", "diag", "extra" };

    private readonly ZapretService _z;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<ButtonBase> _actions = new();          // выключаются, пока Zapret занят
    private readonly Dictionary<string, Row> _listRows = new();
    private readonly Dictionary<string, TextBlock> _fakeLabels = new();
    private readonly List<ButtonBase> _fakeButtons = new();
    private string _tab = "main";
    private bool _syncQueued;
    private string _strategiesKey = "", _testsKey = "", _fakesKey = "-";
    private List<DiagItem>? _shownDiag;
    private DispatcherTimer? _uninstallConfirm;

    public ZapretView()
    {
        InitializeComponent();
        _z = App.Instance.Zapret;

        Tabs.SetItems(TabKeys.Select(k => (k, Loc.T("zapret.tab." + k))));
        Tabs.Picked += ShowTab;
        Tabs.Selected = _tab;

        GameSeg.SetItems(ZapretFiles.GameModes.Select(m => (m, Loc.T("zapret.game.mode." + m))));
        GameSeg.Picked += m => _z.SetGame(mode: m);
        PortsSeg.SetItems(new[]
        {
            (ZapretFiles.AllPorts, Loc.T("zapret.game.all_ports")),
            (ZapretFiles.NoRtmp, Loc.T("zapret.game.no_rtmp")),
        });
        PortsSeg.Picked += r => _z.SetGame(range: r);
        IpsetSeg.SetItems(ZapretFiles.IpsetModes.Select(m => (m, Loc.T("zapret.ipset.mode." + m))));
        IpsetSeg.Picked += _z.SetIpset;

        RestartButton.ToolTip = HelpDot.Build("z_restart");
        _actions.AddRange(GameSeg.Buttons.Concat(PortsSeg.Buttons).Concat(IpsetSeg.Buttons));
        _actions.AddRange(new ButtonBase[]
        {
            RestartButton, PortsEditButton, IpsetUpdateButton, CheckNowButton, TestButton, ApplyListsButton,
            HostsCheckButton, HostsAddButton, HostsRemoveButton, DiagButton, CacheButton,
            ServiceInstallButton, ServiceRemoveButton,
        });

        BuildListRows();
        BuildFakeRows();

        _z.Changed += QueueSync;
        _z.TestLine += line => Dispatcher.InvokeAsync(() => AppendLog(line));
        _poll.Tick += (_, _) => _z.RefreshAsync();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _z.RefreshAsync();
                _poll.Start();
                Sync();
            }
            else _poll.Stop();
        };
    }

    // ---------- вкладки ----------
    public void ShowTab(string key)
    {
        var changed = _tab != key;
        _tab = key;
        Tabs.Selected = key;
        MainTab.Visibility = Vis(key == "main");
        TestTab.Visibility = Vis(key == "test");
        ListsTab.Visibility = Vis(key == "lists");
        DiagTab.Visibility = Vis(key == "diag");
        ExtraTab.Visibility = Vis(key == "extra");
        if (key == "test") ReloadLog();
        Sync();
        if (changed && IsVisible) EnterTab();
    }

    private ScrollViewer CurrentTab => _tab switch
    {
        "test" => TestTab,
        "lists" => ListsTab,
        "diag" => DiagTab,
        "extra" => ExtraTab,
        _ => MainTab,
    };

    /// <summary>Переход на страницу Zapret: вкладки, плашки и строки появляются «лесенкой».</summary>
    public void Enter()
    {
        Layout.UpdateLayout();
        var items = new List<FrameworkElement> { Tabs };
        items.AddRange(Pills.Children.OfType<FrameworkElement>());
        if (CurrentTab.Content is Panel p) items.AddRange(p.Children.OfType<FrameworkElement>());
        DesktopPanel.UI.Theme.Fx.Enter(items, Layout);
    }

    /// <summary>Смена вкладки: «лесенкой» только её строки.</summary>
    private void EnterTab()
    {
        CurrentTab.UpdateLayout();
        if (CurrentTab.Content is Panel p) DesktopPanel.UI.Theme.Fx.Enter(p.Children.OfType<FrameworkElement>(), CurrentTab);
    }

    private static void ShowPill(FrameworkElement pill, Visibility v)
    {
        if (pill.Visibility == v) return;
        pill.Visibility = v;
        if (v == Visibility.Visible && pill.IsVisible) DesktopPanel.UI.Theme.Fx.PopIn(pill);
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    private static Visibility Vis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;

    // ---------- обновление вида ----------
    /// <summary>Changed приходит из фоновых потоков и пачками — перерисовываем один раз.</summary>
    private void QueueSync()
    {
        if (_syncQueued) return;
        _syncQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _syncQueued = false;
            App.Instance.Overlay?.UpdateZapretButton();
            if (IsVisible) Sync();
        }, DispatcherPriority.Background);
    }

    public void Sync()
    {
        var root = _z.Root;
        var busy = _z.Busy is not null;
        foreach (var b in _actions) b.IsEnabled = !busy && root.Length > 0;

        ErrorText.Text = _z.Error is null ? "" : "⚠ " + _z.Error;
        NoticeText.Text = _z.Notice is null ? "" : "✓ " + _z.Notice;
        var vpn = Features.Zapret.VpnDetector.ActiveTunnel();
        VpnText.Text = vpn is null ? "" : Loc.T("zapret.vpn_active", vpn);
        // плашки появляются и пропадают плавно, а содержимое под ними доезжает, а не прыгает
        Visibility ve = Vis(_z.Error is not null), vn = Vis(_z.Notice is not null && _z.Error is null), vv = Vis(vpn is not null);
        if (ErrorPill.Visibility != ve || NoticePill.Visibility != vn || VpnPill.Visibility != vv)
            DesktopPanel.UI.Theme.Fx.Flip(Layout, () =>
            {
                ShowPill(ErrorPill, ve);
                ShowPill(NoticePill, vn);
                ShowPill(VpnPill, vv);
            });

        switch (_tab)
        {
            case "main": SyncMain(root, busy); break;
            case "test": SyncTest(busy); break;
            case "lists": SyncLists(root); break;
            case "diag": SyncDiag(busy); break;
            case "extra": SyncExtra(root, busy); break;
        }
    }

    private void SyncMain(string root, bool busy)
    {
        StatusRow.Title = busy ? Cap(_z.TestInfo ?? _z.Busy ?? "") + "…"
            : _z.Running ? Loc.T(_z.Service is not null ? "zapret.state.on_service" : "zapret.state.on")
            : Loc.T(_z.Service is not null ? "zapret.state.off_service" : "zapret.state.off");
        var strat = _z.Strategy;
        StatusRow.Sub = root.Length == 0 ? Loc.T("zapret.not_downloaded")
            : Loc.T("zapret.strategy.current", strat.Length > 0 ? Path.GetFileNameWithoutExtension(strat) : "—");
        OnOffButton.Content = Loc.T(root.Length == 0 ? "zapret.download" : _z.Running ? "zapret.turn_off" : "zapret.turn_on");
        OnOffButton.IsChecked = _z.Running;
        OnOffButton.IsEnabled = !busy;
        RestartButton.Visibility = Vis(_z.Running);

        // стратегии: кнопки пересоздаём, только когда поменялся список или результаты теста
        var names = _z.Strategies();
        var key = string.Join("|", names) + "#" + string.Join("|", _z.Tests.Select(kv => kv.Key + kv.Value.When.Ticks));
        if (key != _strategiesKey)
        {
            _strategiesKey = key;
            StrategyGrid.Children.Clear();
            foreach (var bat in names) StrategyGrid.Children.Add(StrategyButton(bat));
        }
        foreach (ToggleButton b in StrategyGrid.Children)
        {
            b.IsChecked = (string)b.Tag == strat;
            b.IsEnabled = !busy;
        }

        GameBox.IsEnabled = IpsetBox.IsEnabled = root.Length > 0;
        if (root.Length > 0)
        {
            var (mode, tcp, udp) = ZapretFiles.ReadGame(root);
            GameSeg.Selected = mode;
            var preset = tcp == udp && (tcp == ZapretFiles.AllPorts || tcp == ZapretFiles.NoRtmp);
            PortsSeg.Selected = preset ? tcp : null;
            GameLabel.Text = preset ? "" : $"TCP {tcp} · UDP {udp}";
            var ip = ZapretFiles.IpsetMode(root);
            IpsetSeg.Selected = ip;
            IpsetLabel.Text = Loc.T("zapret.ipset.desc." + ip);
        }

        AutoUpdateSwitch.IsChecked = _z.AutoUpdate;
        var when = App.Instance.Config.ZapretChecked;
        UpdateRow.Sub = Loc.T("zapret.autoupdate.sub", _z.Version.Length > 0 ? _z.Version : "—",
            when == default ? "—" : when.ToString("dd.MM HH:mm"));
    }

    private ToggleButton StrategyButton(string bat)
    {
        var name = Path.GetFileNameWithoutExtension(bat);
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new Run(name));
        if (Score(bat) is { } s)
            text.Inlines.Add(new Run("   " + s.Text) { Foreground = new SolidColorBrush(s.Color), FontWeight = FontWeights.SemiBold });
        var b = new ToggleButton { Content = text, Tag = bat, Margin = new Thickness(0, 0, 6, 6), ToolTip = StrategyTip(name) };
        b.SetResourceReference(StyleProperty, "PanelToggle");
        ToolTipService.SetInitialShowDelay(b, 400);
        b.Click += (_, _) =>
        {
            b.IsChecked = (string)b.Tag == _z.Strategy;
            _z.SetStrategy(bat);
        };
        return b;
    }

    private static TextBlock StrategyTip(string name)
    {
        var n = name.ToUpperInvariant();
        var kind = n.Contains("FAKE TLS AUTO") ? "fake_tls_auto" : n.Contains("SIMPLE FAKE") ? "simple_fake"
            : n.Contains("EXP") ? "exp" : n.Contains("ALT") ? "alt" : "general";
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
        tb.Inlines.Add(new Run(name) { FontWeight = FontWeights.SemiBold });
        tb.Inlines.Add(new LineBreak());
        tb.Inlines.Add(new Run(Loc.T($"help.strategy.{kind}.body")));
        return tb;
    }

    private (string Text, Color Color)? Score(string bat)
    {
        if (!_z.Tests.TryGetValue(bat, out var r)) return null;
        if (r.Error is not null) return (Loc.T("zapret.score.error"), StatusDot.Red);
        return ($"{r.Ok}/{r.Total}", ScoreColor(r));
    }

    private static Color ScoreColor(TestResult r) =>
        r.Ok == r.Total ? StatusDot.Green : r.Ok > 0 ? StatusDot.Yellow : StatusDot.Red;

    // ---------- тест ----------
    private void SyncTest(bool busy)
    {
        var testing = _z.Testing;
        TestButton.Visibility = Vis(!testing);
        TestStopButton.Visibility = Vis(testing);
        TestStatus.Text = _z.TestInfo is not null ? Loc.T("zapret.test.checking", _z.TestInfo) : "";

        var key = _z.Strategy + "#" + string.Join("|", _z.Tests.Select(kv => kv.Key + kv.Value.When.Ticks));
        if (key != _testsKey)
        {
            _testsKey = key;
            BuildTestRows();
        }
        foreach (var b in TestList.Children.OfType<Row>().Select(r => r.Right).OfType<ButtonBase>())
            b.IsEnabled = !busy;
    }

    private void BuildTestRows()
    {
        TestList.Children.Clear();
        var names = _z.Strategies().ToHashSet();
        if (_z.Tests.TryGetValue(StrategyTester.NoBypass, out var b0))
        {
            var failed = string.Join(", ", b0.Failed.Select(StrategyTester.TargetName));
            TestList.Children.Add(new Row
            {
                Title = Loc.T("zapret.test.nobypass_title"),
                Sub = Loc.T("zapret.test.nobypass_sub", b0.Ok, b0.Total) + " · " +
                      (b0.Failed.Count > 0 ? Loc.T("zapret.test.blocked", failed) : Loc.T("zapret.test.nothing_blocked")),
                Lead = StatusDot.Make(b0.Failed.Count == 0 ? StatusDot.Green : b0.Ok > 0 ? StatusDot.Yellow : StatusDot.Red),
            });
        }
        var rows = _z.Tests.Where(kv => names.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value.Ok).ThenBy(kv => kv.Value.Ms ?? int.MaxValue).ToList();
        var cur = _z.Strategy;
        for (var i = 0; i < rows.Count; i++)
        {
            var (bat, r) = (rows[i].Key, rows[i].Value);
            string sub;
            Color col;
            if (r.Error is not null)
            {
                sub = Loc.T("zapret.test.failed_start", r.Error);
                col = StatusDot.Red;
            }
            else
            {
                col = ScoreColor(r);
                sub = Loc.T("zapret.test.opened", r.Ok, r.Total);
                if (r.Ms is { } ms) sub += " · " + Loc.T("zapret.test.avg", ms);
                if (r.Failed.Count > 0 && r.Ok > 0)
                    sub += " · " + Loc.T("zapret.test.not_opened", string.Join(", ", r.Failed.Select(StrategyTester.TargetName)));
            }
            var pick = new ToggleButton
            {
                Content = Loc.T(bat == cur ? "zapret.test.picked" : "zapret.test.pick"),
                IsChecked = bat == cur,
            };
            pick.SetResourceReference(StyleProperty, "PanelToggle");
            var target = bat;
            pick.Click += (_, _) =>
            {
                pick.IsChecked = target == _z.Strategy;
                _z.SetStrategy(target);
            };
            var title = Path.GetFileNameWithoutExtension(bat);
            if (i == 0 && r.Ok > 0) title += "   ★ " + Loc.T("zapret.test.best");
            TestList.Children.Add(new Row { Title = title, Sub = sub, Lead = StatusDot.Make(col), Right = pick });
        }
        if (rows.Count == 0 && b0 is null)
            TestList.Children.Add(new TextBlock
            {
                Text = Loc.T("zapret.test.never"),
                Style = (Style)FindResource("RowSubText"),
                Margin = new Thickness(4, 0, 0, 8),
            });
    }

    private void ReloadLog()
    {
        List<string> lines;
        lock (_z.TestLines) lines = _z.TestLines.ToList();
        if (lines.Count == 0)
        {
            try { lines = File.ReadAllLines(ZapretPaths.TestLog).ToList(); }
            catch { lines = new(); }
        }
        TestLogBox.Text = string.Join(Environment.NewLine, lines);
        TestLogBox.ScrollToEnd();
    }

    private void AppendLog(string line)
    {
        if (TestLogBox.Text.Length > 200_000) TestLogBox.Text = TestLogBox.Text[^100_000..];
        TestLogBox.AppendText((TestLogBox.Text.Length > 0 ? Environment.NewLine : "") + line);
        TestLogBox.ScrollToEnd();
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        TestLogBox.Text = "";
        _z.RunTests();
    }

    private void TestStop_Click(object sender, RoutedEventArgs e) => _z.StopTests();

    private void OpenTestLog_Click(object sender, RoutedEventArgs e) => OpenFile(ZapretPaths.TestLog);

    // ---------- списки ----------
    private void BuildListRows()
    {
        // списки правятся прямо здесь: «Изменить» раскрывает под строкой редактор (а не Блокнот)
        foreach (var (file, help, _) in ZapretFiles.UserLists)
        {
            var open = new Button { Content = Loc.T("zapret.edit.button") };
            open.SetResourceReference(StyleProperty, "PanelButton");
            var f = file;
            var ip = file.StartsWith("ipset", StringComparison.Ordinal);
            var editor = new ListEditor(l =>
                {
                    var (fx, err) = ip ? ZapretFiles.CheckIp(l) : ZapretFiles.CheckDomain(l);
                    return new ListEditor.LineCheck(fx, err);
                },
                "e_" + help[2..], Loc.T(ip ? "zapret.edit.hint_ip" : "zapret.edit.hint_domain"));
            editor.Cancelled += () => DesktopPanel.UI.Theme.Fx.SetVisible(editor, false);
            editor.Saved += text =>
            {
                DesktopPanel.UI.Theme.Fx.SetVisible(editor, false);
                _z.SaveUserList(f, text);
            };
            _actions.Add(editor.SaveButton);
            open.Click += (_, _) =>
            {
                if (editor.Visibility == Visibility.Visible)
                {
                    DesktopPanel.UI.Theme.Fx.SetVisible(editor, false);
                    return;
                }
                var root = _z.Root;
                if (root.Length == 0) return;
                ZapretFiles.EnsureUserLists(root);
                editor.Load(ZapretFiles.ReadUserList(Path.Combine(root, "lists", f)));
                DesktopPanel.UI.Theme.Fx.SetVisible(editor, true);
                editor.FocusText();
            };
            _actions.Add(open);
            var row = new Row { Title = Loc.T("zapret.list." + help), Help = help, Right = open };
            _listRows[file] = row;
            UserListRows.Children.Add(row);
            UserListRows.Children.Add(editor);
        }
    }

    private void SyncLists(string root)
    {
        foreach (var (file, row) in _listRows)
            row.Sub = root.Length == 0 ? file
                : $"{file} · {Loc.Plural(ZapretFiles.CountEntries(Path.Combine(root, "lists", file)), "zapret.entries")}";

        if (_z.Hosts is { } h)
            HostsLabel.Text = h.Missing.Count == 0 ? Loc.T("zapret.hosts.all_present")
                : Loc.T("zapret.hosts.missing", h.Missing.Count, h.Recommended.Count);
        else
            HostsLabel.Text = Loc.T("zapret.hosts.sub");
        HostsRemoveButton.Visibility = Vis(ZapretFiles.HostsHasBlock());
    }

    private void OpenListsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_z.Root.Length > 0) OpenFolder(Path.Combine(_z.Root, "lists"));
    }

    private void ApplyLists_Click(object sender, RoutedEventArgs e) => _z.ApplyLists();
    private void HostsCheck_Click(object sender, RoutedEventArgs e) => _z.CheckHosts();
    private void HostsAdd_Click(object sender, RoutedEventArgs e) => _z.AddHosts();
    private void HostsRemove_Click(object sender, RoutedEventArgs e) => _z.RemoveHosts();

    // ---------- диагностика ----------
    private void SyncDiag(bool busy)
    {
        var d = _z.Diagnostics;
        if (!ReferenceEquals(d, _shownDiag))
        {
            _shownDiag = d;
            BuildDiagRows(d);
        }
        if (busy && _z.Diagnostics is null && _z.Busy == Loc.T("zapret.busy.diag"))
            DiagStatus.Text = Loc.T("zapret.diag.checking");
        else if (d is not null)
        {
            var bad = d.Count(x => x.Level == DiagLevel.Error);
            var warn = d.Count(x => x.Level == DiagLevel.Warn);
            DiagStatus.Text = bad == 0 && warn == 0 ? Loc.T("zapret.diag.clean") : Loc.T("zapret.diag.summary", bad, warn);
        }
        else DiagStatus.Text = "";
        foreach (var b in DiagList.Children.OfType<Row>().Select(r => r.Right).OfType<ButtonBase>())
            b.IsEnabled = !busy;
        DefenderRow.Sub = Loc.T("zapret.defender.sub") + "\n" + ZapretPaths.Dir;
    }

    private void BuildDiagRows(List<DiagItem>? items)
    {
        DiagList.Children.Clear();
        if (items is null) return;
        foreach (var it in items.OrderBy(x => x.Level switch { DiagLevel.Error => 0, DiagLevel.Warn => 1, _ => 2 }))
        {
            Button? fix = null;
            if (it.Fix is not null)
            {
                fix = new Button { Content = Loc.T("zapret.fix." + it.Fix) };
                fix.SetResourceReference(StyleProperty, "PanelButton");
                var k = it.Fix;
                fix.Click += (_, _) => _z.Fix(k);
            }
            var col = it.Level switch { DiagLevel.Error => StatusDot.Red, DiagLevel.Warn => StatusDot.Yellow, _ => StatusDot.Green };
            DiagList.Children.Add(new Row { Title = it.Title, Sub = it.Detail, Lead = StatusDot.Make(col), Right = fix });
        }
    }

    private void Diag_Click(object sender, RoutedEventArgs e) => _z.RunDiagnostics();
    private void Cache_Click(object sender, RoutedEventArgs e) => _z.ClearDiscordCache();

    private void DefenderCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ZapretPaths.Dir);
            _z.SetNotice(Loc.T("zapret.defender.copied"));
        }
        catch { }
    }

    private void DefenderOpen_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ZapretPaths.Dir); }
        catch { }
        _z.SetNotice(Loc.T("zapret.defender.howto"));
        App.Instance.Overlay?.HideOverlay();
        ZapretDiagnostics.OpenDefenderSettings();
    }

    private void OpenWinwsLog_Click(object sender, RoutedEventArgs e) => OpenFile(ZapretPaths.WinwsLog);

    // ---------- дополнительно ----------
    private void BuildFakeRows()
    {
        foreach (var (slot, titleKey, help) in ZapretFiles.FakeSlots)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = Loc.T(titleKey), VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(StyleProperty, "RowSubText");
            head.Children.Add(t);
            head.Children.Add(new HelpDot { Key = help });

            var s = slot;
            var prev = Glyph("", () => _z.SetFake(s, -1));
            var next = Glyph("", () => _z.SetFake(s, 1));
            var label = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0, 6, 0),
            };
            label.SetResourceReference(StyleProperty, "RowTitleText");
            Grid.SetColumn(prev, 1);
            Grid.SetColumn(label, 2);
            Grid.SetColumn(next, 3);
            g.Children.Add(head);
            g.Children.Add(prev);
            g.Children.Add(label);
            g.Children.Add(next);
            _fakeLabels[slot] = label;
            _fakeButtons.Add(prev);
            _fakeButtons.Add(next);
            FakeRows.Children.Add(g);
        }
    }

    private static Button Glyph(string glyph, Action click)
    {
        var b = new Button { Content = glyph };
        b.SetResourceReference(StyleProperty, "GlyphButton");
        b.Click += (_, _) => click();
        return b;
    }

    private void SyncExtra(string root, bool busy)
    {
        var st = _z.Service;
        ServiceRow.Sub = st is not null
            ? Loc.T(st == "RUNNING" ? "zapret.service.installed_running" : "zapret.service.installed_stopped",
                ZapretRuntime.ServiceStrategy() ?? "?")
            : Loc.T("zapret.service.sub");
        ServiceInstallButton.Content = Loc.T(st is not null ? "zapret.service.reinstall" : "zapret.service.install");
        ServiceRemoveButton.Visibility = Vis(st is not null);

        // хэши .bin считаем, только когда файлы поменялись (Sync зовётся каждые 2 с)
        var fakeKey = root + string.Join("|", _fakeLabels.Keys.Select(sl =>
            root.Length == 0 ? "" : File.GetLastWriteTimeUtc(Path.Combine(root, "bin", sl)).Ticks.ToString()));
        if (fakeKey != _fakesKey)
        {
            _fakesKey = fakeKey;
            foreach (var (slot, label) in _fakeLabels)
                label.Text = root.Length == 0 ? "—" : ZapretFiles.ActiveFake(root, slot) ?? Loc.T("zapret.fakes.custom");
        }
        foreach (var b in _fakeButtons) b.IsEnabled = !busy && root.Length > 0;
        UninstallButton.IsEnabled = !busy;
    }

    private void ServiceInstall_Click(object sender, RoutedEventArgs e) => _z.InstallService();
    private void ServiceRemove_Click(object sender, RoutedEventArgs e) => _z.RemoveService();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(_z.Root.Length > 0 ? _z.Root : ZapretPaths.Dir);

    /// <summary>Удаление — в два нажатия: первое спрашивает «точно?».</summary>
    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstallConfirm is null)
        {
            UninstallButton.Content = Loc.T("zapret.uninstall.confirm");
            _uninstallConfirm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _uninstallConfirm.Tick += (_, _) => ResetUninstall();
            _uninstallConfirm.Start();
            return;
        }
        ResetUninstall();
        _z.Reset();
    }

    private void ResetUninstall()
    {
        _uninstallConfirm?.Stop();
        _uninstallConfirm = null;
        UninstallButton.Content = Loc.T("zapret.uninstall.button");
    }

    // ---------- основное: кнопки ----------
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        OnOffButton.IsChecked = _z.Running; // вид поменяет Sync, когда Zapret действительно включится
        _z.Toggle();
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => _z.ApplyLists();
    private void IpsetUpdate_Click(object sender, RoutedEventArgs e) => _z.UpdateIpset();
    private void CheckNow_Click(object sender, RoutedEventArgs e) => _z.CheckUpdates(force: true);
    private void AutoUpdate_Click(object sender, RoutedEventArgs e) => _z.SetAutoUpdate(AutoUpdateSwitch.IsChecked == true);

    // ---------- свои порты: поля TCP и UDP прямо здесь (а не файл в Блокноте) ----------
    private Border? _ports;
    private TextBox? _tcpBox, _udpBox;
    private TextBlock? _portsStatus;
    private Button? _portsSave;

    private void PortsEdit_Click(object sender, RoutedEventArgs e)
    {
        var root = _z.Root;
        if (root.Length == 0) return;
        _ports ??= BuildPortsEditor();
        if (_ports.Visibility == Visibility.Visible)
        {
            DesktopPanel.UI.Theme.Fx.SetVisible(_ports, false);
            return;
        }
        var (_, tcp, udp) = ZapretFiles.ReadGame(root);
        _tcpBox!.Text = tcp;
        _udpBox!.Text = udp;
        CheckPorts();
        DesktopPanel.UI.Theme.Fx.SetVisible(_ports, true);
        _tcpBox.Focus();
    }

    private Border BuildPortsEditor()
    {
        TextBox Field(string label, string help, int row, Grid g)
        {
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            name.SetResourceReference(StyleProperty, "RowSubText");
            var box = new TextBox { Tag = ZapretFiles.NoRtmp, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, row == 0 ? 0 : 6, 0, 0) };
            box.SetResourceReference(StyleProperty, "InputBox");
            box.TextChanged += (_, _) => CheckPorts();
            var dot = new HelpDot { Key = help };
            Grid.SetRow(name, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            Grid.SetRow(dot, row);
            Grid.SetColumn(dot, 2);
            g.Children.Add(name);
            g.Children.Add(box);
            g.Children.Add(dot);
            return box;
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        _tcpBox = Field("TCP", "e_tcp", 0, grid);
        _udpBox = Field("UDP", "e_udp", 1, grid);

        _portsStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _portsStatus.SetResourceReference(StyleProperty, "RowSubText");
        var cancel = new Button { Content = Loc.T("zapret.edit.cancel"), Margin = new Thickness(8, 0, 0, 0) };
        cancel.SetResourceReference(StyleProperty, "PanelButton");
        cancel.Click += (_, _) => DesktopPanel.UI.Theme.Fx.SetVisible(_ports!, false);
        _portsSave = new Button { Content = Loc.T("zapret.edit.save"), Margin = new Thickness(8, 0, 0, 0) };
        _portsSave.SetResourceReference(StyleProperty, "AccentButton");
        _portsSave.Click += (_, _) =>
        {
            var tcp = ZapretFiles.ValidRange(_tcpBox!.Text);
            var udp = ZapretFiles.ValidRange(_udpBox!.Text);
            if (tcp is null || udp is null) return;
            DesktopPanel.UI.Theme.Fx.SetVisible(_ports!, false);
            _z.SetPorts(tcp, udp);
        };
        _actions.Add(_portsSave);
        var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_portsSave);
        Grid.SetColumn(buttons, 1);
        bottom.Children.Add(_portsStatus);
        bottom.Children.Add(buttons);

        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(bottom);
        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 10, 0, 0),
            Child = stack, Visibility = Visibility.Collapsed,
        };
        GameBox.Children.Add(panel);
        return panel;
    }

    private void CheckPorts()
    {
        if (_tcpBox is null || _udpBox is null || _portsStatus is null || _portsSave is null) return;
        var bad = ZapretFiles.ValidRange(_tcpBox.Text) is null ? "TCP" : ZapretFiles.ValidRange(_udpBox.Text) is null ? "UDP" : null;
        _portsSave.IsEnabled = bad is null && _z.Busy is null;
        _portsStatus.Foreground = bad is null ? (Brush)FindResource("SubTextBrush") : new SolidColorBrush(Color.FromRgb(0xF0, 0xB0, 0x70));
        _portsStatus.Text = bad is null ? Loc.T("zapret.edit.ports_ok") : Loc.T("zapret.edit.bad_ports", bad);
    }

    // ---------- открыть файл / папку ----------
    /// <summary>Панель поверх всех окон — прячем её, иначе Блокнот окажется под ней.</summary>
    private static void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        App.Instance.Overlay?.HideOverlay();
        // логи (winws.log, лог теста) лежат в %APPDATA%\DesktopPanel — их открываем копией, списки — как есть
        if (path.StartsWith(Core.Paths.AppData, StringComparison.OrdinalIgnoreCase)) Shell.OpenLog(path);
        else Shell.Notepad(path);
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        App.Instance.Overlay?.HideOverlay();
        Shell.Open(path);
    }
}
