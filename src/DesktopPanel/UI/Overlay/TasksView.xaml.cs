using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;
using DesktopPanel.Features.Tasks;
using DesktopPanel.UI.Controls;

namespace DesktopPanel.UI.Overlay;

/// <summary>Строка таблицы: раздел («Приложения»), программа (группа процессов) или отдельный процесс.</summary>
public sealed class TaskRow : INotifyPropertyChanged
{
    public enum Kinds { Section, Group, Proc }

    public Kinds Kind { get; init; }
    public string Key { get; init; } = "";
    public string? GroupKey { get; init; }
    public int Pid { get; init; }

    public string Title { get; set; } = "";
    public string Count { get; set; } = "";
    public string Arrow { get; set; } = "";
    public ImageSource? Icon { get; set; }
    public string Cpu { get; set; } = "";
    public string Mem { get; set; } = "";
    public string Disk { get; set; } = "";
    public string Net { get; set; } = "";
    public Brush? CpuHeat { get; set; }
    public Brush? MemHeat { get; set; }
    public Brush? DiskHeat { get; set; }
    public Brush? NetHeat { get; set; }
    /// <summary>Выделена — слой выделения строки проявляется (Fx.Show в разметке).</summary>
    public bool IsSelected { get; set; }
    public string? Tip { get; set; }

    public double Height => Kind == Kinds.Section ? 34 : 30;
    public Thickness Indent => new(Kind == Kinds.Proc ? 30 : 4, 0, 0, 0);
    public FontWeight Weight => Kind == Kinds.Section ? FontWeights.SemiBold : FontWeights.Normal;
    public Visibility IconVisibility => Kind == Kinds.Section ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Обновить все привязки строки разом.</summary>
    public void Touch() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// Свой Диспетчер задач: «Приложения» и «Фоновые процессы» с нагрузкой (ЦП, память, диск, сеть).
/// Поиск, сортировка по столбцам, раскрытие программы до процессов, меню по правой кнопке:
/// снять задачу, завершить дерево, расположение файла, приоритет, копировать путь, поиск в интернете.
/// Цифры обновляются раз в секунду и только пока страница открыта.
/// </summary>
public partial class TasksView : UserControl
{
    private static readonly string[] Cols = { "name", "cpu", "mem", "disk", "net" };

    private readonly Dictionary<string, TaskRow> _rows = new(); // по ключу строки: раньше — перебор списка на каждую строку каждую секунду
    private readonly System.Collections.ObjectModel.ObservableCollection<TaskRow> _shown = new();
    private readonly Dictionary<string, (double Cpu, long Mem, double Disk, double Net)> _values = new();
    private readonly Dictionary<string, List<string>> _order = new() { ["app"] = new(), ["bg"] = new() };
    private readonly HashSet<string> _expanded = new();
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _headers = new();
    private ProcMonitor? _monitor;
    private readonly object _sampleLock = new();
    private ProcSnapshot? _snap;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _wake = new(0);
    private string _sortCol = "name";
    private bool _sortAsc = true;
    private string? _selected; // ключ строки
    private string _filter = "";

    public TasksView()
    {
        InitializeComponent();
        RowsList.ItemsSource = _shown;
        BuildHeader();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox) End_Click(this, new RoutedEventArgs());
        };
    }

    // ---------- включение / выключение замеров ----------
    public void SetActive(bool on)
    {
        if (on)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => Loop(token));
        }
        else
        {
            var old = _cts;
            _cts = null;
            old?.Cancel();
            old?.Dispose();
            // ручки процессов и счётчики освобождаем, пока страница закрыта
            Task.Run(() =>
            {
                lock (_sampleLock)
                {
                    _monitor?.Dispose();
                    _monitor = null;
                }
            });
        }
    }

    private async Task Loop(CancellationToken token)
    {
        var first = true;
        while (!token.IsCancellationRequested)
        {
            try
            {
                ProcSnapshot snap;
                lock (_sampleLock)
                {
                    _monitor ??= new ProcMonitor();
                    snap = _monitor.Sample();
                }
                if (!token.IsCancellationRequested)
                    await Dispatcher.InvokeAsync(() => Apply(snap));
            }
            catch (Exception ex)
            {
                Log.Warn($"tasks: замер не удался ({ex.Message})");
            }
            try { await _wake.WaitAsync(first ? 350 : 1000, token); }
            catch (OperationCanceledException) { break; }
            first = false;
        }
    }

    /// <summary>Снять данные прямо сейчас (после «Снять задачу»).</summary>
    private void Poke() => _wake.Release();

    // ---------- шапка ----------
    private void BuildHeader()
    {
        for (var i = 0; i < Cols.Length; i++)
        {
            var col = Cols[i];
            var b = new Button
            {
                HorizontalContentAlignment = col == "name" ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Margin = new Thickness(1, 0, 1, 0),
                Height = 44,
            };
            b.SetResourceReference(StyleProperty, "PanelButton");
            b.Padding = new Thickness(10, 0, 10, 0);
            b.Click += (_, _) => SortBy(col);
            Grid.SetColumn(b, i);
            HeaderGrid.Children.Add(b);
            _headers[col] = b;
        }
        UpdateHeader(null);
    }

    private readonly Dictionary<string, TextBlock> _headerTotals = new(), _headerLabels = new();

    /// <summary>
    /// Содержимое шапки строим один раз и дальше только меняем тексты: раньше оно пересоздавалось каждую секунду,
    /// и подсказка у «?» (её элемент исчез из дерева) закрывалась, открывалась заново и прыгала в угол экрана.
    /// </summary>
    private void UpdateHeader(Totals? t)
    {
        foreach (var col in Cols)
        {
            var label = Loc.T("tasks.col." + col) + (_sortCol == col ? (_sortAsc ? " ▲" : " ▼") : "");
            if (!_headerLabels.TryGetValue(col, out var labelBlock))
            {
                var sp = new StackPanel { HorizontalAlignment = col == "name" ? HorizontalAlignment.Left : HorizontalAlignment.Right };
                if (col != "name")
                {
                    var totalBlock = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
                    _headerTotals[col] = totalBlock;
                    sp.Children.Add(totalBlock);
                }
                var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = sp.HorizontalAlignment };
                labelBlock = new TextBlock { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255)) };
                _headerLabels[col] = labelBlock;
                line.Children.Add(labelBlock);
                if (col == "net") line.Children.Add(new HelpDot { Key = "tm_net", Margin = new Thickness(6, 0, 0, 0) });
                sp.Children.Add(line);
                _headers[col].Content = sp;
            }
            labelBlock.Text = label;
            if (col != "name")
            {
                _headerTotals[col].Text = t is null ? "—" : col switch
                {
                    "cpu" => $"{Math.Round(t.Cpu)}%",
                    "mem" => $"{t.MemoryLoad}%",
                    "disk" => $"{Math.Round(t.Disk)}%",
                    _ => Format("net", t.NetBits / 8),
                };
            }
        }
    }

    private void SortBy(string col)
    {
        if (_sortCol == col) _sortAsc = !_sortAsc;
        else
        {
            _sortCol = col;
            _sortAsc = col == "name"; // имя — по алфавиту, цифры — сначала большие
        }
        foreach (var sec in _order.Values) sec.Sort(Compare);
        UpdateHeader(_snap?.Totals);
        Rebuild();
    }

    private int Compare(string a, string b)
    {
        int r;
        if (_sortCol == "name") r = string.Compare(TitleOf(a), TitleOf(b), StringComparison.CurrentCultureIgnoreCase);
        else r = Value(a).CompareTo(Value(b));
        return _sortAsc ? r : -r;
    }

    private double Value(string key)
    {
        if (!_values.TryGetValue(key, out var v)) return 0;
        return _sortCol switch { "cpu" => v.Cpu, "mem" => v.Mem, "disk" => v.Disk, _ => v.Net };
    }

    private string TitleOf(string key) => _snap is not null && _snap.Groups.TryGetValue(key, out var g) ? g.Title : key;

    // ---------- данные ----------
    private void Apply(ProcSnapshot snap)
    {
        if (!IsVisible) return;
        _snap = snap;
        foreach (var (key, g) in snap.Groups)
        {
            double cpu = 0, disk = 0, net = 0;
            long mem = 0;
            foreach (var pid in g.Pids)
                if (snap.Procs.TryGetValue(pid, out var p))
                {
                    cpu += p.Cpu;
                    mem += p.Memory;
                    disk += p.Disk;
                    net += p.Net;
                }
            // сглаживаем, чтобы цифры не мельтешили
            if (_values.TryGetValue(key, out var old))
                (cpu, disk, net) = (old.Cpu * 0.4 + cpu * 0.6, old.Disk * 0.4 + disk * 0.6, old.Net * 0.4 + net * 0.6);
            _values[key] = (cpu, mem, disk, net);
        }
        foreach (var k in _values.Keys.Where(k => !snap.Groups.ContainsKey(k)).ToList()) _values.Remove(k);

        // порядок: новые встают на своё место по сортировке, остальные не прыгают
        foreach (var sec in _order.Values) sec.RemoveAll(k => !snap.Groups.ContainsKey(k));
        foreach (var (key, g) in snap.Groups)
        {
            var sec = g.IsApp ? "app" : "bg";
            var other = g.IsApp ? "bg" : "app";
            _order[other].Remove(key);
            if (_order[sec].Contains(key)) continue;
            var list = _order[sec];
            var i = 0;
            while (i < list.Count && Compare(list[i], key) <= 0) i++;
            list.Insert(i, key);
        }
        if (_sortCol != "name") foreach (var sec in _order.Values) sec.Sort(Compare); // по нагрузке — честно пересортировываем

        var apps = _order["app"].Count;
        var bg = _order["bg"].Count;
        var t = snap.Totals;
        InfoText.Text = Loc.T("tasks.info", apps, bg,
            (t.MemoryUsed / 1073741824.0).ToString("0.0", CultureInfo.CurrentCulture),
            (t.MemoryTotal / 1073741824.0).ToString("0.0", CultureInfo.CurrentCulture));
        UpdateHeader(t);
        Rebuild();
    }

    /// <summary>Собрать строки и обновить таблицу (пересоздавая только то, что поменялось).</summary>
    private void Rebuild()
    {
        if (_snap is null) return;
        var snap = _snap;
        var wanted = new List<TaskRow>();
        var memTotal = Math.Max(1UL, snap.Totals.MemoryTotal);

        foreach (var sec in new[] { "app", "bg" })
        {
            var keys = _order[sec].Where(k => snap.Groups.TryGetValue(k, out var g) && Matches(g)).ToList();
            if (keys.Count == 0) continue;
            wanted.Add(Row("sec:" + sec, TaskRow.Kinds.Section, r =>
            {
                r.Title = Loc.T(sec == "app" ? "tasks.apps" : "tasks.background");
                r.Count = $"({keys.Count})";
            }));
            foreach (var key in keys)
            {
                var g = snap.Groups[key];
                var v = _values.GetValueOrDefault(key);
                var expanded = _expanded.Contains(key) && g.Pids.Count > 1;
                wanted.Add(Row(key, TaskRow.Kinds.Group, r =>
                {
                    r.Title = g.Title;
                    r.Count = g.Pids.Count > 1 ? $"({g.Pids.Count})" : "";
                    r.Arrow = g.Pids.Count > 1 ? (expanded ? "" : "") : "";
                    r.Icon = IconFor(g.Path);
                    r.Tip = g.Path.Length > 0 ? g.Path : null;
                    Fill(r, v.Cpu, v.Mem, v.Disk, v.Net, memTotal);
                }, groupKey: key));
                if (!expanded) continue;
                foreach (var pid in g.Pids)
                {
                    if (!snap.Procs.TryGetValue(pid, out var p)) continue;
                    wanted.Add(Row($"pid:{pid}", TaskRow.Kinds.Proc, r =>
                    {
                        r.Title = $"{p.Exe}  ·  PID {pid}";
                        r.Icon = IconFor(p.Path);
                        r.Tip = p.Path.Length > 0 ? p.Path : null;
                        Fill(r, p.Cpu, p.Memory, p.Disk, p.Net, memTotal);
                    }, groupKey: key, pid: pid));
                }
            }
        }

        // строки, которых больше нет (процесс закрылся, отфильтровали, свернули), не храним
        if (_rows.Count != wanted.Count)
        {
            _rows.Clear();
            foreach (var r in wanted) _rows[r.Key] = r;
        }
        // та же последовательность строк — только обновить цифры; иначе — пересобрать список
        var same = wanted.Count == _shown.Count && wanted.Zip(_shown).All(x => ReferenceEquals(x.First, x.Second));
        if (!same)
        {
            _shown.Clear();
            foreach (var r in wanted) _shown.Add(r);
        }
        foreach (var r in wanted)
        {
            r.IsSelected = r.Kind != TaskRow.Kinds.Section && r.Key == _selected;
            r.Touch();
        }
        UpdateEndButton();
    }

    private bool Matches(ProcGroup g) =>
        _filter.Length == 0 || g.Title.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
                            || g.Key.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Строка по ключу: та же, что была (чтобы WPF не пересоздавал её), или новая.</summary>
    private TaskRow Row(string key, TaskRow.Kinds kind, Action<TaskRow> fill, string? groupKey = null, int pid = 0)
    {
        if (!_rows.TryGetValue(key, out var r))
        {
            r = new TaskRow { Key = key, Kind = kind, GroupKey = groupKey, Pid = pid };
            _rows[key] = r;
        }
        fill(r);
        return r;
    }

    private ImageSource? IconFor(string path)
    {
        if (path.Length == 0) return null;
        if (_icons.TryGetValue(path, out var img)) return img;
        _icons[path] = null;
        App.Instance.IconLoaderInstance?.Request(path, 32, i => Dispatcher.InvokeAsync(() => _icons[path] = i));
        return null;
    }

    // ---------- оформление ----------
    private static readonly Brush[] Heat = Enumerable.Range(0, 11)
        .Select(i => i == 0 ? Brushes.Transparent : Frozen(Color.FromArgb((byte)(i * 14), 0xFF, 0xC8, 0x3C))).ToArray();

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Brush? HeatBrush(double h) => Heat[(int)Math.Round(Math.Clamp(h, 0, 1) * 10)];

    private static void Fill(TaskRow r, double cpu, long mem, double disk, double net, ulong memTotal)
    {
        r.Cpu = Format("cpu", cpu);
        r.Mem = Format("mem", mem);
        r.Disk = Format("disk", disk);
        r.Net = Format("net", net);
        r.CpuHeat = HeatBrush(cpu / 50);
        r.MemHeat = HeatBrush(mem / (memTotal * 0.15));
        r.DiskHeat = HeatBrush(disk / (20 * 1048576.0));
        r.NetHeat = HeatBrush(net * 8 / (20 * 1e6));
    }

    private static string Format(string col, double v)
    {
        var c = CultureInfo.CurrentCulture;
        return col switch
        {
            "cpu" => v < 0.05 ? "0%" : v.ToString("0.0", c) + "%",
            "mem" => (v / 1048576).ToString("#,0.0", c) + " " + Loc.T("tasks.unit.mb"),
            "disk" => v / 1048576 < 0.05 ? "0 " + Loc.T("tasks.unit.mbs") : (v / 1048576).ToString("0.0", c) + " " + Loc.T("tasks.unit.mbs"),
            _ => v * 8 / 1e6 < 0.05 ? "0 " + Loc.T("tasks.unit.mbits") : (v * 8 / 1e6).ToString("0.0", c) + " " + Loc.T("tasks.unit.mbits"),
        };
    }

    // ---------- выбор и действия ----------
    private void Row_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskRow r || r.Kind == TaskRow.Kinds.Section) return;
        _selected = r.Key;
        if (e.ClickCount == 2 && r.Kind == TaskRow.Kinds.Group)
        {
            if (!_expanded.Remove(r.Key)) _expanded.Add(r.Key);
        }
        else if (r.Kind == TaskRow.Kinds.Group && e.GetPosition((IInputElement)sender).X < 24)
        {
            if (!_expanded.Remove(r.Key)) _expanded.Add(r.Key); // щелчок по стрелке
        }
        Rebuild();
    }

    private TaskRow? SelectedRow => _selected is not null && _rows.TryGetValue(_selected, out var r) && _shown.Contains(r) ? r : null;

    private List<int> SelectedPids()
    {
        var r = SelectedRow;
        if (r is null || _snap is null) return new();
        if (r.Kind == TaskRow.Kinds.Proc) return new() { r.Pid };
        return _snap.Groups.TryGetValue(r.Key, out var g) ? g.Pids.ToList() : new();
    }

    private void UpdateEndButton()
    {
        var r = SelectedRow;
        EndButton.IsEnabled = r is not null;
        var title = r?.Title ?? "";
        if (title.Length > 22) title = title[..21] + "…";
        EndButton.Content = r is null ? Loc.T("tasks.end") : Loc.T("tasks.end_named", title);
    }

    private void End_Click(object sender, RoutedEventArgs e)
    {
        var pids = SelectedPids();
        if (pids.Count == 0) return;
        ProcMonitor.Terminate(pids);
        _selected = null;
        UpdateEndButton();
        Poke();
    }

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskRow r || r.Kind == TaskRow.Kinds.Section || _snap is null) return;
        e.Handled = true;
        _selected = r.Key;
        Rebuild();
        var snap = _snap;
        var pids = SelectedPids();
        var path = r.Tip ?? "";
        var mainPid = r.Kind == TaskRow.Kinds.Proc ? r.Pid : pids.FirstOrDefault();

        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        void Add(string text, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        Add(Loc.T("tasks.end"), () => End_Click(this, new RoutedEventArgs()));
        Add(Loc.T("tasks.menu.tree"), () =>
        {
            var all = pids.SelectMany(p => ProcMonitor.Tree(snap, p)).Distinct().ToList();
            ProcMonitor.Terminate(all);
            Poke();
        });
        if (r.Kind == TaskRow.Kinds.Group && r.Count.Length > 0)
            Add(Loc.T(_expanded.Contains(r.Key) ? "tasks.menu.collapse" : "tasks.menu.expand"), () =>
            {
                if (!_expanded.Remove(r.Key)) _expanded.Add(r.Key);
                Rebuild();
            });
        menu.Items.Add(new Separator());
        Add(Loc.T("tasks.menu.location"), () =>
        {
            App.Instance.Overlay?.HideOverlay();
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")); } catch { }
        }, path.Length > 0);
        Add(Loc.T("tasks.menu.copy_path"), () =>
        {
            try { Clipboard.SetText(path); } catch { }
        }, path.Length > 0);
        Add(Loc.T("tasks.menu.search"), () =>
        {
            var name = Path.GetFileName(path.Length > 0 ? path : r.Title);
            App.Instance.Overlay?.HideOverlay();
            Shell.Open("https://www.google.com/search?q=" + Uri.EscapeDataString(name));
        });
        menu.Items.Add(new Separator());
        var cur = mainPid != 0 ? ProcMonitor.GetPriority(mainPid) : null;
        foreach (var (prio, key) in new[]
                 {
                     (ProcessPriorityClass.High, "high"), (ProcessPriorityClass.AboveNormal, "above"),
                     (ProcessPriorityClass.Normal, "normal"), (ProcessPriorityClass.BelowNormal, "below"),
                     (ProcessPriorityClass.Idle, "idle"),
                 })
        {
            var p = prio;
            Add((cur == p ? "✓ " : "    ") + Loc.T("tasks.prio." + key), () =>
            {
                foreach (var pid in pids) ProcMonitor.SetPriority(pid, p);
            }, cur is not null);
        }
        menu.IsOpen = true;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text.Trim();
        Rebuild();
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.Overlay?.HideOverlay();
        Shell.Open("shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}"); // окно «Выполнить»
    }

    private void WindowsTaskManager_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.Overlay?.HideOverlay();
        Shell.Open("taskmgr.exe");
    }

    /// <summary>Переход на страницу: строка с кнопками, шапка таблицы и сама таблица появляются «лесенкой».</summary>
    public void Enter() => DesktopPanel.UI.Theme.Fx.Enter(new FrameworkElement[] { TopBar, HeaderGrid, Scroll });
}
