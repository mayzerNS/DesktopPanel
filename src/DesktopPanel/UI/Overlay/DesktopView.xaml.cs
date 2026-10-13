using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPanel.Core.Config;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Logging;
using DesktopPanel.Features.Desktop;
using Anim = DesktopPanel.UI.Theme.Anim;
using AnimWatch = DesktopPanel.UI.Theme.AnimWatch;
using FrameClock = DesktopPanel.UI.Theme.FrameClock;
using Motion = DesktopPanel.UI.Theme.Motion;

namespace DesktopPanel.UI.Overlay;

/// <summary>Плитка: ярлык рабочего стола, группа, корзина или найденное в системе.</summary>
public sealed class TileVm : INotifyPropertyChanged
{
    public enum Kinds { Item, Group, Trash, Hit }

    public TileVm(DesktopItem item) { Item = item; Kind = Kinds.Item; }
    public TileVm(IconGroup group) { Group = group; Kind = Kinds.Group; }
    public TileVm(SearchHit hit) { Hit = hit; Kind = Kinds.Hit; }
    private TileVm() => Kind = Kinds.Trash;

    public static TileVm Trash() => new();

    public Kinds Kind { get; }
    public DesktopItem? Item { get; }
    public IconGroup? Group { get; }
    public SearchHit? Hit { get; }

    /// <summary>Путь файла за плиткой (ярлык или найденное), null — группа и корзина.</summary>
    public string? Path => Item?.Path ?? Hit?.Path;

    /// <summary>Ключ порядка: путь ярлыка или "group:id".</summary>
    public string Key => Kind switch
    {
        Kinds.Group => "group:" + Group!.Id,
        Kinds.Trash => "trash",
        _ => Path!,
    };

    private string _name = "";
    public string Name { get => _name; set { _name = value; OnChanged(); } }

    private string? _tip;
    public string? Tip { get => _tip; set { _tip = value; OnChanged(); } }

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { _icon = value; OnChanged(); } }

    public ObservableCollection<ImageSource?> Previews { get; } = new();

    public Visibility ItemVisibility => Kind == Kinds.Group ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GroupVisibility => Kind == Kinds.Group ? Visibility.Visible : Visibility.Collapsed;

    private bool _drop;
    public bool IsDropTarget
    {
        get => _drop;
        set { if (_drop == value) return; _drop = value; OnChanged(); OnChanged(nameof(DropVisibility)); }
    }
    public Visibility DropVisibility => _drop ? Visibility.Visible : Visibility.Collapsed;

    private bool _dragged;
    /// <summary>Эту плитку сейчас тащат — на её месте пусто.</summary>
    public bool IsDragged
    {
        get => _dragged;
        set { _dragged = value; OnChanged(nameof(TileOpacity)); }
    }
    public double TileOpacity => _dragged ? 0 : 1; // место, откуда тащат, пустое — значок только «в руке"

    private bool _noHover;
    /// <summary>Плитку только что бросили и она села под курсор: подсветку наведения не показываем, пока курсор с неё не уйдёт
    /// (иначе в момент остановки фон под значком вспыхивал — «блик»).</summary>
    public bool NoHover { get => _noHover; set { if (_noHover == value) return; _noHover = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Рабочий стол в панели: ярлыки, группы (перетащите значок на середину другого), свой порядок (перетащите между
/// значками; «Упорядочить» — снова по алфавиту), корзина (перетащите значок на неё), поиск по рабочему столу и системе
/// (найденное можно перетащить на рабочий стол), меню по правой кнопке, отмена Ctrl+Z.
/// Файлы на рабочем столе меняются только при удалении в корзину и добавлении ярлыка из поиска.
/// </summary>
public partial class DesktopView : UserControl
{
    private IconLoader? _loader;
    private IconStore _store = null!;
    private readonly List<DesktopItem> _items = new();
    private readonly Dictionary<string, ImageSource?> _shellIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<TileVm> _tiles = new();
    private readonly ObservableCollection<TileVm> _groupTiles = new();
    private readonly ObservableCollection<TileVm> _apps = new();
    private readonly ObservableCollection<TileVm> _files = new();
    private string _filter = "";
    private string? _openGroupId;
    private string? _renamePath, _renameGroupId;
    private bool _iconRefreshQueued;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _searchCts;
    private readonly TileVm _trash = TileVm.Trash();

    /// <summary>Корзина — значок в шапке панели (данные для него: значок, подсказка, подсветка при перетаскивании).</summary>
    public TileVm TrashVm => _trash;

    /// <summary>Кнопка корзины в шапке: на неё бросают значки.</summary>
    public FrameworkElement? TrashTarget { get; set; }

    /// <summary>Запустили ярлык или открыли папку — панель прячется.</summary>
    public event Action? LaunchRequested;

    public DesktopView()
    {
        InitializeComponent();
        Scroll.SizeChanged += (_, _) => FitColumns();
        Loaded += (_, _) => UpdateTileCache();
        TileGrid.ItemsSource = _tiles;
        GroupGrid.ItemsSource = _groupTiles;
        AppsGrid.ItemsSource = _apps;
        FilesGrid.ItemsSource = _files;
        _trash.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(TileVm.Tip): if (TrashZone is not null && !_trashBack) TrashZone.ToolTip = _trash.Tip; break;
                case nameof(TileVm.DropVisibility): UpdateTrashOpen(); break; // на неё тащат значок — подрастает
            }
        };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            FadeToast(false);
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RunSystemSearch(_filter);
        };
        _edge.Tick += (_, _) =>
        {
            _edge.Stop();
            if (_drag is { Started: true, EdgeVm: not null } d)
            {
                d.EdgeReady = true;
                UpdateDragTarget(_lastDragPos);
            }
        };
        _dwell.Tick += (_, _) =>
        {
            _dwell.Stop();
            if (_drag is { Started: true, Hover: not null } d)
            {
                d.Ripe = true;
                UpdateDragTarget(_lastDragPos);
            }
        };
    }

    public void Init(IconLoader loader)
    {
        _loader = loader;
        _store = App.Instance.Icons;
    }

    private AppConfig Cfg => App.Instance.Config;

    /// <summary>
    /// Размер картинки значка: вдвое больше, чем он на экране (не больше 256). Уменьшение с качественным сглаживанием даёт
    /// ровные края: при картинке «точно по размеру» мелкие значки (папка и т.п.) выходили с лесенкой по контуру.
    /// </summary>
    private int IconPx => Math.Clamp((int)Math.Ceiling(Cfg.DesktopIconSize * VisualTreeHelper.GetDpi(this).DpiScaleX * 2), 32, 256);

    /// <summary>Размеры значков по уровням ползунка 1–10 (px).</summary>
    public static readonly int[] IconSteps = { 36, 46, 56, 66, 80 };

    public static int IconLevel(int px)
    {
        int best = 0;
        for (int i = 1; i < IconSteps.Length; i++)
            if (Math.Abs(IconSteps[i] - px) < Math.Abs(IconSteps[best] - px)) best = i;
        return best + 1;
    }

    /// <summary>Размеры сетки и значков из настроек → ресурсы, на которые ссылается разметка плиток.</summary>
    public static void ApplySizes(AppConfig cfg)
    {
        var res = Application.Current.Resources;
        res["IconBox"] = (double)cfg.DesktopIconSize;
        res["TileWidth"] = (double)cfg.DesktopIconSize + 44;
        res["TileTextWidth"] = (double)(cfg.DesktopIconSize + 44 - 12);
        res["TileHeight"] = (double)(cfg.DesktopIconSize + 66); // значок + отступы + две строки подписи с запасом
    }

    /// <summary>
    /// Ширина клетки — ровно по ширине панели: столбцов столько, сколько помещается клеток «значок + 44», и лишнее место
    /// делится между ними (до целого пикселя экрана). Поля слева и справа одинаковые и почти нулевые на любом размере,
    /// крайние плитки стоят вплотную к краю, у которого плитки сжимаются при переезде на другую строку.
    /// </summary>
    public void FitColumnsNow() => FitColumns();

    private void FitColumns()
    {
        var width = Scroll.ActualWidth;
        if (width < 50) return;
        var res = Application.Current.Resources;
        var want = (double)Cfg.DesktopIconSize + 44;
        var cols = Math.Max(1, (int)Math.Floor(width / want));
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var tw = Math.Floor(width / cols * dpi) / dpi; // целые пиксели экрана: клетки не «плывут» на полпикселя
        // отступ сетки сверху (от шапки до подсветки первого ряда) — как сбоку от края окна до подсветки крайних плиток:
        // 7 (поле панели) + 11 (поле страниц, OverlayWindow) + половина остатка ширины (сетка по центру)
        var side = Math.Round((7 + 11 + (width - cols * tw) / 2) * dpi) / dpi;
        if (Math.Abs(TileStack.Margin.Top - side) > 0.01) TileStack.Margin = new Thickness(0, side, 0, 0);
        if (res["TileWidth"] is double cur && Math.Abs(cur - tw) < 0.01) return;
        res["TileWidth"] = tw;
        res["TileTextWidth"] = tw - 12;
    }

    /// <summary>Колесо мыши прокручивает ровно на ряд значков: шаг = высота клетки (она зависит от размера значков).</summary>
    private void UpdateScrollStep()
    {
        if (Application.Current.Resources["TileHeight"] is not double h) return;
        Controls.SmoothScroll.SetStep(Scroll, h);
        if (GroupGrid.Parent is ScrollViewer gs) Controls.SmoothScroll.SetStep(gs, h);
    }

    /// <summary>Поменяли размеры в настройках: значки нужны другого разрешения.</summary>
    public void ResizeIcons()
    {
        ApplySizes(Cfg);
        FitColumns();
        UpdateScrollStep();
        _shellIcons.Clear();
        Reload();
    }

    // ---------- данные ----------
    /// <summary>
    /// Перечитать рабочий стол (при запуске и когда на нём что-то поменялось). animate — на нём что-то удалили или добавили,
    /// пока его видно (например, значок ушёл в корзину): соседи доезжают «паровозиком» с того места, где они сейчас, а не
    /// перескакивают (раньше значки стояли, а потом разом прыгали на новые места).
    /// </summary>
    /// <param name="ifChanged">пересобрать, только если список на рабочем столе поменялся (слежение за рабочим столом:
    /// после броска в корзину сетка уже сомкнута — пустая пересборка стоила 50–80 мс)</param>
    public void Reload(bool animate = false, bool ifChanged = false)
    {
        // Значок в руке (или на него только что нажали) — сетку НЕ пересобираем, а перечитаем после броска (FlushReload).
        // Пересборка создаёт плитки заново: перетаскивание оставалось со старой плиткой, а в сетке появлялась новая плитка
        // того же значка (он был и в руке, и в сетке), сетка прыгала. Слежение за рабочим столом присылает «изменилось»
        // через 0,5 с после удаления — как раз когда тащат следующий значок.
        // То же, пока значок-призрак летит (в корзину, в папку, на место): пересборка посреди полёта создаёт плитки заново
        // под летящим значком. Панель скрыта — полёт не ждём, а доводим до конца сразу.
        if (_flightStep is not null && !IsVisible) CancelFlight();
        // ⚠ не пересобирать при значке в руке, в полёте и внутри другой пересборки — только отложить (FlushReload)
        if (_reloading || _drag is { Started: true } || (_drag is not null && Mouse.LeftButton == MouseButtonState.Pressed)
            || _flightStep is not null)
        {
            _reloadPending = true;
            _reloadAnimate |= animate;
            _reloadForce |= !ifChanged;
            return;
        }
        if (_drag is not null) _drag = null; // нажатие без перетаскивания давно кончилось где-то ещё
        _reloadPending = false;
        _reloadAnimate = false;
        _reloadForce = false;
        _reloading = true; // пересборка внутри пересборки (что-то прокачало сообщения) — откладывается, а не идёт поверх
        try { ReloadCore(animate, ifChanged); }
        finally { _reloading = false; }
        FlushReload();
    }

    private bool _reloading;

    private void ReloadCore(bool animate, bool ifChanged = false)
    {
        UpdateScrollStep();
        var items = DesktopScanner.Scan();
        // значки, которые бросили в корзину, но файл ещё не удалён (полёт, удаление следом): их уже нет — иначе пересборка
        // находила файл, раздвигала сетку обратно под него, а после удаления смыкала снова («туда-сюда по конвейеру»)
        if (_deleting.Count > 0) items = items.Where(i => !_deleting.Contains(i.Path)).ToList();
        if (ifChanged && SameItems(items, _items)) return; // на экране уже то же самое
        // ⚠ неизменившиеся ярлыки — прежними объектами: по ним пересборка узнаёт свои плитки и оставляет их (_reuse).
        // С новыми объектами после каждого удаления все плитки создавались заново, и значки на миг мигали
        var old = new Dictionary<string, DesktopItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in _items) old.TryAdd(it.Path, it);
        for (var i = 0; i < items.Count; i++)
            if (old.TryGetValue(items[i].Path, out var was) && was.Name == items[i].Name && was.IsFolder == items[i].IsFolder)
                items[i] = was;
        _items.Clear();
        _items.AddRange(items);
        _store.Prune(items.Select(i => i.Path).ToList());
        if (animate && IsVisible && _filter.Length == 0 && Motion.Enabled) RebuildTrain();
        else Rebuild();

        var px = IconPx;
        foreach (var item in items)
        {
            var path = item.Path;
            _loader?.Request(path, px, img => Dispatcher.InvokeAsync(() =>
            {
                _shellIcons[path] = img;
                QueueIconRefresh();
            }));
        }
        Log.Info($"desktop: {items.Count} ярлыков, групп {_store.Groups.Count}");
    }

    private bool _reloadPending, _reloadAnimate, _reloadForce;

    private static bool SameItems(List<DesktopItem> a, List<DesktopItem> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].Path, b[i].Path, StringComparison.OrdinalIgnoreCase) || a[i].Name != b[i].Name
                || a[i].IsFolder != b[i].IsFolder) return false;
        return true;
    }

    /// <summary>Перечитать рабочий стол, если это откладывали, пока значок был в руке (см. Reload).</summary>
    private void FlushReload()
    {
        if (!_reloadPending || _drag is not null || _flightStep is not null) return;
        // после того, как бросок полностью обработан (полёт, посадка, удаление уже запущены)
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_reloadPending && _drag is null && _flightStep is null) Reload(_reloadAnimate, ifChanged: !_reloadForce);
        });
    }

    private ImageSource? IconFor(string path) =>
        _store.CustomImage(path) ?? (_shellIcons.TryGetValue(path, out var i) ? i : null);

    private string NameFor(DesktopItem item) => _store.CustomName(item.Path) ?? item.Name;

    private DesktopItem? ItemByPath(string path) =>
        _items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Плитки, которые пересборка может оставить как есть (RebuildTrain без вылетов): ключ → прежняя плитка.
    /// ⚠ Раньше каждая пересборка создавала все плитки заново, и первый кадр после неё (20–50 мс: два десятка новых плиток,
    /// у каждой своя картинка-кэш) значки на миг пропадали — после каждого добавления в папку. Теперь заново создаются
    /// только новые (папка), остальные остаются и едут «паровозиком» со своих мест.
    /// </summary>
    private Dictionary<string, TileVm>? _reuse;

    private TileVm MakeItemTile(DesktopItem item)
    {
        var custom = _store.CustomName(item.Path);
        if (_reuse is not null && _reuse.TryGetValue(item.Path, out var old) && ReferenceEquals(old.Item, item))
        {
            if (old.Name != (custom ?? item.Name)) old.Name = custom ?? item.Name;
            var tip = custom is not null ? item.Name : null;
            if (old.Tip != tip) old.Tip = tip;
            var icon = IconFor(item.Path);
            if (!ReferenceEquals(old.Icon, icon)) old.Icon = icon;
            old.IsDragged = item.Path == _landKey || _unborn?.Contains(item.Path) == true; // прежняя плитка видна: она уже на экране
            return old;
        }
        return new TileVm(item)
        {
            Name = custom ?? item.Name,
            Tip = custom is not null ? item.Name : null,
            Icon = IconFor(item.Path),
            IsDragged = item.Path == _landKey || _unbornAll || _unborn?.Contains(item.Path) == true, // значок ещё летит на это место / ещё не «родился»
        };
    }

    private TileVm MakeGroupTile(IconGroup g)
    {
        if (_reuse is not null && _reuse.TryGetValue("group:" + g.Id, out var old) && ReferenceEquals(old.Group, g))
        {
            if (old.Name != g.Name) old.Name = g.Name;
            var previews = g.Items.Take(4).Select(IconFor).ToList();
            if (_holdKey == old.Key && _holdIdx < previews.Count) previews[_holdIdx] = null; // этот значок ещё летит
            if (!old.Previews.SequenceEqual(previews))
            {
                old.Previews.Clear();
                foreach (var pv in previews) old.Previews.Add(pv);
            }
            old.IsDragged = old.Key == _landKey;
            return old;
        }
        var vm = new TileVm(g) { Name = g.Name, IsDragged = "group:" + g.Id == _landKey || _unbornAll };
        foreach (var p in g.Items.Take(4)) vm.Previews.Add(IconFor(p));
        if (_holdKey == vm.Key && _holdIdx < vm.Previews.Count) vm.Previews[_holdIdx] = null; // этот значок ещё летит
        return vm;
    }

    /// <summary>
    /// Пересобрать плитки. Группы стоят на месте своего первого значка, дальше — свой порядок (если его задали
    /// перетаскиванием). Корзина — первой. При поиске групп и корзины нет — только подходящие ярлыки.
    /// </summary>
    private void Rebuild()
    {
        var perf = Stopwatch.StartNew();
        try { RebuildCore(); }
        finally
        {
            if (perf.Elapsed.TotalMilliseconds > 8) PerfLog.Note($"пересборка плиток {perf.Elapsed.TotalMilliseconds:0.0} мс ({_tiles.Count} плиток)");
        }
    }

    /// <summary>Пересборка должна создать все плитки заново (вылеты и влёты RebuildTrain: плитки рождаются невидимыми).</summary>
    private bool _freshTiles;

    private void RebuildCore()
    {
        // ⚠ Обычная пересборка (переименовали, сменили значок, «Упорядочить», вынули из папки…) тоже оставляет прежние плитки,
        // а не создаёт все заново — иначе значки на миг мигали (см. _reuse). Плитки на ходу сразу встают на места: раньше их
        // просто пересоздавали на новых местах, лента со старыми целями сдвинула бы их не туда.
        var own = _reuse is null && !_freshTiles && _filter.Length == 0;
        if (own)
        {
            _reuse = new Dictionary<string, TileVm>(StringComparer.OrdinalIgnoreCase);
            foreach (var vm in _tiles) _reuse.TryAdd(vm.Key, vm);
            foreach (var t in _tapeList.ToList())
                if (t.C.DataContext is TileVm tv && _reuse.ContainsKey(tv.Key)) TapeEnd(t);
        }
        try { RebuildTiles(); }
        finally { if (own) _reuse = null; }
    }

    private void RebuildTiles()
    {
        _ungroupStop?.Invoke(); // идущее расформирование: плитки сейчас пересоздадутся, призрак папки убираем
        UpdateTrashVisibility();
        if (_reuse is null || _filter.Length > 0) _tiles.Clear();
        if (_filter.Length > 0)
        {
            foreach (var it in _items.Where(i => NameFor(i).Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
                                                 || i.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)))
                _tiles.Add(MakeItemTile(it));
        }
        else
        {
            var list = new List<TileVm>();
            var shown = new HashSet<string>();
            foreach (var it in _items)
            {
                var g = _store.GroupOf(it.Path);
                if (g is null) list.Add(MakeItemTile(it));
                else if (shown.Add(g.Id)) list.Add(MakeGroupTile(g));
            }
            // группы, у которых пока нет значков (созданные из меню), — тоже видны
            foreach (var g in _store.Groups)
                if (shown.Add(g.Id)) list.Add(MakeGroupTile(g));
            var order = _store.Order;
            if (order.Count > 0)
            {
                var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < order.Count; i++) index.TryAdd(order[i], i);
                list = list.OrderBy(t => index.TryGetValue(t.Key, out var i) ? i : int.MaxValue).ToList(); // стабильная сортировка
            }
            if (Cfg.ShowRecycleBin && !_skipTrashUpdate) UpdateTrash();
            if (_reuse is null) foreach (var t in list) _tiles.Add(t);
            else Reconcile(list);
        }
        UpdateNothingFound();
        var reuse = _reuse;
        _reuse = null; // ⚠ плитки открытой папки — свои: плитка из общей сетки в них не годится
        try { RebuildGroupPopup(); }
        finally { _reuse = reuse; }
        QueueSweep();
    }

    /// <summary>Привести _tiles к списку, не трогая плитки, которые в нём остались (их контейнеры на экране сохраняются).</summary>
    private void Reconcile(List<TileVm> list)
    {
        var keep = new HashSet<TileVm>(list);
        for (var i = _tiles.Count - 1; i >= 0; i--)
            if (!keep.Contains(_tiles[i])) _tiles.RemoveAt(i);
        for (var i = 0; i < list.Count; i++)
        {
            if (i < _tiles.Count && ReferenceEquals(_tiles[i], list[i])) continue;
            var at = _tiles.IndexOf(list[i]);
            if (at >= 0) _tiles.Move(at, i);
            else _tiles.Insert(i, list[i]);
        }
    }

    /// <summary>
    /// Страховка от «пустой клетки»: когда ничего не тащат, не летит и не едет, невидимых плиток быть не должно
    /// (после быстрой череды бросков в корзину одна плитка однажды осталась невидимой).
    /// </summary>
    private void SweepHidden()
    {
        if (_drag is not null || _flightStep is not null || _ungroupStop is not null || _tapeList.Count > 0 || _trainBusy > 0 || Anim.IsRunning(Ghost, "land")) return;
        _landKey = null;
        foreach (var vm in _tiles.Concat(_groupTiles))
            if (vm.IsDragged) vm.IsDragged = false;
    }

    private void QueueSweep() => Dispatcher.BeginInvoke(SweepHidden, DispatcherPriority.ContextIdle);

    private void UpdateNothingFound() =>
        NothingFound.Visibility = _filter.Length > 0 && _tiles.Count == 0 && _apps.Count == 0 && _files.Count == 0
                                  && !_searching ? Visibility.Visible : Visibility.Collapsed;

    private void QueueIconRefresh()
    {
        if (_iconRefreshQueued) return;
        _iconRefreshQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _iconRefreshQueued = false;
            foreach (var vm in _tiles.Concat(_groupTiles))
            {
                // только то, что поменялось: иначе каждое перечитывание рабочего стола перерисовывало все плитки (мигание)
                if (vm.Item is { } it)
                {
                    var icon = IconFor(it.Path);
                    if (!ReferenceEquals(vm.Icon, icon)) vm.Icon = icon;
                }
                else if (vm.Group is { } g)
                {
                    if (_holdKey == vm.Key) continue; // мини-значок ещё летит
                    var previews = g.Items.Take(4).Select(IconFor).ToList();
                    if (vm.Previews.SequenceEqual(previews)) continue;
                    vm.Previews.Clear();
                    foreach (var p in previews) vm.Previews.Add(p);
                }
            }
        }, DispatcherPriority.Background);
    }

    // ---------- корзина ----------
    private bool _trashUpdating;
    private string _trashInfo = "";

    public void OpenTrashFromHeader() => OpenTrash();

    // ---------- корзина в шапке (сама плашка — в OverlayWindow, ведём её отсюда) ----------
    private Border TrashZone = null!;
    private Viewbox TrashGlyph = null!;
    private RotateTransform TrashLidTurn = null!;
    private TranslateTransform TrashLidLift = null!;
    private Viewbox BackGlyph = null!;
    private TranslateTransform BackShift = null!;
    private FrameworkElement? _headerPlate, _headerBg; // шапка окна (содержимое) и её фон (подложка)
    private TranslateTransform? _titleShift;           // смещение надписи шапки
    private bool _trashHover, _trashBack;

    /// <summary>На других вкладках плашка корзины — кнопка «Назад»: её нажали.</summary>
    public event Action? BackRequested;

    /// <summary>Корзина из шапки окна: плашка, значок, крышка и стрелка «Назад» (для других вкладок).</summary>
    public void AttachTrash(Border zone, Viewbox glyph, RotateTransform lidTurn, TranslateTransform lidLift, Viewbox back, TranslateTransform backShift,
        FrameworkElement headerPlate, FrameworkElement headerBg, TranslateTransform titleShift, UIElement pressLayer, FrameworkElement glyphs, UIElement hoverLayer)
    {
        _trashPressLayer = pressLayer;
        _trashHoverLayer = hoverLayer;
        _trashGlyphs = glyphs;
        _headerPlate = headerPlate;
        _headerBg = headerBg;
        _titleShift = titleShift;
        TrashZone = zone;
        TrashGlyph = glyph;
        BackGlyph = back;
        BackShift = backShift;
        TrashLidTurn = lidTurn;
        TrashLidLift = lidLift;
        TrashTarget = zone;
        DesktopPanel.UI.Theme.Tips.SetInfo(zone, true); // сколько в корзине — полезно и в минималистичных подсказках
        zone.ToolTip = _trash.Tip;
        // щелчок — как у кнопок шапки и свёрнутой лупы: нажатие захватывает мышь (слой нажатия, значок вдавливается),
        // срабатывает по отпусканию над плашкой; увели мышь — не сработало. Раньше хватало одного отпускания над ней
        zone.MouseLeftButtonDown += TrashZone_Down;
        zone.MouseLeftButtonUp += TrashZone_Up;
        zone.LostMouseCapture += (_, _) =>
        {
            if (!_trashPressed) return;
            _trashPressed = false;
            UpdateTrashPress();
        };
        zone.MouseMove += (_, _) => { if (_trashPressed) UpdateTrashPress(); }; // мышь у плашки: ушли с ней — слой гаснет
        zone.ToolTipOpening += NoTipWhileMoving;
        zone.MouseRightButtonUp += TrashZone_RightClick;
        // наведение — чуть подрастает и приоткрывает крышку
        zone.MouseEnter += (_, _) => { _trashHover = true; UpdateTrashOpen(); UpdateTrashPress(); };
        zone.MouseLeave += (_, _) => { _trashHover = false; UpdateTrashOpen(); UpdateTrashPress(); };
        UpdateTrashVisibility();
    }

    private UIElement? _trashPressLayer, _trashHoverLayer;
    private FrameworkElement? _trashGlyphs;
    private bool _trashPressed;

    /// <summary>
    /// Сетка в движении (значок в руке, летит, едет «паровозик» или пересборка) — подсказку корзины и плитки не открываем.
    /// Открытие подсказки — новое окно (30–65 мс в потоке интерфейса, в perf.log — задание таймера подсказок): курсор после
    /// броска остаётся на корзине, и её подсказка открывалась посреди смыкания сетки — значки на миг вставали.
    /// </summary>
    private void NoTipWhileMoving(object sender, ToolTipEventArgs e)
    {
        if (_drag is not null || _flightStep is not null || _tapeList.Count > 0 || _trainBusy > 0 || _ungroupStop is not null
            || _trashStep is not null || _deleting.Count > 0) // и пока корзина сжимается после броска и файл ещё удаляется
            e.Handled = true;
    }

    private void Tile_ToolTipOpening(object sender, ToolTipEventArgs e) => NoTipWhileMoving(sender, e);

    private void TrashZone_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _trashPressed = TrashZone.CaptureMouse();
        UpdateTrashPress();
    }

    private void TrashZone_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_trashPressed) return;
        e.Handled = true;
        var over = new Rect(TrashZone.RenderSize).Contains(e.GetPosition(TrashZone));
        _trashPressed = false;
        TrashZone.ReleaseMouseCapture();
        UpdateTrashPress();
        if (!over) return;
        if (_trashBack) BackRequested?.Invoke();
        else OpenTrash();
    }

    /// <summary>Нажата и курсор над плашкой — слой нажатия и вдавленный значок (Fx, как у кнопок).</summary>
    private void UpdateTrashPress()
    {
        if (_trashPressLayer is null || _trashGlyphs is null) return;
        var down = _trashPressed && new Rect(TrashZone.RenderSize).Contains(Mouse.GetPosition(TrashZone));
        DesktopPanel.UI.Theme.Fx.SetShow(_trashPressLayer, down);
        if (_trashHoverLayer is not null) DesktopPanel.UI.Theme.Fx.SetShow(_trashHoverLayer, _trashBack && (_trashHover || _trashPressed));
        DesktopPanel.UI.Theme.Fx.SetPress(_trashGlyphs, down);
    }

    /// <summary>
    /// Плашка в режиме «Назад» (другая вкладка) или «Корзина» (рабочий стол): значки плавно сменяют друг друга —
    /// уходящий тает, приходящий проявляется, стрелка чуть «подъезжает» справа (тем же ходом, что у плиток).
    /// </summary>
    public void SetTrashBack(bool back)
    {
        if (TrashZone is null) return;
        var changed = back != _trashBack;
        _trashBack = back;
        TrashZone.ToolTip = back ? Loc.T("overlay.back") : _trash.Tip;
        UpdateTrashVisibility();
        if (!changed) return;
        UpdateTrashOpen(); // стала «Назад» под курсором — не остаётся выросшей
        UpdateTrashPress();
        var fade = Motion.UiMs * 0.5;
        Viewbox show = back ? BackGlyph : TrashGlyph, hide = back ? TrashGlyph : BackGlyph;
        show.Visibility = Visibility.Visible;
        Anim.To(show, OpacityProperty, 1, fade, Motion.EaseOut());
        Anim.To(hide, OpacityProperty, 0, fade, Motion.EaseOut(),
            () => { if (_trashBack == (hide == TrashGlyph)) hide.Visibility = Visibility.Collapsed; });
        if (back) Anim.To(BackShift, TranslateTransform.XProperty, 0, Motion.UiMs, Motion.UiEaseDamped(), from: 4);
    }

    /// <summary>Плашка видна в полной панели: на рабочем столе — если корзина включена в настройках, на других вкладках — всегда («Назад»).</summary>
    public void UpdateTrashVisibility()
    {
        if (TrashZone is null) return;
        var show = (_trashBack || Cfg.ShowRecycleBin) && App.Instance?.Overlay?.IsMini != true;
        TrashZone.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SetTrashScale(show ? _trashScale : 1);
    }

    private void TrashZone_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_trashBack) return; // у «Назад» меню нет
        ShowTrashMenu(TrashZone);
    }

    private const double TrashGap = 4; // сколько не доходит до ближайшего значка

    /// <summary>
    /// Во сколько раз плашка может вырасти (от левого верхнего угла вправо и вниз): вниз — до значков первой строки рабочего стола
    /// без TrashGap (считаем по самому значку плитки, а не по её краю), поэтому рост «совсем чуть-чуть».
    /// </summary>
    private double TrashGrow()
    {
        try
        {
            var first = _tiles.Select(v => TileGrid.ItemContainerGenerator.ContainerFromItem(v)).OfType<FrameworkElement>().FirstOrDefault();
            var trashTop = TrashZone.TranslatePoint(new Point(), this).Y;
            var below = first is not null && FindFirst<Viewbox>(first) is { } vb
                ? vb.TranslatePoint(new Point(), this).Y - OffsetOf(first).Y
                : TileGrid.TranslatePoint(new Point(), this).Y + 9; // значков нет — как будто есть
            // до значков — предел, но растём только на 40 % этого (в 2,5 раза меньше): иначе выросшая плашка кое-где заходила не туда
            var limit = Math.Clamp((below - TrashGap - trashTop) / TrashBase, 1, 1.6);
            return 1 + (limit - 1) * 0.4;
        }
        catch { return 1.08; }
    }

    private const double TrashBase = 48, TrashGlyphBase = 26;
    private const double TrashLidAngle = 28; // насколько открыта крышка у полностью выросшей плашки, градусы
    private double _trashGrowTo = 1.1;       // до скольких раз растёт плашка сейчас (для крышки)
    private double _trashScale = 1;          // текущий размер плашки (1 — обычный)
    private EventHandler? _trashStep;

    private ScaleTransform? _trashSt, _headerBgSt;

    /// <summary>
    /// Размер плашки k — только смещениями и масштабом отрисовки, без перестройки раскладки: раньше каждый кадр роста менял
    /// ширину плашки и поле подложки шапки, WPF заново раскладывал шапку — кадры шли по 10–20 мс, а кнопки дрожали.
    /// Плашка растёт от левого верхнего угла (значок и крышка — вместе с ней), подложка шапки сжимается к правому краю
    /// на столько же, на сколько плашка выросла вширь, надпись сдвигается.
    /// </summary>
    private void SetTrashScale(double k)
    {
        if (TrashZone is null) return;
        _trashScale = k;
        var w = TrashBase * k;
        if (_trashSt is null)
        {
            _trashSt = new ScaleTransform(1, 1);
            TrashZone.RenderTransformOrigin = new Point(0, 0);
            TrashZone.RenderTransform = _trashSt;
        }
        _trashSt.ScaleX = _trashSt.ScaleY = k;
        // Шапка начинается за корзиной с отступом 7 (как от шапки до края окна); корзины нет — с самого края.
        // Раскладку содержимого шапки во время роста НЕ меняем (поле — только по обычному размеру корзины): иначе каждый кадр
        // всё в ней заново округлялось до пикселей и кнопки дрожали. Двигаются только левый край подложки-фона (в ней пусто)
        // и надпись — смещением отрисовки.
        var shown = TrashZone.Visibility == Visibility.Visible;
        // ⚠ поля шапки — только по обычному размеру корзины, не по k: смена раскладки каждый кадр — кнопки дрожали
        var left = shown ? TrashBase + 7 : 0;
        if (_headerPlate is not null && Math.Abs(_headerPlate.Margin.Left - left) > 0.01) _headerPlate.Margin = new Thickness(left, 0, 0, 0);
        if (_headerBg is not null)
        {
            var bgLeft = shown ? TrashBase + 7 : 0.0;
            if (Math.Abs(_headerBg.Margin.Left - bgLeft) > 0.01) _headerBg.Margin = new Thickness(bgLeft, 0, 0, 0);
            if (_headerBgSt is null)
            {
                _headerBgSt = new ScaleTransform(1, 1);
                _headerBg.RenderTransformOrigin = new Point(1, 0.5);
                _headerBg.RenderTransform = _headerBgSt;
            }
            var bw = _headerBg.ActualWidth;
            _headerBgSt.ScaleX = shown && bw > 1 ? Math.Max(0.5, (bw - (w - TrashBase)) / bw) : 1;
        }
        if (_titleShift is not null) _titleShift.X = shown ? w - TrashBase : 0;
        // крышка привязана к росту: 0 — закрыта, плашка выросла до конца — открыта (с перелётом роста чуть больше, но не «внутрь»)
        var open = Math.Clamp((k - 1) / Math.Max(0.05, _trashGrowTo - 1), 0, 1.15);
        TrashLidTurn.Angle = -TrashLidAngle * open; // против часовой: петля слева, поднимается правый край
        TrashLidLift.Y = -1.4 * open;
    }

    /// <summary>Плашка подрастает при наведении и когда на неё тащат значок; иначе возвращается.</summary>
    /// <summary>
    /// Корзина подрастает к значкам при наведении и когда на неё тащат значок. «Назад» не растёт — у неё слой наведения,
    /// как у кнопок шапки: рост сдвигал надпись шапки и делал стрелку толще (штрих масштабировался вместе с плашкой).
    /// </summary>
    private void UpdateTrashOpen() => AnimateTrash((_trashHover && !_trashBack) || _trash.IsDropTarget ? TrashGrow() : 1);

    /// <summary>Плавно к размеру to — без перелёта (с ним плашка и шапка «дрожали»), по кадрам; then — после.</summary>
    private void AnimateTrash(double to, double ms = DesktopPanel.UI.Theme.Motion.UiMs, Action? then = null)
    {
        if (TrashZone is null) return;
        if (_trashStep is not null) FrameClock.Rendering -= _trashStep;
        if (to > 1) _trashGrowTo = to;
        var from = _trashScale;
        var ease = Motion.UiEaseDamped();
        var sw = AnimWatch.StartNew();
        EventHandler? step = null;
        step = (_, _) =>
        {
            PerfLog.AnimTick("корзина");
            var p = Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
            SetTrashScale(from + (to - from) * ease.Ease(p));
            if (p < 1) return;
            FrameClock.Rendering -= step;
            if (_trashStep == step) _trashStep = null;
            then?.Invoke();
        };
        _trashStep = step;
        FrameClock.Rendering += step;
    }

    /// <summary>В корзину что-то попало: плашка вырастает и возвращается.</summary>
    private void PulseTrash() => AnimateTrash(TrashGrow(), 160, () => AnimateTrash(_trashHover ? TrashGrow() : 1));

    public void ShowTrashMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        menu.Items.Add(new MenuItem { Header = _trashInfo.Length > 0 ? _trashInfo : Loc.T("desktop.trash"), IsEnabled = false });
        menu.Items.Add(new Separator());
        var open = new MenuItem { Header = Loc.T("desktop.menu.open") };
        open.Click += (_, _) => OpenTrash();
        menu.Items.Add(open);
        var inExplorer = new MenuItem { Header = Loc.T("desktop.trash.in_explorer") };
        inExplorer.Click += (_, _) => OpenTrashInExplorer();
        menu.Items.Add(inExplorer);
        var empty = new MenuItem { Header = Loc.T("desktop.trash.empty_menu"), IsEnabled = _trashInfo != Loc.T("desktop.trash.empty") };
        empty.Click += (_, _) => EmptyTrash();
        menu.Items.Add(empty);
        menu.IsOpen = true;
        UpdateTrash();
    }

    /// <summary>Сколько в корзине и её значок — в фоне (опрос корзины бывает медленным).</summary>
    private void UpdateTrash()
    {
        var trash = _trash;
        if (_trashUpdating) return;
        _trashUpdating = true;
        trash.Name = Loc.T("desktop.trash");
        Task.Run(() =>
        {
            var (count, bytes) = RecycleBin.Info();
            Dispatcher.InvokeAsync(() =>
            {
                _trashUpdating = false;
                var info = count == 0 ? Loc.T("desktop.trash.empty")
                    : Loc.T("desktop.trash.info", Loc.Plural(count, "desktop.trash.items"), FormatBytes(bytes));
                _trashInfo = info;
                trash.Tip = Loc.T("desktop.trash") + " — " + info;
            });
        });
    }

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} {Loc.T("unit.gb")}",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} {Loc.T("unit.mb")}",
        _ => $"{Math.Max(1, b / 1024)} {Loc.T("unit.kb")}",
    };

    /// <summary>Открыть корзину: своя страница панели (таблица, выделение, восстановление).</summary>
    private void OpenTrash() => App.Instance.Overlay?.ShowPage(OverlayWindow.PageTrash);

    /// <summary>Свою корзину изменили (восстановили, стёрли) — обновить подсказку и значок.</summary>
    public void RefreshTrashInfo() => UpdateTrash();

    private void OpenTrashInExplorer()
    {
        LaunchRequested?.Invoke();
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")); }
        catch (Exception ex) { Log.Warn($"desktop: корзину не открыть: {ex.Message}"); }
    }

    /// <summary>Очистить корзину (меню корзины): сколько в ней и сама очистка — в фоне, как на странице корзины.</summary>
    private void EmptyTrash()
    {
        // ⚠ оболочку (корзину) не звать посреди щелчка по пункту меню: она прокачивает сообщения — в фон или следом
        Task.Run(RecycleBin.Info).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            var count = t.IsFaulted ? 0 : t.Result.Count;
            if (count == 0) return;
            App.Instance.Overlay?.Confirm(Loc.T("desktop.trash.empty_q"), Loc.T("desktop.trash.empty_text", Loc.Plural(count, "desktop.trash.items")),
                Loc.T("desktop.trash.empty_ok"), _ =>
                {
                    App.Instance.Overlay?.ShowPage(OverlayWindow.PageDesktop);
                    Task.Run(RecycleBin.Empty).ContinueWith(_ => Dispatcher.InvokeAsync(UpdateTrash));
                });
        }));
    }

    private readonly HashSet<string> _deleting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Отправить в корзину файл с рабочего стола — в своём потоке (RecycleBin.DeleteInBackground): оболочка больше не
    /// держит поток интерфейса (было 80–90 мс на значок) и не прокачивает его сообщения посреди шага анимации.
    /// Пока удаляется — значка нет (_deleting: пересборка его не показывает). done(ok) — в потоке интерфейса, когда
    /// удаление закончилось; список потом обновит слежение за рабочим столом.
    /// </summary>
    private void DeleteItem(DesktopItem item, bool pulse = true, Action<bool>? done = null)
    {
        var path = item.Path;
        _deleting.Add(path);
        RecycleBin.DeleteInBackground(new[] { path }, n => Dispatcher.InvokeAsync(() =>
        {
            _deleting.Remove(path); // дальше файла нет (или он остался — тогда значок возвращается)
            var ok = n > 0;
            if (ok)
            {
                if (pulse) PulseTrash();
                UpdateTrash();
                // плитки уже нет на экране (сетка сомкнулась над корзиной) — и в списке её нет: слежение за рабочим столом
                // увидит то же самое и не станет пересобирать сетку впустую
                if (_store.GroupOf(path) is null && !_tiles.Concat(_groupTiles).Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)))
                    _items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            }
            else ShowToast(Loc.T("desktop.trash.failed"));
            done?.Invoke(ok);
        }));
    }

    private void DeleteGroup(IconGroup g)
    {
        App.Instance.Overlay?.Confirm(Loc.T("desktop.trash.group_q", g.Name),
            Loc.T("desktop.trash.group_text", Loc.Plural(g.Items.Count, "desktop.trash.items")),
            Loc.T("desktop.trash.delete"), _ =>
            {
                var paths = g.Items.ToList();
                App.Instance.Overlay?.ShowPage(OverlayWindow.PageDesktop);
                // удаление через оболочку — в своём потоке (не держит и не прокачивает поток интерфейса).
                // В _deleting их не кладём: пересборка в промежутке выкинула бы их из группы (Prune) насовсем, даже если
                // удалить не выйдет; сетку обновит слежение за рабочим столом
                RecycleBin.DeleteInBackground(paths, n => Dispatcher.InvokeAsync(() =>
                {
                    if (n > 0) PulseTrash();
                    UpdateTrash();
                }));
            });
    }

    // ---------- поиск ----------
    private bool _searching;

    public void SetFilter(string text)
    {
        if (text == _filter) return;
        PerfLog.Mark("поиск: новый запрос");
        _filter = text;
        if (text.Length > 0) CloseGroup();
        Rebuild();

        _searchCts?.Cancel();
        _searchTimer.Stop();
        _apps.Clear();
        _files.Clear();
        _searching = false;
        var system = text.Length > 1 && Cfg.SearchSystem;
        SearchSection.Visibility = system ? Visibility.Visible : Visibility.Collapsed;
        SearchStatus.Text = "";
        if (system)
        {
            _searching = true;
            SearchStatus.Text = Loc.T("desktop.search.searching");
            _searchTimer.Stop();
            _searchTimer.Start();
        }
        UpdateSearchTitles();
        UpdateNothingFound();
    }

    private void RunSystemSearch(string query)
    {
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var desktopPaths = _items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Task.Run(() =>
        {
            var apps = SystemSearch.FindApps(query);
            Dispatcher.InvokeAsync(() => { if (!cts.IsCancellationRequested) ShowHits(_apps, apps); });
            var files = SystemSearch.FindFiles(query, 40, cts.Token)
                .Where(f => !desktopPaths.Contains(f.Path)).ToList();
            Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested) return;
                ShowHits(_files, files);
                _searching = false;
                SearchStatus.Text = _apps.Count + _files.Count == 0 ? "" : Loc.T("desktop.search.hint");
                UpdateSearchTitles();
                UpdateNothingFound();
            });
        });
    }

    private void ShowHits(ObservableCollection<TileVm> target, List<SearchHit> hits)
    {
        target.Clear();
        var px = IconPx;
        foreach (var h in hits)
        {
            var vm = new TileVm(h) { Name = h.Name, Tip = h.Path };
            target.Add(vm);
            _loader?.Request(h.Path, px, img => Dispatcher.InvokeAsync(() => vm.Icon = img));
        }
        UpdateSearchTitles();
    }

    private void UpdateSearchTitles()
    {
        AppsTitle.Visibility = _apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilesTitle.Visibility = _files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void LaunchFirst()
    {
        var first = _tiles.FirstOrDefault(t => t.Item is not null) ?? _apps.FirstOrDefault() ?? _files.FirstOrDefault();
        if (first?.Path is { } p) Open(p);
    }

    public void FocusFirst() => TileGrid.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

    // ---------- запуск ----------
    private int _lastClickCount; // сколько раз подряд нажали кнопку мыши на плитке (0 — щёлкнули не мышью, а клавишей)

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (DateTime.Now < _suppressClickUntil) return;
        var clicks = _lastClickCount;
        _lastClickCount = 0;
        if (Cfg.DesktopDoubleClick && clicks == 1) return; // включён двойной щелчок — одинарный ничего не открывает
        if (sender is not FrameworkElement { DataContext: TileVm vm }) return;
        switch (vm.Kind)
        {
            case TileVm.Kinds.Group: OpenGroup(vm.Group!.Id); break;
            case TileVm.Kinds.Trash: OpenTrash(); break;
            default:
                if (vm.Path is { } p) Open(p);
                break;
        }
    }

    /// <summary>
    /// Открыть ярлык, файл или папку. Программа работает с правами администратора; чтобы игры и браузеры
    /// запускались как обычно, просим открыть их Проводник (он работает без этих прав).
    /// </summary>
    private void Open(string path)
    {
        LaunchRequested?.Invoke();
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Error($"launch: {path}", ex); }
    }

    // ---------- группы ----------
    private void OpenGroup(string id)
    {
        PerfLog.Mark("открыть группу");
        _openGroupId = id;
        var g = _store.Groups.FirstOrDefault(x => x.Id == id);
        if (g is null) return;
        GroupName.Text = g.Name;
        RebuildGroupPopup();
        ShowLayer(GroupLayer, GroupPanel);
        // фон за группой размывается вместе с появлением
        var blur = MainArea.Effect as System.Windows.Media.Effects.BlurEffect ?? new System.Windows.Media.Effects.BlurEffect { Radius = 0 };
        MainArea.Effect = blur;
        Anim.To(blur, System.Windows.Media.Effects.BlurEffect.RadiusProperty, 14, 200, Motion.EaseOut());
    }

    /// <summary>Слой поверх рабочего стола (группа, переименование): фон проявляется, окошко «выезжает» с лёгким перелётом.</summary>
    private static void ShowLayer(Border layer, FrameworkElement card)
    {
        layer.IsHitTestVisible = true;
        if (layer.Visibility != Visibility.Visible) layer.Opacity = 0;
        layer.Visibility = Visibility.Visible;
        Anim.To(layer, OpacityProperty, 1, 160, Motion.EaseOut());
        if (!Motion.Enabled) return;
        // окошко на время хода — картинкой: в нём значки с тенью текста, перерисовывать их каждый кадр незачем
        card.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(card).DpiScaleX);
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        var st = card.RenderTransform as ScaleTransform ?? new ScaleTransform();
        card.RenderTransform = st;
        Anim.Run(card, "pop", Motion.UiMs, k => st.ScaleX = st.ScaleY = 0.94 + 0.06 * k, Motion.UiEase(), () => card.CacheMode = null);
    }

    /// <summary>Слой гаснет и убирается; мышь он больше не ловит сразу.</summary>
    private static void HideLayer(Border layer, Action? done = null)
    {
        if (layer.Visibility != Visibility.Visible || !layer.IsHitTestVisible) return;
        layer.IsHitTestVisible = false;
        Anim.To(layer, OpacityProperty, 0, 140, Motion.EaseIn(), () =>
        {
            if (layer.IsHitTestVisible) return; // за это время открыли снова
            layer.Visibility = Visibility.Collapsed;
            done?.Invoke();
        });
    }

    private static bool IsOpen(Border layer) => layer.Visibility == Visibility.Visible && layer.IsHitTestVisible;

    /// <summary>Размытие фона за группой уходит вместе с ней.</summary>
    private void UnblurMain()
    {
        if (MainArea.Effect is not System.Windows.Media.Effects.BlurEffect blur) return;
        Anim.To(blur, System.Windows.Media.Effects.BlurEffect.RadiusProperty, 0, 140, Motion.EaseIn(), () =>
        {
            if (_openGroupId is null && MainArea.Effect == blur) MainArea.Effect = null;
        });
    }

    private void RebuildGroupPopup()
    {
        _groupTiles.Clear();
        if (_openGroupId is null) return;
        var g = _store.Groups.FirstOrDefault(x => x.Id == _openGroupId);
        if (g is null)
        {
            _openGroupId = null;
            HideLayer(GroupLayer);
            UnblurMain();
            return;
        }
        foreach (var p in g.Items)
            if (ItemByPath(p) is { } it) _groupTiles.Add(MakeItemTile(it));
    }

    /// <summary>Закрыть открытую группу (или окно переименования). true — было что закрывать.</summary>
    public bool CloseGroup()
    {
        if (IsOpen(RenameLayer))
        {
            HideLayer(RenameLayer);
            return true;
        }
        if (_openGroupId is null) return false;
        _openGroupId = null; // Esc, крестик, щелчок мимо — просто выйти, новое имя не применяется
        // значки группы убираем, когда окошко уже погасло (иначе оно пустело бы на глазах)
        HideLayer(GroupLayer, () => { if (_openGroupId is null) _groupTiles.Clear(); });
        UnblurMain();
        return true;
    }

    /// <summary>Enter или галочка: применить имя группы и свернуть её.</summary>
    private void ConfirmGroup()
    {
        SaveGroupName();
        CloseGroup();
    }

    private void GroupConfirm_Click(object sender, RoutedEventArgs e) => ConfirmGroup();

    private void SaveGroupName()
    {
        if (_openGroupId is null) return;
        var name = GroupName.Text.Trim();
        var g = _store.Groups.FirstOrDefault(x => x.Id == _openGroupId);
        if (g is null || name.Length == 0 || name == g.Name) return;
        _store.RenameGroup(_openGroupId, name, Loc.T("desktop.undo.rename_group"));
        Rebuild();
    }


    private void GroupName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConfirmGroup();
        }
    }

    private void GroupClose_Click(object sender, RoutedEventArgs e) => CloseGroup();

    private void GroupBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_drag is null) CloseGroup();
    }

    /// <summary>Щелчок внутри панели группы не закрывает её.</summary>
    private void GroupPanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private string DefaultGroupName() => Loc.T("desktop.group.default", _store.Groups.Count + 1);

    // ---------- перетаскивание (своё, без системного drag-and-drop) ----------
    private sealed class DragState
    {
        public required TileVm Vm;
        public required ObservableCollection<TileVm> Source;
        public required Point Start;
        public bool Started;
        public TileVm? Target;          // на середину какой плитки бросают
        public bool OverDropBar;        // найденное → на рабочий стол
        public bool OutOfGroup;         // из открытой группы → наружу
        public bool Reordered;
        public bool LandedTile;         // садится сама плитка (призрак уже убран)
        public bool Detached;           // над корзиной: плитка убрана из сетки, остальные сомкнулись
        public int DetachedIndex;
        public TileVm? Hover;           // на середине какой плитки держат значок
        public bool Ripe;               // продержали достаточно долго — бросить значит создать папку
        public TileVm? EdgeVm;          // над какой плиткой (не по центру) держат — переставляем, когда задержались
        public bool EdgeReady;
    }

    private DragState? _drag;
    private DateTime _suppressClickUntil;
    private Point _lastDragPos;

    /// <summary>Как на iPhone: значок, который подержали над серединой другого, превращается в папку; пока не «созрело» — соседи просто расступаются.</summary>
    private readonly DispatcherTimer _dwell = new() { Interval = TimeSpan.FromMilliseconds(150) };

    /// <summary>Перестановка — только если задержались над плиткой: пока проводят мимо к середине другой, ничего не едет.</summary>
    private readonly DispatcherTimer _edge = new() { Interval = TimeSpan.FromMilliseconds(60) };

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TileVm vm || vm.Kind == TileVm.Kinds.Trash) return;
        var source = _groupTiles.Contains(vm) ? _groupTiles : _apps.Contains(vm) ? _apps : _files.Contains(vm) ? _files : _tiles;
        _lastClickCount = e.ClickCount;
        _drag = new DragState { Vm = vm, Source = source, Start = e.GetPosition(this) };
    }

    /// <summary>Картинка значка плитки группы (рамка с мини-значками) такого же размера, как у значка в руке. null — не вышло.</summary>
    private ImageSource? GroupIconSnapshot(TileVm vm)
    {
        try
        {
            if (TileGrid.ItemContainerGenerator.ContainerFromItem(vm) is not DependencyObject c) return null;
            var box = FindFirst<Viewbox>(c);
            if (box is null) return null;
            var sz = (double)Cfg.DesktopIconSize;
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(sz * dpi.DpiScaleX), (int)Math.Ceiling(sz * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(box) { Stretch = Stretch.Fill }, null, new Rect(0, 0, sz, sz));
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        catch { return null; }
    }

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is T hit) return hit;
            if (FindFirst<T>(ch) is { } deep) return deep;
        }
        return null;
    }

    private void Root_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(apply: false);
            return;
        }
        var pos = e.GetPosition(this);
        if (!_drag.Started)
        {
            var d = pos - _drag.Start;
            if (Math.Abs(d.X) < 8 && Math.Abs(d.Y) < 8) return;
            if (_drag.Vm.Kind == TileVm.Kinds.Group && _openGroupId is not null) return;
            if (_drag.Source == _tiles && _filter.Length > 0 && _drag.Vm.Kind != TileVm.Kinds.Hit) return; // при поиске порядок не меняем
            CaptureMouse();
            if (_drag is null) return;
            _drag.Started = true;
            _holdCache = true;
            CancelFlight(); // прошлый значок мог ещё лететь на место — обрываем, иначе он гасил бы и двигал новый призрак
            _ungroupStop?.Invoke(); // расформирование ещё шло — плитки сразу на свои места, иначе сетка перетаскивания считалась бы по сдвинутым
            // у группы в руке — её значок (сетка мини-значков), а не значок первого участника; снимаем до того, как плитка опустеет
            GhostImage.Source = _drag.Vm.Kind == TileVm.Kinds.Group ? GroupIconSnapshot(_drag.Vm) : null;
            _drag.Vm.IsDragged = true;
            GhostImage.Source ??= _drag.Vm.Icon ?? _drag.Vm.Previews.FirstOrDefault();
            GhostText.Text = _drag.Vm.Name;
            var sz = (double)Cfg.DesktopIconSize;
            Ghost.Width = sz + 44;
            GhostImage.Width = GhostImage.Height = sz;
            GhostImage.Clip = new RectangleGeometry(new Rect(0, 0, sz, sz), sz * 12 / 64, sz * 12 / 64); // скругление пропорционально, как у плиток
            Ghost.Visibility = Visibility.Visible;
            Ghost.RenderTransformOrigin = new Point(0.5, 0.5);
            var pop = new ScaleTransform(0.9, 0.9);
            Ghost.RenderTransform = pop;
            Anim.Run(pop, "pop", 160, k => pop.ScaleX = pop.ScaleY = 0.9 + 0.2 * k, PopEase); // значок «вынимается» из сетки
            DropBar.Visibility = _drag.Vm.Kind == TileVm.Kinds.Hit ? Visibility.Visible : Visibility.Collapsed;
            Anim.Stop(Ghost, OpacityProperty);
            Ghost.Opacity = 0.95;
            _landKey = null;
            ReleaseHold();
            GhostText.Opacity = 1;
            foreach (var tv in _tiles.Concat(_groupTiles)) if (tv != _drag.Vm) tv.IsDragged = false;
            Anim.Stop(Ghost, "land"); // вдруг ещё летит с прошлого раза
        }
        if (_drag is null) return;
        // Мышь шлёт до ~1000 движений в секунду, а на экране от них виден только последний за кадр: здесь лишь запоминаем
        // позицию, а призрак и поиск места под курсором (перебор всех плиток) — один раз за кадр (DragFrame).
        // Раньше всё это делалось на каждое движение — перетаскивание грузило ядро процессора на 90–130 %.
        // ⚠ здесь только запомнить позицию: поиск места (перебор плиток) на каждое движение мыши грузил ядро на 90–130 %
        _lastDragPos = pos;
        _dragDirty = true;
        if (!_dragFrames)
        {
            _dragFrames = true;
            FrameClock.Rendering += DragFrame;
            StartGhost(pos);
            ApplyDragPos(); // первое движение — сразу, без ожидания кадра
        }
    }

    private bool _dragDirty, _dragFrames;

    // пока тащат значок, сдвинутые плитки остаются картинками (см. TapeAttach); после броска — снова векторные
    private bool _holdCache;
    private readonly HashSet<FrameworkElement> _heldCache = new();

    private void ReleaseHeldCache()
    {
        _holdCache = false;
        foreach (var c in _heldCache)
            if (!_tape.ContainsKey(c)) c.CacheMode = null; // ещё едущие снимут сами, когда приедут
        _heldCache.Clear();
    }

    private void DragFrame(object? sender, EventArgs e)
    {
        if (_drag is not { Started: true })
        {
            StopDragFrames();
            return;
        }
        if (_dragDirty) ApplyDragPos();
        MoveGhost(); // призрак догоняет курсор и тогда, когда мышь стоит
    }

    private void StopDragFrames()
    {
        if (!_dragFrames) return;
        _dragFrames = false;
        FrameClock.Rendering -= DragFrame;
    }

    /// <summary>Призрак — под курсор, место под ним — по последней позиции мыши.</summary>
    private void ApplyDragPos()
    {
        _dragDirty = false;
        var pos = _lastDragPos;
        MoveGhost();
        PerfLog.AnimTick("перетаскивание");
        UpdateDragTarget(pos);
    }

    // ---------- призрак чуть отстаёт от курсора ----------
    // Значок в руке тянется за курсором с небольшой инерцией: он догоняет курсор по экспоненте (GhostLagMs), а отставание
    // «мягко ограничено» кругом диаметром в 1/3 значка — R·tanh(отставание/R): чем быстрее движение, тем ближе к краю круга,
    // но край достигается только при бесконечной скорости. Место под значком (куда он встанет) — по самому курсору.
    private const double GhostLagMs = 28;
    private Point _ghostRaw;
    private double _ghostT;

    private void StartGhost(Point pos)
    {
        _ghostRaw = pos;
        _ghostT = FrameClock.NowMs;
        PlaceGhost(pos);
    }

    private void MoveGhost()
    {
        if (_drag is not { Started: true }) return;
        var cur = _lastDragPos;
        var now = FrameClock.NowMs;
        var dt = Math.Clamp(now - _ghostT, 0, 100);
        _ghostT = now;
        if (!Motion.Enabled)
        {
            _ghostRaw = cur;
            PlaceGhost(cur);
            return;
        }
        _ghostRaw += (cur - _ghostRaw) * (1 - Math.Exp(-dt / GhostLagMs));
        var lag = _ghostRaw - cur;
        var len = lag.Length;
        var r = Cfg.DesktopIconSize / 6.0; // радиус круга: диаметр — треть значка
        if (len > 0.01) lag *= r * Math.Tanh(len / r) / len;
        PlaceGhost(cur + lag);
    }

    /// <summary>Центр значка призрака — в точке p.</summary>
    private void PlaceGhost(Point p)
    {
        Canvas.SetLeft(Ghost, p.X - Ghost.Width / 2);
        Canvas.SetTop(Ghost, p.Y - (8 + Cfg.DesktopIconSize / 2.0));
    }

    private void UpdateDragTarget(Point pos)
    {
        var drag = _drag!;
        var prevHover = drag.Hover;
        var wasRipe = drag.Ripe;
        drag.Hover = null;
        drag.Ripe = false;
        drag.EdgeVm = null;
        drag.EdgeReady = false;
        // над корзиной флаг не сбрасываем на каждое движение мыши: иначе плашка то сжималась, то раздувалась и анимация ломалась
        var overTrash = TrashTarget is { IsVisible: true } tz && drag.Vm.Kind is TileVm.Kinds.Item or TileVm.Kinds.Group && IsInside(tz, pos);
        if (drag.Target is not null && !(overTrash && drag.Target == _trash)) drag.Target.IsDropTarget = false;
        if (!overTrash && _trash.IsDropTarget) _trash.IsDropTarget = false;
        drag.Target = null;
        drag.OverDropBar = false;
        drag.OutOfGroup = false;

        if (drag.Vm.Kind == TileVm.Kinds.Hit)
        {
            drag.OverDropBar = IsInside(DropBar, pos) || IsInside(TileGrid, pos);
            DropBar.BorderThickness = new Thickness(drag.OverDropBar ? 3 : 2);
            return;
        }

        if (_openGroupId is not null && drag.Source == _groupTiles && !IsInside(GroupPanel, pos))
        {
            drag.OutOfGroup = true; // наружу — достать из группы
            GroupHint.Text = Loc.T("desktop.group.drop_out");
            GroupHint.Visibility = Visibility.Visible;
            return;
        }
        GroupHint.Visibility = Visibility.Collapsed;

        // корзина в шапке панели
        if (overTrash)
        {
            drag.Target = _trash;
            _trash.IsDropTarget = true;
            DetachFromGrid(drag); // для раскладки значок над корзиной уже «исчез»: остальные смыкаются на его месте
            return;
        }
        AttachToGrid(drag);

        var grid = drag.Source == _groupTiles ? GroupGrid : TileGrid;
        var (vm, rect) = TileAtLogical(pos, drag.Source, grid);
        if (vm is null) return;

        // Сетка статична. «Рёбра» — узкие полосы по краям плиток, между соседними значками: попали в ребро — вклиниваем
        // значок между ними (соседи расступаются). Попали в саму плитку (место, где стоит или стояла другая) — папка.
        // ширина ребра — пятая часть плитки (14 px у самых мелких 72, 30 px у самых крупных 200): по краям легче попасть,
        // а на «папку» в середине остаётся не меньше 60 % плитки. Расстояния между значками от этого не меняются
        var band = Math.Clamp(rect.Width * 0.2, 12, 30);
        var onLeftEdge = pos.X - rect.X < band;
        var onRightEdge = rect.Right - pos.X < band;
        var canMerge = vm.Kind == TileVm.Kinds.Trash
                       || (drag.Vm.Kind == TileVm.Kinds.Item && drag.Source == _tiles && vm.Kind is TileVm.Kinds.Item or TileVm.Kinds.Group);
        if (vm.Kind == TileVm.Kinds.Trash)
        {
            drag.Target = vm;
            vm.IsDropTarget = true;
            return;
        }
        if (!onLeftEdge && !onRightEdge)
        {
            if (vm == drag.Vm || !canMerge) return;
            drag.Hover = vm;
            if (prevHover == vm) drag.Ripe = wasRipe;
            else
            {
                _dwell.Stop();
                _dwell.Start(); // держат над плиткой — через 0,35 с получится папка
            }
            if (drag.Ripe)
            {
                drag.Target = vm;
                vm.IsDropTarget = true;
            }
            return;
        }

        // ребро: куда вклинить (слот до или после этой плитки) — сразу, без задержки
        var list = drag.Source;
        var idx = list.IndexOf(vm);
        var from = list.IndexOf(drag.Vm);
        if (idx < 0 || from < 0) return;
        var slot = onLeftEdge ? idx : idx + 1;
        var to = slot > from ? slot - 1 : slot;
        if (to == from) return;
        MoveAnimated(list, grid, from, to);
        drag.Reordered = true;
    }

    /// <summary>Значок ушёл над корзину: убираем плитку из сетки (остальные плавно смыкаются), чтобы не было дыры и скачков.</summary>
    private void DetachFromGrid(DragState drag)
    {
        if (drag.Detached || drag.Source != _tiles) return;
        var idx = _tiles.IndexOf(drag.Vm);
        if (idx < 0) return;
        drag.DetachedIndex = idx;
        drag.Detached = true;
        drag.Reordered = true; // отмена или возврат — Rebuild вернёт всё как было
        ReflowAnimated(_tiles, TileGrid, () => _tiles.RemoveAt(idx));
    }

    /// <summary>Значок вернулся с корзины в сетку: плитка встаёт на прежнее место, остальные расступаются.</summary>
    private void AttachToGrid(DragState drag)
    {
        if (!drag.Detached) return;
        drag.Detached = false;
        var idx = Math.Min(drag.DetachedIndex, _tiles.Count);
        ReflowAnimated(_tiles, TileGrid, () => _tiles.Insert(idx, drag.Vm));
    }

    // ---------- возврат значка на место и отладочная сетка ----------
    private string? _landKey;

    /// <summary>
    /// Пересобрать плитки так, чтобы оставшиеся значки не телепортировались на освободившиеся места, а ехали туда
    /// с пружинкой «паровозиком»: первый тронулся, следующий чуть позже и так далее.
    /// </summary>
    private bool _skipTrashUpdate;

    /// <summary>Значки, которые должны появиться с анимацией: плитка создаётся уже невидимой и показывается в свой кадр.</summary>
    private HashSet<string>? _unborn;
    private bool _unbornAll; // при пересборке все плитки создаются невидимыми и показываются в первом кадре, когда стоят на стартовых местах

    /// <summary>
    /// Плитки всегда рисуются готовой картинкой (BitmapCache с разрешением экрана, ресурс TileCache): так значок и подпись
    /// сглажены и мягкие — как раньше было только на ходу (в покое картинку снимали, и плитка рисовалась «зернисто»:
    /// подпись с цветной подгонкой к пикселям, край значка жёстче). Нагрузки меньше, а не больше: картинка перерисовывается,
    /// только когда плитка меняется (наведение, новый значок), а не в каждом кадре вокруг неё.
    /// </summary>
    private static bool TileCacheOn => Application.Current.Resources["TileCache"] is BitmapCache;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateTileCache(); // картинки плиток — в разрешении нового экрана
    }

    private void UpdateTileCache()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (Application.Current.Resources["TileCache"] is BitmapCache { RenderAtScale: var cur } && Math.Abs(cur - dpi) < 0.001) return;
        var cache = new BitmapCache(dpi) { SnapsToDevicePixels = false };
        cache.Freeze(); // одна картинка-настройка на все плитки (у каждой плитки своя картинка)
        Application.Current.Resources["TileCache"] = cache;
        Ghost.CacheMode = cache;
    }

    /// <summary>Снимок сетки значков поверх неё (null — не получилось или сетка прокручена).</summary>
    /// <remarks>
    /// Снимок рисуется без видеокарты (тени подписей — самое дорогое), это и был подфриз 20–40 мс в момент броска значка.
    /// Если из снимка ничего не вырезается (копия папки, влетающие значки — full), он лежит не дольше кадра — хватает
    /// половинного разрешения: вчетверо меньше точек.
    /// </remarks>
    private Image? MakeCover(bool full = true)
    {
        // ⚠ у TileGrid не должно быть своего Margin: RenderTargetBitmap рисует элемент вместе с его полем — снимок съезжает
        try
        {
            if (Scroll.VerticalOffset > 0.5 || TileGrid.ActualWidth < 1 || TileGrid.ActualHeight < 1) return null;
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX * (full ? 1 : 0.5), sy = dpi.DpiScaleY * (full ? 1 : 0.5);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(TileGrid.ActualWidth * sx), (int)Math.Ceiling(TileGrid.ActualHeight * sy),
                96 * sx, 96 * sy, PixelFormats.Pbgra32);
            rtb.Render(TileGrid);
            var tl = TileGrid.TranslatePoint(new Point(), this);
            var img = new Image
            {
                Source = rtb, Width = TileGrid.ActualWidth, Height = TileGrid.ActualHeight, IsHitTestVisible = false,
                Stretch = Stretch.Fill,
            };
            Canvas.SetLeft(img, tl.X);
            Canvas.SetTop(img, tl.Y);
            DragLayer.Children.Insert(0, img);
            return img;
        }
        catch { return null; }
    }

    private void RemoveCover(Image? cover)
    {
        if (cover is not null) DragLayer.Children.Remove(cover);
    }


    private const double FlyMs = 456;

    // ================= лента =================
    // Весь рабочий стол — одна сплошная линия: строки просто склеены друг за другом (разрезаны на столбцы только для показа).
    // Каждая плитка стоит в точке s этой линии; при перестановке она едет по линии из s_было в s_стало. У границы строки линия
    // «переламывается»: плитка тает до нуля, оказывается с другого края следующей строки и растёт из нуля. Диагоналей нет,
    // а положение плитки в любой момент — одно число s (оно берётся из того, где плитка сейчас на экране), поэтому
    // перестановка посреди чужого движения продолжает его плавно, без пролётов через сетку.

    /// <summary>
    /// Геометрия ленты: левый край и верх сетки, размер плитки, длина строки (столбцов × ширина плитки) и НАСТОЯЩИЕ места строк и столбцов.
    /// Плитки стоят не ровно через высоту (ширину) плитки: раскладка округляет их до пикселей экрана (при высоте 129,6 строки шли
    /// 0 → 129,6 → 260,0). Если считать «верх + номер × высота», на ходу плитка стояла на пиксель-два выше, чем в покое,
    /// и при остановке опускалась. Поэтому строки и столбцы берём из раскладки, а между столбцами — по прямой.
    /// </summary>
    private readonly record struct TapeGeo(double L0, double T0, double Pw, double Ph, double RowLen, double[] RowYs, double[] ColXs, double W)
    {
        // Виток ленты — вся ширина панели (W): чёткой границы нет. Плитка, подъезжая к краю панели, упирается в него и сжимается
        // (её край не выходит за край панели), на самом краю — ноль; на следующей строке так же вырастает от противоположного края.
        // Сетка подогнана к ширине панели без полей (FitColumns), поэтому у крайних плиток в покое размер ровно 1.
        private double Zone => Pw / 2;
        private double Period => W;

        /// <summary>Верх строки номер r (за пределами известных строк — продолжаем шагом последних).</summary>
        private double RowY(double r)
        {
            var n = RowYs.Length;
            if (n == 0) return T0 + r * Ph;
            if (r < 0) return RowYs[0] + r * Ph;
            if (r <= n - 1) return RowYs[(int)r];
            return RowYs[n - 1] + (r - (n - 1)) * Ph;
        }

        /// <summary>Номер ближайшей строки к верху плитки y.</summary>
        private double RowOf(double y)
        {
            var n = RowYs.Length;
            if (n == 0) return Math.Round((y - T0) / Ph);
            if (y <= RowYs[0]) return Math.Round((y - RowYs[0]) / Ph);
            if (y >= RowYs[n - 1]) return n - 1 + Math.Round((y - RowYs[n - 1]) / Ph);
            var best = 0;
            for (var i = 1; i < n; i++) if (Math.Abs(RowYs[i] - y) < Math.Abs(RowYs[best] - y)) best = i;
            return best;
        }

        /// <summary>Расчётный левый край (L0 + столбец × ширина) → настоящий, по прямой между соседними столбцами.</summary>
        private double ActualX(double nx)
        {
            var n = ColXs.Length;
            if (n < 2) return nx + (n == 1 ? ColXs[0] - L0 : 0);
            var f = (nx - L0) / Pw;
            var c = (int)Math.Clamp(Math.Floor(f), 0, n - 2);
            return ColXs[c] + (ColXs[c + 1] - ColXs[c]) * (f - c);
        }

        /// <summary>Обратно: настоящий левый край → расчётный.</summary>
        private double NominalX(double x)
        {
            var n = ColXs.Length;
            if (n < 2) return x - (n == 1 ? ColXs[0] - L0 : 0);
            var c = 0;
            while (c < n - 2 && x >= ColXs[c + 1]) c++;
            var span = ColXs[c + 1] - ColXs[c];
            return L0 + (c + (span > 1e-6 ? (x - ColXs[c]) / span : 0)) * Pw;
        }

        /// <summary>Точка ленты по положению плитки на экране (левый верхний угол, в координатах сетки).</summary>
        public double SOf(double x, double y) => RowOf(y) * Period + NominalX(x) + Pw / 2;

        /// <summary>
        /// Сколько можно проехать от точки s в сторону dir, не заходя в зону уменьшения у края строки (там плитка тает
        /// и переходит на соседнюю строку). Перелёт хода в конце не должен туда заходить — иначе плитка «пульсировала».
        /// </summary>
        public double Slack(double s, int dir)
        {
            var inRow = s - Math.Floor(s / Period) * Period;
            return Math.Max(0, dir > 0 ? Period - Zone - inRow : inRow - Zone);
        }

        /// <summary>Положение плитки и её размер в точке s (по центру плитки).</summary>
        public void Pose(double s, out double x, out double y, out double scale)
        {
            var row = Math.Floor(s / Period);
            var inRow = s - row * Period; // центр плитки от левого края панели
            x = ActualX(inRow - Pw / 2);
            y = RowY(row);
            scale = Math.Clamp(Math.Min(inRow, Period - inRow) / Zone, 0, 1);
        }
    }
    private sealed class TapeTile
    {
        public FrameworkElement C = null!;
        public readonly TranslateTransform Tt = new();
        public readonly ScaleTransform St = new(1, 1);
        public TapeGeo Geo;
        public Point Layout;
        public double From, To, Begin, Dur;
        public double M0;      // инерция: ход сменили на полпути — начальная скорость прошлого хода (см. TapeS)
        public bool Inertia;
        public double Off;     // толчок соседа: смещение поверх своего хода, затухает (см. Repel)
        public double LastBase; // точка своего хода в прошлом кадре (без толчка): толкает только тот, кто едет сам
        public double Body;    // половина ширины тела (значок или подпись — что шире)
        public double LastS, LastX, LastY, LastScale; // для отладочного лога
    }

    /// <summary>ОТЛАДКА: писать в logs\tape.log старт каждой плитки и её положение (s, x, y, размер) в каждом кадре.</summary>
    private const bool TapeTrace = false; // покадровый tape.log сам тормозил; для замеров теперь PerfLog
    private System.Text.StringBuilder? _tapeLog;

    private readonly Dictionary<FrameworkElement, TapeTile> _tape = new();
    private readonly List<TapeTile> _tapeList = new();
    private Stopwatch? _tapeClock;
    private double _tapeNow, _tapeLast;
    private int _tapeFrames;
    private static readonly System.Windows.Media.Animation.BackEase TapeEase =
        Frozen(new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.45 });
    // призрак «вынимается» (с перелётом), летит в папку/корзину (плавно разгоняясь и тормозя), садится на место (с пружинкой)
    private static readonly System.Windows.Media.Animation.BackEase PopEase =
        Frozen(new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.6 });
    private static readonly System.Windows.Media.Animation.CubicEase FlyEase =
        Frozen(new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut });
    private static readonly System.Windows.Media.Animation.BackEase LandEase =
        Frozen(new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.5 });

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    /// <summary>Смещение плитки от её места в сетке (идущая анимация), для любого вида RenderTransform.</summary>
    private static Vector OffsetOf(FrameworkElement c) => c.RenderTransform switch
    {
        TranslateTransform t => new Vector(t.X, t.Y),
        TransformGroup g when g.Children.OfType<TranslateTransform>().FirstOrDefault() is { } tt => new Vector(tt.X, tt.Y),
        _ => new Vector(),
    };

    /// <summary>
    /// Место плитки по раскладке (левый верхний угол в координатах grid) — без её RenderTransform. Не «экран минус смещение»:
    /// у плитки на краю строки есть ещё и уменьшение, и вычитание одного смещения давало ошибку.
    /// </summary>
    private static Point LayoutOf(FrameworkElement c, Visual grid)
    {
        var off = VisualTreeHelper.GetOffset(c);
        return VisualTreeHelper.GetParent(c) is UIElement panel
            ? panel.TranslatePoint(new Point(off.X, off.Y), (UIElement)grid)
            : c.TranslatePoint(new Point(), (UIElement)grid) - OffsetOf(c);
    }

    private static TapeGeo? MakeGeo(ItemsControl grid, IEnumerable<TileVm> list)
    {
        var cs = list.Select(v => grid.ItemContainerGenerator.ContainerFromItem(v) as FrameworkElement)
            .Where(c => c is { ActualWidth: > 1 }).Select(c => c!).ToList();
        if (cs.Count == 0) return null;
        double pw = cs[0].ActualWidth, ph = cs[0].ActualHeight;
        var cols = Math.Max(1, (int)Math.Floor((grid.ActualWidth + 1) / pw));
        var layouts = cs.Select(c => LayoutOf(c, grid)).ToList();
        var rowLen = cols * pw;
        var l0 = cs.Count >= cols ? layouts.Min(p => p.X) : (grid.ActualWidth - rowLen) / 2;
        var t0 = layouts.Min(p => p.Y);
        // настоящие места строк и столбцов (раскладка округляет их до пикселей); пропуски — расчётом
        var rows = (int)Math.Round((layouts.Max(p => p.Y) - t0) / ph) + 1;
        var rowYs = new double[rows];
        var colXs = new double[cols];
        for (var r = 0; r < rows; r++) rowYs[r] = double.NaN;
        for (var c = 0; c < cols; c++) colXs[c] = double.NaN;
        foreach (var p in layouts)
        {
            var r = (int)Math.Round((p.Y - t0) / ph);
            var c = (int)Math.Round((p.X - l0) / pw);
            if (r >= 0 && r < rows && double.IsNaN(rowYs[r])) rowYs[r] = p.Y;
            if (c >= 0 && c < cols && double.IsNaN(colXs[c])) colXs[c] = p.X;
        }
        for (var r = 0; r < rows; r++) if (double.IsNaN(rowYs[r])) rowYs[r] = r == 0 ? t0 : rowYs[r - 1] + ph;
        for (var c = 0; c < cols; c++) if (double.IsNaN(colXs[c])) colXs[c] = c == 0 ? l0 : colXs[c - 1] + pw;
        return new TapeGeo(l0, t0, pw, ph, rowLen, rowYs, colXs, Math.Max(grid.ActualWidth, rowLen));
    }

    /// <summary>
    /// Запустить плитку по ленте: from — где она сейчас на экране (с учётом идущей анимации), layout — где ей быть по сетке.
    /// delayMs — когда стартовать (очередь).
    /// </summary>
    private void TapeStart(FrameworkElement c, TapeGeo g, Point from, Point layout, double delayMs, double durMs)
    {
        double sFrom = g.SOf(from.X, from.Y), sTo = g.SOf(layout.X, layout.Y);
        _tape.TryGetValue(c, out var t);
        // плитка уже на ленте той же раскладки — продолжаем ровно с её точки: пересчёт «экран → лента» у края панели
        // (плитка там сжата) давал скачок на долю клетки, и при частой смене цели плитка подрагивала
        if (t is not null && SameGeo(t.Geo, g)) sFrom = t.LastS;
        if (Math.Abs(sTo - sFrom) < 0.5)
        {
            if (t is not null) TapeEnd(t); // уже на месте
            return;
        }
        if (t is null)
        {
            t = TapeAttach(c, g, layout);
            _tape[c] = t;
            _tapeList.Add(t);
        }
        // Плитка уже едет, а цель сменилась (значок водят туда-сюда): новый ход начинается с её текущей скоростью,
        // а не с нуля и заново с перелётом. Иначе у края строки плитка дёргалась вперёд-назад через зону уменьшения — «дребезжала».
        var v0 = 0.0;
        var moving = t.Dur > 0 && _tapeNow >= t.Begin && _tapeNow - t.Begin < t.Dur;
        if (moving) v0 = TapeVelocity(t, (_tapeNow - t.Begin) / t.Dur);
        t.Geo = g;
        t.Layout = layout;
        t.From = sFrom;
        t.To = sTo;
        t.Begin = _tapeNow + (moving ? 0 : delayMs);
        t.Dur = durMs;
        t.Inertia = moving;
        t.Off = 0; // толчок уже в точке старта (LastS) — дальше свой ход с нуля
        t.LastBase = sFrom;
        // начальная скорость нового хода = скорость прошлого: s'(0) = 3·(To−From) + M0 (в долях хода), см. TapeS
        t.M0 = moving ? 0.5 * v0 * durMs - 3 * (sTo - sFrom) : 0; // половина прежней скорости — «немного инерции»
        TapePose(t, sFrom); // сразу на старом месте — ни кадра в конечной точке
        if (TapeTrace)
        {
            _tapeLog ??= new System.Text.StringBuilder();
            g.Pose(sTo, out var chkX, out var chkY, out _); // лента в конечной точке должна совпасть с местом по раскладке
            _tapeLog.AppendLine($"  проверка места: ошибка ({chkX - layout.X:0.00}; {chkY - layout.Y:0.00})");
            _tapeLog.AppendLine($"СТАРТ {(c.DataContext as TileVm)?.Name,-28} s {sFrom,8:0.0} → {sTo,8:0.0}  экран ({from.X:0.0}; {from.Y:0.0}) → ({layout.X:0.0}; {layout.Y:0.0})  задержка {delayMs:0} мс, {durMs:0} мс  " +
                                $"[лента: L0 {g.L0:0.0}, T0 {g.T0:0.0}, плитка {g.Pw:0.0}×{g.Ph:0.0}, строка {g.RowLen:0.0}]");
        }
        if (_tapeClock is null)
        {
            _tapeClock = Stopwatch.StartNew();
            _tapeLast = 0;
            FrameClock.Rendering += TapeStep;
        }
        _tapeFrames = 0; // новая пачка: тяжёлые первые кадры не идут в анимацию
    }

    private static bool SameGeo(TapeGeo a, TapeGeo b) =>
        Math.Abs(a.W - b.W) < 0.5 && Math.Abs(a.Pw - b.Pw) < 0.01 && Math.Abs(a.Ph - b.Ph) < 0.01
        && Math.Abs(a.L0 - b.L0) < 0.5 && Math.Abs(a.T0 - b.T0) < 0.5;

    /// <summary>Подготовить плитку к движению: свои смещение и размер, кэш картинкой на время движения.</summary>
    private TapeTile TapeAttach(FrameworkElement c, TapeGeo g, Point layout)
    {
        var t = new TapeTile { C = c, Geo = g, Layout = layout, Body = BodyHalf(c) };
        var group = new TransformGroup();
        group.Children.Add(t.St);
        group.Children.Add(t.Tt);
        c.RenderTransformOrigin = new Point(0.5, 0.4);
        c.RenderTransform = group;
        // плитка с тенью текста и обрезкой на каждом кадре перерисовывалась (12–20 мс вместо 5): на время движения кэшируем картинкой.
        // Уже есть картинка — оставляем: новая означает заново отрисовать плитку, а при перетаскивании перестановки идут одна за другой
        // и каждая заново рисовала все сдвинутые плитки — отсюда мелкие подфризы
        // сама плитка (кнопка) и так всегда картинкой (TileCache) — вторая картинка поверх неё не нужна (и мылила бы на ходу)
        if (!TileCacheOn && c.CacheMode is not BitmapCache) c.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (_holdCache) _heldCache.Add(c);
        return t;
    }

    // ---------- расформирование группы: папка — первый вагон «паровозика», значки остаются под ней ----------
    // Плитки после группы едут общим «паровозиком» (TapeTrain) — так же, как смыкание после папки, только вправо. Папка (копия её
    // плитки) занимает в этой очереди первое место: стартует сразу, тем же ходом (TapeEase, та же длительность), от своего места до
    // места последнего значка, и первая плитка хвоста трогается на шаг очереди позже. Значки разъезжаются из-под неё «гармошкой»
    // (растягиваются вслед за папкой и растут из нуля); последние полклетки папка тает, открывая последний значок.
    private Action? _ungroupStop;

    private void StartUngroup(TapeGeo g, Point groupOld, Size groupSize, List<(FrameworkElement C, TileVm Vm, Point Layout)> members,
        List<(FrameworkElement C, Point Old, Point Layout)> tail, Image? cover)
    {
        _ungroupStop?.Invoke(); // прошлое расформирование ещё шло — довести до конца сразу
        var sG = g.SOf(groupOld.X, groupOld.Y);
        var sK = members.Select(m => g.SOf(m.Layout.X, m.Layout.Y)).ToArray();
        var sEnd = Math.Max(sG, sK[^1]);
        var cells = Math.Max(1, members.Count - 1); // на столько клеток сдвигается хвост и едет папка
        var growEase = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.5 };

        // папка: копия её плитки из снимка (без снимка значки всё равно рождаются по ходу невидимой папки)
        Image? head = null;
        TranslateTransform? headT = null;
        ScaleTransform? headSc = null;
        if (cover?.Source is System.Windows.Media.Imaging.BitmapSource bmp && cover.Width > 1)
        {
            try
            {
                var sc = bmp.PixelWidth / cover.Width;
                var rect = new Int32Rect((int)(groupOld.X * sc), (int)(groupOld.Y * sc), (int)(groupSize.Width * sc), (int)(groupSize.Height * sc));
                if (rect.X >= 0 && rect.Y >= 0 && rect.X + rect.Width <= bmp.PixelWidth && rect.Y + rect.Height <= bmp.PixelHeight)
                {
                    headT = new TranslateTransform();
                    headSc = new ScaleTransform(1, 1);
                    var tg = new TransformGroup();
                    tg.Children.Add(headSc);
                    tg.Children.Add(headT);
                    head = new Image
                    {
                        Source = new System.Windows.Media.Imaging.CroppedBitmap(bmp, rect), Width = groupSize.Width, Height = groupSize.Height,
                        IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.4), RenderTransform = tg,
                    };
                    var tl = TileGrid.TranslatePoint(new Point(), this);
                    Canvas.SetLeft(head, tl.X + groupOld.X);
                    Canvas.SetTop(head, tl.Y + groupOld.Y);
                    DragLayer.Children.Add(head);
                }
            }
            catch (Exception ex) { Log.Warn($"desktop: копия папки не получилась: {ex.Message}"); }
        }

        // значки группы: плитки уже на своих местах, но невидимы (IsDragged) и нулевого размера — до своего рождения
        var memberTiles = members.Select(m => TapeAttach(m.C, g, m.Layout)).ToList();
        for (var i = 1; i < memberTiles.Count; i++) TapePose(memberTiles[i], sG); // все вылезут из-под папки
        foreach (var mt in memberTiles.Skip(1)) mt.St.ScaleX = mt.St.ScaleY = 0;
        var sMem = new double[members.Count];

        // хвост — «паровозиком», папка — первое место очереди
        var dur = (TapeTrain(g, tail, lead: 1, cellsHint: cells) ?? TrainTiming(1, cells)).Dur;
        if (TapeTrace)
        {
            _tapeLog ??= new System.Text.StringBuilder();
            _tapeLog.AppendLine($"РАСФОРМИРОВАНИЕ: значков {members.Count}, хвост {tail.Count}, папка s {sG:0.0} → {sEnd:0.0} за {dur:0} мс");
        }

        var clock = Stopwatch.StartNew();
        double now = 0, last = 0; // время анимации, мс — как в TapeStep, чтобы папка шла в ногу с хвостом
        var frames = 0;
        var opacity = 1.0;
        var done = false;
        void Finish()
        {
            if (done) return;
            done = true;
            FrameClock.Rendering -= Tick;
            if (_ungroupStop == Finish) _ungroupStop = null;
            foreach (var t in memberTiles)
            {
                t.Tt.X = t.Tt.Y = 0;
                t.St.ScaleX = t.St.ScaleY = 1;
                t.C.CacheMode = null; // вырос — снова рисуем вектором, текст чёткий
            }
            foreach (var m in members) m.Vm.IsDragged = false;
            if (head is not null) DragLayer.Children.Remove(head);
        }
        void Tick(object? sender, EventArgs e)
        {
            PerfLog.AnimTick("расформирование");
            var real = clock.Elapsed.TotalMilliseconds;
            var dt = real - last;
            last = real;
            // тяжёлые первые кадры после пересборки — как в TapeStep; анимации выключены — сразу в конец
            now += ++frames <= 12 ? Math.Min(dt, Motion.FirstFrameCapMs) : dt;
            if (!Motion.Enabled) now = double.MaxValue / 4;
            var p = Math.Clamp(now / dur, 0, 1);
            var head0 = sG + (sEnd - sG) * TapeEase.Ease(p);
            // «гармошка»: значки не стоят, а разъезжаются из-под папки — каждый проходит свою долю её пути тем же ходом
            // (последний едет прямо под папкой, поэтому перед хвостом дыры нет) и растёт по мере того, как отходит от соседа
            var e1 = TapeEase.Ease(p);
            for (var i = 0; i < members.Count; i++)
            {
                if (frames == 1) members[i].Vm.IsDragged = false; // первый кадр: все на старте (под папкой, нулевого размера)
                if (i == 0) continue; // первый значок встаёт на место папки
                sMem[i] = sG + (sK[i] - sG) * e1;
                var t = memberTiles[i];
                TapePose(t, sMem[i]);
                var gap = Math.Clamp((sMem[i] - (i == 1 ? sG : sMem[i - 1])) / g.Pw, 0, 1);
                t.St.ScaleX = t.St.ScaleY = t.LastScale * Math.Max(0, growEase.Ease(gap));
            }
            if (head is not null && headT is not null && headSc is not null)
            {
                g.Pose(head0, out var hx, out var hy, out var hsc);
                headT.X = hx - groupOld.X;
                headT.Y = hy - groupOld.Y;
                // последние полклетки пути (но не больше половины всего пути) папка тает; обратно не проявляется
                var fadeLen = Math.Max(1, Math.Min(0.5 * g.Pw, 0.5 * (sEnd - sG)));
                opacity = Math.Min(opacity, Math.Clamp((sEnd - head0) / fadeLen, 0, 1));
                head.Opacity = opacity;
                headSc.ScaleX = headSc.ScaleY = hsc * (0.85 + 0.15 * opacity);
            }
            if (TapeTrace) _tapeLog?.AppendLine($"   [папка] t {now,6:0.0} мс  p {p:0.000}  s {head0,8:0.0}  прозрачность {opacity:0.00}");
            if (p >= 1) Finish();
        }
        _ungroupStop = Finish;
        FrameClock.Rendering += Tick;
    }

    /// <summary>
    /// Точка ленты на доле хода p. Обычный ход — TapeEase (лёгкий перелёт). Ход, сменённый на полпути, — с инерцией:
    /// s = From + (To−From)·(1−(1−p)³) + M0·p·(1−p)² — скорость в начале равна прежней, в конце — ноль, без рывка.
    /// В обоих случаях перелёт за цель не заходит в зону уменьшения у края строки.
    /// </summary>
    private static double TapeS(TapeTile t, double p)
    {
        double s;
        if (t.Inertia)
        {
            var q = 1 - p;
            s = t.From + (t.To - t.From) * (1 - q * q * q) + t.M0 * p * q * q;
        }
        else s = t.From + (t.To - t.From) * TapeEase.Ease(p);
        var dir = Math.Sign(t.To - t.From);
        if (dir == 0) dir = Math.Sign(s - t.To);
        // перелёт за цель — не дальше начала сжатия у края
        var over = (s - t.To) * dir;
        if (over > 0) s = t.To + dir * Math.Min(over, t.Geo.Slack(t.To, dir));
        // откат назад по инерции — тоже не к краю: иначе плитка у края сжималась и снова вырастала («дребезг»)
        var back = (t.From - s) * dir;
        if (back > 0) s = t.From - dir * Math.Min(back, t.Geo.Slack(t.From, -dir));
        return s;
    }

    /// <summary>Скорость плитки (единиц ленты за мс) на доле хода p.</summary>
    private static double TapeVelocity(TapeTile t, double p)
    {
        p = Math.Clamp(p, 0, 1);
        if (t.Inertia)
        {
            var q = 1 - p;
            return ((t.To - t.From) * 3 * q * q + t.M0 * q * (1 - 3 * p)) / t.Dur;
        }
        const double h = 0.01;
        double a = Math.Max(0, p - h), b = Math.Min(1, p + h);
        return (t.To - t.From) * (TapeEase.Ease(b) - TapeEase.Ease(a)) / (b - a) / t.Dur;
    }

    private static void TapePose(TapeTile t, double s)
    {
        t.Geo.Pose(s, out var x, out var y, out var scale);
        t.Tt.X = x - t.Layout.X;
        t.Tt.Y = y - t.Layout.Y;
        t.St.ScaleX = t.St.ScaleY = scale;
        t.LastS = s;
        t.LastX = x;
        t.LastY = y;
        t.LastScale = scale;
    }

    private void TapeEnd(TapeTile t)
    {
        t.Tt.X = t.Tt.Y = 0;
        t.St.ScaleX = t.St.ScaleY = 1;
        if (!_holdCache) t.C.CacheMode = null; // приехала — снова рисуем вектором, текст чёткий (при перетаскивании — после броска)
        _tape.Remove(t.C);
        _tapeList.Remove(t);
    }

    /// <summary>
    /// Убрать с ленты плитки, которых больше нет в сетке. Пересборка (удалили значок, пока сетка ещё ехала после прошлого
    /// удаления) создаёт плитки заново, а идущая анимация продолжала вести старые, уже выброшенные: они оставались
    /// «телами» на ленте и толкали новые — значки носились туда-сюда на бешеной скорости или застывали посреди хода.
    /// </summary>
    private void PruneTape()
    {
        for (var i = _tapeList.Count - 1; i >= 0; i--)
        {
            var c = _tapeList[i].C;
            if (VisualTreeHelper.GetParent(c) is not null) continue;
            _tape.Remove(c);
            _tapeList.RemoveAt(i);
        }
        _tapeObstacles.RemoveAll(o => VisualTreeHelper.GetParent(o.C) is null);
    }

    private void TapeStep(object? sender, EventArgs e)
    {
        PruneTape();
        var real = _tapeClock!.Elapsed.TotalMilliseconds;
        var dt = real - _tapeLast;
        _tapeLast = real;
        // первые кадры после пересборки самые тяжёлые (один из них бывал 31 мс при норме 5): долгий кадр считаем коротким,
        // иначе значки в начале «прыгали» сразу на несколько кадров вперёд
        // (предел — не меньше кадра по пределу частоты кадров: на 30 кадрах/с иначе начало шло медленнее)
        var adv = !Motion.Enabled ? 1e7 : ++_tapeFrames <= 12 ? Math.Min(dt, Motion.FirstFrameCapMs) : dt;
        _tapeNow += adv;
        PerfLog.AnimTick("паровозик");
        if (TapeTrace) _tapeLog?.AppendLine($"-- кадр {_tapeFrames}: время ленты {_tapeNow:0.0} мс, с прошлого кадра {dt:0.0} мс, плиток {_tapeList.Count}");
        _tapeEnts.Clear();
        _entUsed = 0;
        var decay = Math.Exp(-Math.Min(adv, 100) / RepelReturnMs);
        foreach (var t in _tapeList)
        {
            double baseS;
            var done = false;
            if (_tapeNow < t.Begin) baseS = t.From; // ещё не её очередь — стоит на старом месте
            else
            {
                var p = Math.Clamp((_tapeNow - t.Begin) / t.Dur, 0, 1);
                baseS = p >= 1 ? t.To : TapeS(t, p);
                done = p >= 1;
            }
            t.Off *= decay; // толчок затухает — плитка возвращается на свой путь
            var en = Ent();
            en.T = t;
            en.Geo = t.Geo;
            en.S = baseS + t.Off;
            en.Base = baseS;
            en.PrevBase = t.LastBase;
            en.Target = t.To;
            en.Body = t.Body;
            en.Done = done;
            _tapeEnts.Add(en);
        }
        if (Motion.Enabled) Repel(adv);
        foreach (var en in _tapeEnts)
        {
            if (en.T is { } t)
            {
                t.Off = en.S - en.Base; // накопленный толчок
                t.LastBase = en.Base;
                if (en.Done && Math.Abs(t.Off) < 0.3) TapeEnd(t);
                else TapePose(t, en.S);
                if (TapeTrace)
                    _tapeLog?.AppendLine($"   {(t.C.DataContext as TileVm)?.Name,-28} s {t.LastS,8:0.0}  толчок {t.Off,6:0.0}  размер {t.LastScale:0.00}");
            }
            else if (en.C is { } c && Math.Abs(en.S - en.Base) > 0.3 && !_tape.ContainsKey(c))
            {
                // стоящую плитку толкнули — она тоже встаёт на ленту (без своего хода) и потом возвращается на место
                var nt = TapeAttach(c, en.Geo, en.Layout);
                nt.From = nt.To = nt.LastS = nt.LastBase = en.Base;
                nt.Begin = _tapeNow;
                nt.Dur = 1;
                nt.Off = en.S - en.Base;
                _tape[c] = nt;
                _tapeList.Add(nt);
                TapePose(nt, en.S);
                _tapeObstacles.RemoveAll(o => o.C == c);
            }
        }
        if (_tapeList.Count > 0) return;
        FrameClock.Rendering -= TapeStep;
        _tapeClock = null;
        _tapeObstacles.Clear();
        QueueSweep();
        if (TapeTrace && _tapeLog is not null)
        {
            try { File.WriteAllText(System.IO.Path.Combine(DesktopPanel.Core.Paths.AppData, "logs", "tape.log"), _tapeLog.ToString()); }
            catch { }
            _tapeLog = null;
        }
    }
    // ---------- плитки отталкиваются ----------
    // У каждой плитки на ходу есть невидимое «тело»: прямоугольник шириной со значок или с подпись (что шире), с запасом на тень.
    // Тела соседей по ленте не сходятся ближе 1 пикселя экрана. Отталкивание мягкое: пока между телами расстояние как в обычной
    // сетке (или больше), соседи друг на друга не влияют; чем ближе, тем больше толкаемая перенимает скорость толкающей
    // (нарастает по экспоненте: вдали почти ничего, у самого края резко), на 1 пикселе — едет с ней вместе. Толчок — смещение
    // поверх своего хода плитки (TapeTile.Off), оно плавно затухает, и плитка возвращается на свой путь. Стоящую плитку тоже
    // можно толкнуть — она встаёт на ленту и потом возвращается на место. Плитки, которым надо разминуться (порядок меняется), не толкаются.
    private const double RepelReturnMs = 110, RepelCurve = 3;

    private sealed class TapeEnt
    {
        public TapeTile? T;
        public FrameworkElement? C; // стоящая плитка (препятствие)
        public Point Layout;
        public TapeGeo Geo;
        public double S, Base, PrevBase, Target, Body;
        public bool Done;
    }

    private readonly List<TapeEnt> _tapeEnts = new();
    private readonly List<(FrameworkElement C, TapeGeo Geo, Point Layout, double S, double Body)> _tapeObstacles = new(); // стоящие плитки последней перестройки

    // записи ленты на кадр — из запаса, а не новые: раньше каждый шаг создавал по объекту на плитку (при 200 Гц и двух
    // десятках плиток — тысячи в секунду, сборка мусора каждую секунду, пока едет «паровозик»). Живут только внутри шага.
    private readonly List<TapeEnt> _entPool = new();
    private int _entUsed;

    private TapeEnt Ent()
    {
        if (_entUsed == _entPool.Count) _entPool.Add(new TapeEnt());
        var e = _entPool[_entUsed++];
        e.T = null;
        e.C = null;
        e.Layout = default;
        e.Done = false;
        return e;
    }

    /// <summary>Половина ширины тела плитки: значок или настоящая ширина подписи (с переносом в две строки), что шире, + тень.</summary>
    private double BodyHalf(FrameworkElement c)
    {
        var icon = Application.Current.Resources["IconBox"] is double ib ? ib : Cfg.DesktopIconSize;
        var text = 0.0;
        if (FindFirst<TextBlock>(c) is { Text.Length: > 0 } tb)
        {
            try
            {
                var ft = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.White,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip)
                {
                    MaxTextWidth = Math.Max(1, double.IsNaN(tb.Width) ? tb.ActualWidth : tb.Width),
                    MaxTextHeight = double.IsInfinity(tb.MaxHeight) ? double.MaxValue : tb.MaxHeight,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                text = ft.Width + 4; // тень подписи (размытие 4) чуть шире самих букв
            }
            catch { }
        }
        return Math.Max(icon, text) / 2;
    }

    private static double ScaleAt(TapeGeo g, double s)
    {
        g.Pose(s, out _, out _, out var scale);
        return scale;
    }

    /// <summary>
    /// Плитки, которые уже вошли друг в друга (разминулись при смене порядка, и теперь толкаться им снова можно), расходятся
    /// не за кадр, а со скоростью не больше клетки за столько мс. Раньше такая пара за один кадр отскакивала на всю глубину
    /// перекрытия — значок «таранил» подпись соседа и тут же прыжком отлетал от него без анимации.
    /// </summary>
    private const double RepelSeparateMs = 160;

    private void Repel(double adv)
    {
        foreach (var (c, g, layout, so, body) in _tapeObstacles)
        {
            var en = Ent();
            en.C = c;
            en.Geo = g;
            en.Layout = layout;
            en.S = en.Base = en.PrevBase = en.Target = so;
            en.Body = body;
            _tapeEnts.Add(en);
        }
        if (_tapeEnts.Count < 2) return;
        var px = 1 / VisualTreeHelper.GetDpi(this).DpiScaleX;
        var sep = _tapeEnts[0].Geo.Pw * Math.Min(adv, 50) / RepelSeparateMs; // ⚠ предел расхождения за кадр — см. RepelSeparateMs
        _tapeEnts.Sort((a, b) => a.S.CompareTo(b.S));
        // «вплотную» для пары: 1 пиксель, а если подписи у соседей так широки, что и в обычной сетке тела ближе, — их зазор
        // в сетке. Иначе такие соседи и на своих местах считались врезавшимися: толкали друг друга без конца и «слипались»
        double Touch(TapeEnt a, TapeEnt b) => Math.Min(px, a.Geo.Pw - a.Body - b.Body - 0.5);
        // насколько тела a и b сейчас врезались в «зону влияния»: 0 — далеко (как в сетке), 1 — вплотную
        double Influence(TapeEnt a, TapeEnt b, double gap)
        {
            var rest = a.Geo.Pw - a.Body - b.Body; // зазор между телами в обычной сетке
            var touch = Touch(a, b);
            if (rest <= touch + 0.01) return gap <= touch ? 1 : 0;
            var x = Math.Clamp((rest - gap) / (rest - touch), 0, 1);
            return (Math.Exp(RepelCurve * x) - 1) / (Math.Exp(RepelCurve) - 1);
        }
        bool Pair(TapeEnt a, TapeEnt b) => SameGeo(a.Geo, b.Geo) && a.Target <= b.Target + 0.5;
        double Gap(TapeEnt a, TapeEnt b) => b.S - a.S - a.Body * ScaleAt(a.Geo, a.S) - b.Body * ScaleAt(b.Geo, b.S);

        // толкают вправо: слева направо, чтобы толчок передавался по цепочке
        for (var i = 0; i < _tapeEnts.Count - 1; i++)
        {
            TapeEnt a = _tapeEnts[i], b = _tapeEnts[i + 1];
            if (!Pair(a, b)) continue;
            var rel = (a.Base - a.PrevBase) - Math.Max(0, b.Base - b.PrevBase); // a своим ходом приблизилась к b
            if (rel <= 0) continue;
            var gap = Gap(a, b);
            b.S += Influence(a, b, gap + rel) * rel; // перенимает часть скорости — тем больше, чем ближе
            var g2 = Gap(a, b);
            var touch = Touch(a, b);
            // вплотную — едет вместе; уже были внутри друг друга — расходятся плавно
            if (g2 < touch) b.S += gap < touch ? Math.Min(touch - g2, rel + sep) : touch - g2;
        }
        // толкают влево: справа налево
        for (var i = _tapeEnts.Count - 2; i >= 0; i--)
        {
            TapeEnt a = _tapeEnts[i], b = _tapeEnts[i + 1];
            if (!Pair(a, b)) continue;
            // ⚠ знак: b своим ходом приближается к a (едет влево быстрее, чем a уходит влево) — rel < 0. Раньше здесь стояла
            // обратная разность: влево не толкал никто, и плитка, едущая влево, въезжала в соседа («таран»), а потом
            // отталкивание вправо разводило их рывком
            var rel = (b.Base - b.PrevBase) - Math.Min(0, a.Base - a.PrevBase);
            if (rel >= 0) continue;
            var gap = Gap(a, b);
            a.S += Influence(a, b, gap - rel) * rel;
            var g2 = Gap(a, b);
            var touch = Touch(a, b);
            if (g2 < touch) a.S -= gap < touch ? Math.Min(touch - g2, -rel + sep) : touch - g2;
        }
    }

    // ---------- «паровозик»: одна анимация на все перестройки сетки ----------
    // Эталон — смыкание после того, как значок положили в папку: плитки трогаются по одной от начала ленты с шагом 12 мс
    // (вся очередь не дольше 240 мс), каждая едет 504 мс с лёгким перелётом (TapeEase) — получается лесенка: строка 1, потом 2, потом 3.
    // Так же теперь едут: смыкание над корзиной, перестановка при перетаскивании, отмена, хвост при расформировании.
    // Сдвиг больше чем на клетку (расформирование, отмена) с тем же шагом заставил бы ближнюю плитку наехать на ещё стоящую дальнюю:
    // сжатие между соседями ≈ 4,4 · шаг / длительность · сдвиг. Поэтому шаг уменьшается, а длительность чуть растёт со сдвигом
    // (не больше 1,4×) — сжатие не больше ~0,12 плитки. На сдвиг в одну клетку это не влияет (эталон как был). Проверено в Python.
    private const double TrainStepMs = 12, TrainSpanMs = 240, TrainDurMs = 504;
    private const double TrainSqueeze = 13.7; // предел шага (мс) на сдвиг в одну клетку при 504 мс: сжатие соседей ≤ 0,12 плитки

    /// <summary>Длительность хода и шаг очереди для count плиток со сдвигом cells клеток (медиана).</summary>
    private static (double Dur, double Step) TrainTiming(int count, double cells)
    {
        var dur = TrainDurMs * Math.Min(1.4, 1 + 0.15 * Math.Max(0, cells - 1));
        var step = Math.Min(Math.Min(TrainStepMs, TrainSpanMs / Math.Max(1, count)), TrainSqueeze * dur / TrainDurMs / Math.Max(1, cells));
        return (dur, step);
    }

    /// <summary>
    /// Пустить плитки «паровозиком». lead — сколько мест в начале очереди занято чем-то своим (папка при расформировании).
    /// Возвращает выбранные длительность и шаг (или null, если никто не едет).
    /// </summary>
    private (double Dur, double Step)? TapeTrain(TapeGeo geo, IList<(FrameworkElement C, Point Old, Point Layout)> jobs, int lead = 0, double? cellsHint = null)
    {
        var moving = new List<(FrameworkElement C, Point Old, Point Layout, double S1, double D)>();
        _tapeObstacles.Clear();
        foreach (var (c, old, layout) in jobs)
        {
            double s0 = geo.SOf(old.X, old.Y), s1 = geo.SOf(layout.X, layout.Y);
            if (Math.Abs(s1 - s0) < 0.5)
            {
                TapeStart(c, geo, old, layout, 0, TrainDurMs); // уже на месте — только завершить идущий ход
                _tapeObstacles.Add((c, geo, layout, s1, BodyHalf(c))); // стоит — её можно толкнуть, но не наехать
            }
            else moving.Add((c, old, layout, s1, Math.Abs(s1 - s0)));
        }
        if (moving.Count == 0 && cellsHint is null) return null;
        var ds = moving.Select(m => m.D).OrderBy(d => d).ToList();
        var cells = cellsHint ?? ds[ds.Count / 2] / geo.Pw; // медиана: плитки у края строки едут по ленте дальше (через «подкулисье»)
        var (dur, step) = TrainTiming(moving.Count + lead, cells);
        var r = lead;
        foreach (var m in moving.OrderBy(m => m.S1)) // от начала ленты
        {
            // плитка уже едет (перестановка посреди хода) — не замораживаем её на время очереди, продолжает сразу
            var delay = _tape.TryGetValue(m.C, out var t) && _tapeNow >= t.Begin ? 0 : r * step;
            TapeStart(m.C, geo, m.Old, m.Layout, delay, dur);
            r++;
        }
        return (dur, step);
    }
    private const double PopStepMs = 35, PopSpanMs = 210;     // то же для вылета из папки

    private sealed record FlyIn(Image Img, TranslateTransform Tt, ScaleTransform St, Vector D, TileVm Target, Point From, double S0, double S1);

    /// <summary>
    /// Значки, которые были на экране, а теперь лежат в группе: делаем копию их плитки из снимка (cover) на старом месте,
    /// чтобы она улетела в плитку группы. Без снимка (сетка прокручена) значки просто пропадают.
    /// </summary>
    private List<FlyIn> MakeFlyIns(Dictionary<string, Point> before, Dictionary<string, (TileVm Vm, Size Size)> beforeVm, Image? cover)
    {
        var res = new List<FlyIn>();
        if (cover?.Source is not System.Windows.Media.Imaging.BitmapSource bmp || cover.Width < 1) return res;
        var scale = bmp.PixelWidth / cover.Width;
        var tl = TileGrid.TranslatePoint(new Point(), this);
        var now = _tiles.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var geo = MakeGeo(TileGrid, _tiles);
        foreach (var (key, (vm, size)) in beforeVm)
        {
            if (now.Contains(key) || vm.Item is not { } item || _store.GroupOf(item.Path) is not { } g) continue;
            var target = _tiles.FirstOrDefault(x => x.Key == "group:" + g.Id);
            if (target is null || TileGrid.ItemContainerGenerator.ContainerFromItem(target) is not FrameworkElement tc) continue;
            var from = before[key];
            var d = tc.TranslatePoint(new Point(), TileGrid) - from;
            try
            {
                var rect = new Int32Rect((int)(from.X * scale), (int)(from.Y * scale), (int)(size.Width * scale), (int)(size.Height * scale));
                if (rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > bmp.PixelWidth || rect.Y + rect.Height > bmp.PixelHeight) continue;
                var tt = new TranslateTransform();
                var st = new ScaleTransform(1, 1);
                var tg = new TransformGroup();
                tg.Children.Add(st);
                tg.Children.Add(tt);
                var img = new Image
                {
                    Source = new System.Windows.Media.Imaging.CroppedBitmap(bmp, rect), Width = size.Width, Height = size.Height,
                    IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.4), RenderTransform = tg,
                };
                Canvas.SetLeft(img, tl.X + from.X);
                Canvas.SetTop(img, tl.Y + from.Y);
                DragLayer.Children.Add(img);
                // путь по ленте (как у «паровозика»), а не наискосок; без ленты — прямо
                var to = LayoutOf(tc, TileGrid);
                var (s0, s1) = geo is { } gg ? (gg.SOf(from.X, from.Y), gg.SOf(to.X, to.Y)) : (double.NaN, double.NaN);
                res.Add(new FlyIn(img, tt, st, d, target, from, s0, s1));
            }
            catch (Exception ex) { Log.Warn($"desktop: не сделать копию плитки: {ex.Message}"); }
        }
        return res;
    }

    /// <summary>
    /// Пересобрать плитки: оставшиеся значки едут на новые места «паровозиком» (как при перетаскивании значка в ряд).
    /// appear — значки, которых раньше не было на экране (расформированная группа): они не летят, а мягко «проявляются» на месте.
    /// </summary>
    private void RebuildTrain(IList<string>? appear = null, string? appearFrom = null, bool popNew = false, IDictionary<string, string>? origin = null, bool flyIn = false)
    {
        PerfLog.Mark(appearFrom is not null ? "расформирование" : flyIn ? "отмена/возврат" : "пересборка с анимацией");
        var before = new Dictionary<string, Point>();
        var beforeVm = new Dictionary<string, (TileVm Vm, Size Size)>();
        var beforeOrder = _tiles.Select(v => v.Key).ToList();
        foreach (var vm in _tiles)
            if (TileGrid.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement c)
            {
                before[vm.Key] = c.TranslatePoint(new Point(), TileGrid);
                beforeVm[vm.Key] = (vm, new Size(c.ActualWidth, c.ActualHeight));
            }
        // Внутри Rebuild вызов оболочки Windows (корзина) качает сообщения — окно успевало отрисоваться на полпути.
        // Корзину обновляем уже после, а на время пересборки поверх сетки лежит её снимок (без мигания).
        var cover = MakeCover(full: flyIn || appearFrom is not null);
        _skipTrashUpdate = true;
        // ⚠ плитки — невидимыми, снимок — до первого кадра анимации: иначе кадр с плитками на конечных местах
        _unbornAll = true;
        _unborn = appear is null ? null : new HashSet<string>(appear, StringComparer.OrdinalIgnoreCase);
        // без вылетов и влётов (добавили в папку, вынули из неё) прежние плитки остаются — см. _reuse
        if (!flyIn && appear is null && appearFrom is null && !popNew)
        {
            _reuse = new Dictionary<string, TileVm>(StringComparer.OrdinalIgnoreCase);
            foreach (var vm in _tiles) _reuse.TryAdd(vm.Key, vm);
        }
        else _freshTiles = true;
        try
        {
            Rebuild();
            TileGrid.UpdateLayout();
            PruneTape(); // ⚠ старые плитки (их пересоздали) — с ленты долой, иначе толкают новые
        }
        catch
        {
            RemoveCover(cover);
            throw;
        }
        finally { _skipTrashUpdate = false; _unborn = null; _unbornAll = false; _reuse = null; _freshTiles = false; }
        if (Cfg.ShowRecycleBin) Dispatcher.BeginInvoke(UpdateTrash, DispatcherPriority.Background);
        var flyEase = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        var flyOutEase = new System.Windows.Media.Animation.BackEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.45 }; // тот же ход, что у «паровозика» (TapeEase)
        var hidden = _tiles.Where(x => x.IsDragged && x.Key != _landKey && (appear is null || !appear.Contains(x.Key, StringComparer.OrdinalIgnoreCase))).ToList();
        var movers = new List<(TranslateTransform Tt, ScaleTransform St, Vector D, double Begin, double Dur, double S0)>(); // только вылетающие из группы
        // значки, которые ушли в группу (отмена расформирования и т.п.): летят в неё по обратной траектории — копией своей плитки
        var sprites = flyIn ? MakeFlyIns(before, beforeVm, cover) : new List<FlyIn>(); // только при отмене: при ручном объединении значок в группу влетает сам
        foreach (var s in sprites) hidden.Remove(s.Target); // плитка группы покажется, когда значки до неё долетят
        // отмена расформирования — обратная «гармошка»: значки ползут по ленте в папку тем же ходом, что и хвост (TapeTrain)
        var spriteGeo = sprites.Count > 0 ? MakeGeo(TileGrid, _tiles) : null;
        var spriteDur = TrainTiming(1, Math.Max(1, sprites.Count - 1)).Dur;
        var cachedTiles = new List<FrameworkElement>();
        var moverNew = new List<int>(); // для каждого вылетающего: новое место в списке плиток
        var popped = new List<(FrameworkElement C, ScaleTransform St, double S0, TileVm Vm, double Begin)>();
        var jobs = new List<(FrameworkElement C, Point Old, Point Layout, int NewIdx, int OldIdx)>(); // остальные плитки едут по ленте
        // расформирование группы вручную: папка летит по ленте, значки рождаются под ней, остальные плитки — цепочка на пружинках
        var ungroup = appear is { Count: > 0 } && appearFrom is not null && before.ContainsKey(appearFrom);
        var members = new List<(FrameworkElement C, TileVm Vm, Point Layout)>();
        foreach (var vm in _tiles)
        {
            if (TileGrid.ItemContainerGenerator.ContainerFromItem(vm) is not FrameworkElement c) continue;
            var isNew = !before.TryGetValue(vm.Key, out var old);
            var layout = LayoutOf(c, TileGrid);
            var pop = isNew && (popNew || appear is not null && appear.Any(p => string.Equals(p, vm.Key, StringComparison.OrdinalIgnoreCase)));
            if (isNew && !pop) continue;
            if (pop && ungroup)
            {
                members.Add((c, vm, layout));
                continue;
            }
            if (!pop)
            {
                jobs.Add((c, old, layout, _tiles.IndexOf(vm), beforeOrder.IndexOf(vm.Key)));
                continue;
            }
            // вылетают из плитки своей прежней группы (если она была на экране), иначе просто вырастают на месте
            var src = origin is not null && origin.TryGetValue(vm.Key, out var og) ? og : appearFrom;
            var fromGroup = src is not null && before.TryGetValue(src, out old);
            var d = fromGroup ? old - layout : new Vector();
            const double s0 = 22.0 / 64; // из группы вылетают размером с мини-значок и растут по пути
            var tt = new TranslateTransform(d.X, d.Y); // сразу на старом месте — ни кадра в конечной точке
            var st = new ScaleTransform(s0, s0);
            var group = new TransformGroup();
            group.Children.Add(st);
            group.Children.Add(tt);
            c.RenderTransformOrigin = new Point(0.5, 0.4);
            c.RenderTransform = group;
            if (!TileCacheOn) c.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
            cachedTiles.Add(c);
            moverNew.Add(_tiles.IndexOf(vm));
            movers.Add((tt, st, d, 0, TrainDurMs, s0));
            popped.Add((c, st, s0, vm, 0));
        }
        // остальные плитки — «паровозиком» (TapeTrain): лесенкой от начала ленты, строка за строкой
        var ungrouping = false;
        if (ungroup && members.Count > 0 && MakeGeo(TileGrid, _tiles) is { } ugeo)
        {
            var lastIdx = members.Max(m => _tiles.IndexOf(m.Vm));
            var tailJobs = jobs.Where(j => j.NewIdx > lastIdx && j.OldIdx >= 0).ToList();
            jobs.RemoveAll(j => tailJobs.Contains(j));
            StartUngroup(ugeo, before[appearFrom!], beforeVm[appearFrom!].Size, members, tailJobs.Select(j => (j.C, j.Old, j.Layout)).ToList(), cover);
            ungrouping = true;
        }
        else foreach (var m in members) m.Vm.IsDragged = false; // не вышло построить ленту — просто показываем
        if (jobs.Count > 0 && MakeGeo(TileGrid, _tiles) is { } geo)
            TapeTrain(geo, jobs.Select(j => (j.C, j.Old, j.Layout)).ToList());
        // Вылет из папки: тоже небольшая очередь, но дальние плитки стартуют первыми, а ближняя к папке — последней. Тогда дальняя
        // всегда впереди ближней на одной линии и обгона (а значит пересечений) нет, как и при одновременном старте.
        var pops = Enumerable.Range(0, movers.Count).OrderBy(i => moverNew[i]).ToList();
        var popStep = Math.Min(PopStepMs, PopSpanMs / Math.Max(1, pops.Count));
        for (var r = 0; r < pops.Count; r++)
        {
            var mv = movers[pops[r]];
            var begin = (pops.Count - 1 - r) * popStep;
            movers[pops[r]] = (mv.Tt, mv.St, mv.D, begin, mv.Dur, mv.S0);
            var pp = popped[pops[r]];
            popped[pops[r]] = (pp.C, pp.St, pp.S0, pp.Vm, begin); // плитка показывается в свой старт
        }
        if (movers.Count == 0 && sprites.Count == 0 && _tapeList.Count == 0 && !ungrouping)
        {
            foreach (var h in hidden) h.IsDragged = false;
            RemoveCover(cover);
            return;
        }
        // снимок старого состояния лежит до первого кадра анимации: к этому моменту плитки стоят на стартовых местах;
        // свой отсчёт по кадрам, а не часы анимаций WPF (для остальных плиток — то же самое в TapeStep)
        Stopwatch? sw = null;
        var frame = 0;
        double lastReal = 0, virtualMs = 0;
        void Step(object? s, EventArgs a)
        {
            PerfLog.AnimTick("пересборка");
            if (sw is null)
            {
                sw = Stopwatch.StartNew();
                foreach (var h in hidden) h.IsDragged = false;
                RemoveCover(cover);
            }
            // первый кадр после пересборки самый тяжёлый (рисуются все новые плитки): время, которое он съел, в анимацию не идёт
            if (frame++ == 1) sw.Restart();
            var real = sw.Elapsed.TotalMilliseconds;
            var dt = real - lastReal;
            lastReal = real;
            if (frame > 2) virtualMs += frame < 12 ? Math.Min(dt, Motion.FirstFrameCapMs) : dt;
            var ms = !Motion.Enabled ? double.MaxValue / 4 : frame <= 2 ? 0 : virtualMs;
            var alive = false;
            for (var i = 0; i < movers.Count; i++)
            {
                var m = movers[i];
                if (ms < m.Begin) { alive = true; continue; } // ещё не её очередь — стоит на старом месте, считать нечего
                var p = Math.Clamp((ms - m.Begin) / m.Dur, 0, 1);
                var left = 1 - flyOutEase.Ease(p);
                m.Tt.X = m.D.X * left;
                m.Tt.Y = m.D.Y * left;
                m.St.ScaleX = m.St.ScaleY = 1 - (1 - m.S0) * left;
                if (p < 1) alive = true;
            }
            foreach (var sp in sprites)
            {
                if (spriteGeo is { } sg && !double.IsNaN(sp.S0))
                {
                    var p = Math.Clamp(ms / spriteDur, 0, 1);
                    var f = TapeEase.Ease(p);
                    var sNow = sp.S0 + (sp.S1 - sp.S0) * f;
                    sg.Pose(sNow, out var x, out var y, out var edge); // у края строки тает и выходит с другого края следующей — как плитки
                    sp.Tt.X = x - sp.From.X;
                    sp.Tt.Y = y - sp.From.Y;
                    sp.St.ScaleX = sp.St.ScaleY = edge * Math.Max(0, 1 - (1 - 22.0 / 64) * f);
                    // тает последние полклетки до папки (обратно не проявляется); значок, стоявший на месте папки, — к середине хода
                    var left = Math.Abs(sp.S1 - sp.S0) < 1 ? 1 - p / 0.45 : Math.Abs(sp.S1 - sNow) / (0.5 * sg.Pw);
                    sp.Img.Opacity = Math.Min(sp.Img.Opacity, Math.Clamp(left, 0, 1));
                    if (p >= 0.45 && sp.Target.IsDragged) sp.Target.IsDragged = false;
                    if (p < 1) alive = true;
                }
                else
                {
                    var p = Math.Clamp(ms / FlyMs, 0, 1);
                    var f = flyEase.Ease(p); // ленты нет — по прямой, плавно
                    sp.Tt.X = sp.D.X * f;
                    sp.Tt.Y = sp.D.Y * f;
                    sp.St.ScaleX = sp.St.ScaleY = 1 - (1 - 22.0 / 64) * f;
                    sp.Img.Opacity = p > 0.8 ? Math.Clamp((1 - p) / 0.2, 0, 1) : 1;
                    if (p >= 0.7 && sp.Target.IsDragged) sp.Target.IsDragged = false;
                    if (p < 1) alive = true;
                }
            }
            foreach (var (_, _, _, vm, begin) in popped)
            {
                // плитка родилась невидимой; показываем её только когда пришла её очередь (размер к этому моменту уже стартовый)
                if (ms >= begin && vm.IsDragged) vm.IsDragged = false;
            }
            if (alive) return;
            foreach (var (_, _, _, vm, _) in popped) vm.IsDragged = false;
            foreach (var sp in sprites)
            {
                sp.Target.IsDragged = false;
                DragLayer.Children.Remove(sp.Img);
            }
            FrameClock.Rendering -= Step;
            _trainBusy--;
            QueueSweep();
            foreach (var c in cachedTiles) { c.CacheMode = null; c.Opacity = 1; } // приехали — снова рисуем вектором, текст чёткий
        }
        _trainBusy++;
        FrameClock.Rendering += Step;
    }

    private int _trainBusy; // идут анимации пересборки (плитки показываются каждая в свой кадр)

    /// <summary>
    /// Бросили на другой значок: значок в руке уменьшается до размера мини-значка в папке и «влетает» на его место
    /// (у новой папки это второй квадрант, у существующей — следующий свободный).
    /// </summary>
    private string? _mergeTargetKey;
    private string? _holdKey, _holdPath; // мини-значок в группе скрыт, пока значок до неё не долетел
    private int _holdIdx;

    private void ReleaseHold()
    {
        if (_holdKey is null) return;
        var vm = _tiles.FirstOrDefault(x => x.Key == _holdKey);
        if (vm is not null && _holdPath is not null && _holdIdx < vm.Previews.Count) vm.Previews[_holdIdx] = IconFor(_holdPath);
        _holdKey = null;
    }

    private bool _flyingToTrash;

    /// <summary>Идущий полёт значка-призрака (в папку или в корзину): его надо оборвать, если за новый значок схватились раньше, чем он долетел,
    /// иначе старый полёт продолжал двигать и гасить призрак нового значка.</summary>
    private EventHandler? _flightStep;
    private Action? _flightFinish;

    private void CancelFlight()
    {
        if (_flightStep is null) return;
        FrameClock.Rendering -= _flightStep;
        _flightStep = null;
        var finish = _flightFinish;
        _flightFinish = null;
        finish?.Invoke(); // довести дело до конца сразу: файл уходит в корзину, значок закрепляется в группе
    }

    /// <summary>Где у корзины крышка: чуть выше середины значка — значок сжимается и влетает именно туда.</summary>
    private Point? TrashLidPoint() =>
        TrashZone is { IsVisible: true } ? TrashGlyph.TranslatePoint(new Point(TrashGlyph.ActualWidth / 2, TrashGlyph.ActualHeight * 0.3), this) : null;

    /// <summary>Значок отпустили над корзиной: он уменьшается и плавно влетает в крышку, потом файл уходит в корзину.</summary>
    private bool FlyGhostToTrash(DragState drag, DesktopItem item)
    {
        if (TrashLidPoint() is not { } first) return false;
        var icon = (double)Cfg.DesktopIconSize;
        var start = new Point(Canvas.GetLeft(Ghost), Canvas.GetTop(Ghost));
        var pop = Ghost.RenderTransform as ScaleTransform;
        var startScale = pop?.ScaleX ?? 1;
        if (pop is not null) Anim.Stop(pop, "pop");
        var ease = FlyEase;
        var sw = AnimWatch.StartNew();
        const double dur = 450;
        var last = first;
        _flyingToTrash = true;
        _landKey = drag.Vm.Key; // пересборка не должна показать плитку, пока значок летит
        _deleting.Add(item.Path); // и вообще не должна его находить: для сетки его уже нет (её сомкнули ещё над корзиной)
        void Step(object? s, EventArgs a)
        {
            PerfLog.AnimTick("полёт в корзину");
            var p = Math.Min(1, sw.Elapsed.TotalMilliseconds / dur);
            var e = ease.Ease(p);
            if (TrashLidPoint() is { } lid) last = lid; // корзина сама сжимается обратно — летим за крышкой
            var scale = startScale + (0.12 - startScale) * e;
            if (pop is not null) pop.ScaleX = pop.ScaleY = scale;
            // значок сжимается вокруг середины всей плитки (вместе с подписью), а не вокруг своего центра, поэтому верх считаем так,
            // чтобы центр именно значка в каждом кадре был на пути к крышке и в конце лёг точно на неё
            var h = Ghost.ActualHeight > 1 ? Ghost.ActualHeight : icon + 60;
            var iconCenter = 8 + icon / 2;
            var endTop = last.Y - h / 2 - (iconCenter - h / 2) * scale;
            Canvas.SetLeft(Ghost, start.X + (last.X - Ghost.Width / 2 - start.X) * e);
            Canvas.SetTop(Ghost, start.Y + (endTop - start.Y) * e);
            Ghost.Opacity = p > 0.8 ? 0.95 * (1 - (p - 0.8) / 0.2) : 0.95;
            GhostText.Opacity = Math.Max(0, 1 - p / 0.5);
            if (p < 1) return;
            FrameClock.Rendering -= Step;
            Finish(hide: true);
        }
        void Finish(bool hide)
        {
            _flyingToTrash = false;
            _flightStep = null;
            _flightFinish = null;
            FlushReload(); // рабочий стол менялся, пока значок летел, — перечитать (после удаления/посадки)
            if (hide) Ghost.Visibility = Visibility.Collapsed;
            _landKey = null;
            // Удаление — в своём потоке (DeleteItem): раньше оболочка Windows работала прямо в потоке интерфейса и прокачивала
            // его сообщения — посреди шага успевала пройти пересборка от прошлого удаления (после двух удалений подряд плитки
            // застывали или носились), а само удаление держало интерфейс 80–90 мс.
            DeleteItem(item, pulse: false, done: ok =>
            {
                if (!ok) Reload(); // не вышло — значок возвращается на место
            });
        }
        CancelFlight();
        _flightStep = Step;
        _flightFinish = () => Finish(hide: false);
        FrameClock.Rendering += Step;
        return true;
    }

    private bool ShrinkGhostInto(DragState drag, TileVm target)
    {
        var grid = drag.Source == _groupTiles ? GroupGrid : TileGrid;
        if (grid.ItemContainerGenerator.ContainerFromItem(target) is not FrameworkElement c || !c.IsVisible) return false;
        var icon = (double)Cfg.DesktopIconSize;
        var idx = target.Group is { } g ? Math.Min(g.Items.Count, 3) : 1;
        _mergeTargetKey = target.Key;
        _holdIdx = idx;
        _holdPath = drag.Vm.Key;
        _holdKey = target.Group is { Items.Count: < 4 } ? target.Key : null; // в существующей группе есть свободное место
        var last = MiniPoint(_mergeTargetKey, idx, icon) ?? new Point(Canvas.GetLeft(Ghost) + Ghost.Width / 2, Canvas.GetTop(Ghost) + 8 + icon / 2);
        var start = new Point(Canvas.GetLeft(Ghost), Canvas.GetTop(Ghost));
        var pop = Ghost.RenderTransform as ScaleTransform;
        var startScale = pop?.ScaleX ?? 1;
        if (pop is not null) Anim.Stop(pop, "pop");
        var ease = FlyEase;
        var sw = AnimWatch.StartNew();
        var dur = 450.0;
        // каждый кадр заново смотрим, где сейчас плитка-цель: она сама уезжает на новое место (группа встаёт на своё) —
        // значок летит прямо в неё и за ней, а не в точку, где она стояла в момент отпускания
        void Step(object? s, EventArgs a)
        {
            PerfLog.AnimTick("полёт в папку");
            var p = Math.Min(1, sw.Elapsed.TotalMilliseconds / dur);
            var e = ease.Ease(p);
            if (MiniPoint(_mergeTargetKey!, idx, icon) is { } tp) last = tp;
            Canvas.SetLeft(Ghost, start.X + (last.X - Ghost.Width / 2 - start.X) * e);
            Canvas.SetTop(Ghost, start.Y + (last.Y - (8 + icon / 2) - start.Y) * e);
            if (pop is not null)
            {
                pop.ScaleX = pop.ScaleY = startScale + (22.0 / 64 - startScale) * e;
            }
            Ghost.Opacity = p > 0.75 ? 0.95 * (1 - (p - 0.75) / 0.25) : 0.95;
            GhostText.Opacity = Math.Max(0, 1 - p / 0.55); // подпись тает к моменту, когда значок дойдёт до группы
            if (p < 1) return;
            FrameClock.Rendering -= Step;
            Finish(hide: true);
        }
        void Finish(bool hide)
        {
            _flightStep = null;
            _flightFinish = null;
            FlushReload(); // рабочий стол менялся, пока значок летел, — перечитать (после удаления/посадки)
            if (hide) Ghost.Visibility = Visibility.Collapsed;
            drag.Vm.IsDragged = false;
            _landKey = null;
            ReleaseHold(); // долетел и закрепился — мини-значок появляется в группе
        }
        CancelFlight();
        _flightStep = Step;
        _flightFinish = () => Finish(hide: false);
        FrameClock.Rendering += Step;
        return true;
    }

    /// <summary>Где сейчас (с учётом идущей анимации) место мини-значка номер idx в плитке с этим ключом.</summary>
    private Point? MiniPoint(string key, int idx, double icon)
    {
        var vm = _tiles.FirstOrDefault(x => x.Key == key);
        if (vm is null || TileGrid.ItemContainerGenerator.ContainerFromItem(vm) is not FrameworkElement c || !c.IsVisible) return null;
        // от самого значка плитки (Viewbox 64×64), вместе с текущим смещением и размером анимации: рамка кнопки (1 px) и округление
        // раскладки учтены сами. Центры мини-значков в сетке 64: рамка 1 + поле 7 + половина клетки 12 = 20, вторая клетка — 44.
        if (FindFirst<Viewbox>(c) is not { ActualWidth: > 1 } vb) return null;
        var k = vb.ActualWidth / 64;
        return vb.TranslatePoint(new Point((idx % 2 == 0 ? 20 : 44) * k, (idx < 2 ? 20 : 44) * k), this);
    }

    /// <summary>Где значок плитки встанет по сетке (левый верхний угол Viewbox, без смещения идущей анимации); null — не нашёлся.</summary>
    private Point? IconSlot(FrameworkElement c)
    {
        if (_tape.TryGetValue(c, out var t)) TapeEnd(t); // плитка невидима (её место держит призрак) — ставим сразу на место
        if (FindFirst<Viewbox>(c) is not { ActualWidth: > 1 } vb) return null;
        return vb.TranslatePoint(new Point(), this) - OffsetOf(c);
    }

    /// <summary>Значок отпустили: он с пружинкой «долетает» из точки, где его держали, на своё место в сетке.</summary>
    private bool LandGhost(DragState drag)
    {
        if (drag.Vm.Kind == TileVm.Kinds.Hit || drag.OutOfGroup) return false;
        if (drag.Target is { Kind: TileVm.Kinds.Trash } && drag.Vm.Item is { } trashed) return FlyGhostToTrash(drag, trashed);
        if (drag.Target is { } tv) return tv.Kind is TileVm.Kinds.Item or TileVm.Kinds.Group && ShrinkGhostInto(drag, tv);
        var grid = drag.Source == _groupTiles ? GroupGrid : TileGrid;
        if (grid.ItemContainerGenerator.ContainerFromItem(drag.Vm) is not FrameworkElement c || !c.IsVisible) return false;
        // в общей сетке без пересборки после броска садится сама плитка — подменять в конце нечего (ни «тэпа», ни смены цвета)
        if (drag.Source == _tiles && !drag.Detached && LandTile(drag, c)) return true;
        // призрак садится ровно туда, где нарисован значок плитки (у призрака значок по центру ширины и на 8 ниже верха):
        // раньше считали от края плитки и промахивались на рамку кнопки — в конце значок «тэпался» на пиксель
        if (IconSlot(c) is not { } icon) return false;
        var iconSize = FindFirst<Viewbox>(c)!.ActualWidth;
        var rect = new Rect(icon.X + iconSize / 2 - Ghost.Width / 2, icon.Y - 8, Ghost.Width, 0);
        var cx = icon.X + iconSize / 2;
        double x0 = Canvas.GetLeft(Ghost), y0 = Canvas.GetTop(Ghost), x1 = cx - Ghost.Width / 2, y1 = rect.Y;
        var pop = Ghost.RenderTransform as ScaleTransform; // «вставляется» обратно: размер возвращается к обычному
        var s0 = pop?.ScaleX ?? 1;
        if (pop is not null) Anim.Stop(pop, "pop");
        Anim.Run(Ghost, "land", 380, k =>
        {
            Canvas.SetLeft(Ghost, x0 + (x1 - x0) * k);
            Canvas.SetTop(Ghost, y0 + (y1 - y0) * k);
            if (pop is not null) pop.ScaleX = pop.ScaleY = s0 + (1 - s0) * k;
        }, LandEase, () =>
        {
            if (_drag is { Started: true }) return; // уже тащат следующий
            Ghost.Visibility = Visibility.Collapsed;
            _landKey = null;
            foreach (var vm in _tiles.Concat(_groupTiles)) vm.IsDragged = false; // значок встал на место
            drag.Vm.IsDragged = false;
        });
        return true;
    }

    /// <summary>
    /// Посадка самой плитки: в момент отпускания призрак убирается, а на его месте (тот же центр значка и размер) показывается плитка,
    /// которая едет на своё место и возвращается к обычному размеру. Подмена — в начале, на ходу, а не в конце, когда всё уже стоит:
    /// раньше призрак (полупрозрачный, со своей отрисовкой текста) в конце сменялся плиткой и было видно «тэп» и чуть другой цвет.
    /// </summary>
    private bool LandTile(DragState drag, FrameworkElement c)
    {
        if (IconSlot(c) is null || FindFirst<Viewbox>(c) is not { ActualWidth: > 1 } vb || GhostImage.ActualWidth < 1) return false;
        var sz = vb.ActualWidth;
        var from = GhostImage.TranslatePoint(new Point(GhostImage.ActualWidth / 2, GhostImage.ActualHeight / 2), this); // с учётом размера призрака
        var to = vb.TranslatePoint(new Point(sz / 2, sz / 2), this);
        var d0 = from - to;
        var s0 = (Ghost.RenderTransform as ScaleTransform)?.ScaleX ?? 1;
        s0 *= GhostImage.ActualWidth / sz; // на случай, если значок призрака другого размера
        var origin = vb.TranslatePoint(new Point(sz / 2, sz / 2), c); // плитка растёт вокруг центра значка
        var st = new ScaleTransform(s0, s0, origin.X, origin.Y);
        var tt = new TranslateTransform(d0.X, d0.Y);
        var group = new TransformGroup();
        group.Children.Add(st);
        group.Children.Add(tt);
        c.RenderTransform = group;
        Anim.Stop(Ghost, "land");
        Ghost.Visibility = Visibility.Collapsed;
        drag.Vm.IsDragged = false;
        drag.LandedTile = true;
        var vm = drag.Vm;
        vm.NoHover = true;
        void Leave(object s, MouseEventArgs a)
        {
            c.MouseLeave -= Leave;
            vm.NoHover = false;
        }
        c.MouseLeave += Leave;
        var ease = LandEase;
        const double dur = 380;
        var sw = AnimWatch.StartNew();
        void Step(object? s, EventArgs a)
        {
            PerfLog.AnimTick("посадка плитки");
            var p = Math.Min(1, sw.Elapsed.TotalMilliseconds / dur);
            var left = 1 - ease.Ease(p);
            tt.X = d0.X * left;
            tt.Y = d0.Y * left;
            st.ScaleX = st.ScaleY = 1 + (s0 - 1) * left;
            c.Opacity = 0.95 + 0.05 * p; // призрак был чуть прозрачным — плитка плавно доходит до обычной
            if (p >= 1) Finish();
        }
        void Finish()
        {
            FrameClock.Rendering -= Step;
            _flightStep = null;
            _flightFinish = null;
            FlushReload(); // рабочий стол менялся, пока значок летел, — перечитать (после удаления/посадки)
            if (c.RenderTransform == group) c.RenderTransform = Transform.Identity;
            c.ClearValue(OpacityProperty); // c — контейнер плитки, привязка прозрачности висит на кнопке внутри и не трогается
        }
        CancelFlight();
        _flightStep = Step;
        _flightFinish = Finish;
        FrameClock.Rendering += Step;
        return true;
    }

    /// <summary>
    /// Переставить плитку так, чтобы остальные не прыгали, а плавно ехали на новые места с лёгкой пружинкой.
    /// Анимации WPF идут с частотой монитора. Запоминаем, где плитки были на экране (с учётом идущей анимации),
    /// переставляем, и каждую «возвращаем» на старое место смещением, которое сходит к нулю.
    /// </summary>
    private void MoveAnimated(ObservableCollection<TileVm> list, ItemsControl grid, int from, int to) =>
        ReflowAnimated(list, grid, () => list.Move(from, to));

    /// <summary>Любое изменение списка плиток (перестановка, убрать, вставить) — остальные плитки плавно едут на новые места по ленте.</summary>
    private void ReflowAnimated(ObservableCollection<TileVm> list, ItemsControl grid, Action change)
    {
        var before = new Dictionary<TileVm, Point>(); // где плитки на экране сейчас (с учётом идущей анимации)
        foreach (var vm in list)
            if (grid.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement c)
                before[vm] = c.TranslatePoint(new Point(), grid);
        change();
        grid.UpdateLayout();
        if (MakeGeo(grid, list) is not { } geo) return;
        var jobs = new List<(FrameworkElement C, Point Old, Point Layout)>();
        foreach (var vm in list)
        {
            if (!before.TryGetValue(vm, out var old)) continue;
            if (grid.ItemContainerGenerator.ContainerFromItem(vm) is not FrameworkElement c) continue;
            if (vm == _drag?.Vm) // плитка в руке невидима: ставим сразу на новое место, иначе при посадке призрака она ещё ехала бы
            {
                if (_tape.TryGetValue(c, out var held)) TapeEnd(held);
                continue;
            }
            jobs.Add((c, old, LayoutOf(c, grid))); // где плитка стоит по сетке, без смещения
        }
        TapeTrain(geo, jobs); // так же, как смыкание после папки: лесенкой
    }
    /// <summary>
    /// Плитка под точкой по её месту в сетке (без идущей анимации) и её прямоугольник. Место — по раскладке (GetOffset от
    /// панели плиток), как в LayoutOf: «экран минус смещение» ошибался у плиток, сжатых у края строки. И пересчёт координат —
    /// один на панель, а не на каждую плитку: это зовётся каждый кадр, пока значок в руке, и TranslatePoint на плитку
    /// создавал объекты (сборка мусора раз в секунду).
    /// </summary>
    private (TileVm? Vm, Rect Rect) TileAtLogical(Point pos, ObservableCollection<TileVm> list, ItemsControl grid)
    {
        UIElement? panel = null;
        var origin = new Point();
        var gen = grid.ItemContainerGenerator;
        for (var i = 0; i < list.Count; i++)
        {
            if (gen.ContainerFromIndex(i) is not FrameworkElement c || !c.IsVisible) continue;
            if (VisualTreeHelper.GetParent(c) is not UIElement p) continue;
            if (!ReferenceEquals(p, panel))
            {
                panel = p;
                origin = p.TranslatePoint(new Point(), this);
            }
            var off = VisualTreeHelper.GetOffset(c);
            var r = new Rect(origin.X + off.X, origin.Y + off.Y, c.ActualWidth, c.ActualHeight);
            if (r.Contains(pos)) return (list[i], r);
        }
        return (null, default);
    }

    private void Root_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is null) return;
        if (_drag.Started)
        {
            e.Handled = true;
            _suppressClickUntil = DateTime.Now.AddMilliseconds(300);
            EndDrag(apply: true);
        }
        else
        {
            _drag = null; // это был обычный щелчок — его обработает кнопка
            FlushReload();
        }
    }

    private void Root_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // кнопка плитки теряет захват, когда его забираем мы, — это не конец перетаскивания
        if (!ReferenceEquals(e.OriginalSource, this)) return;
        if (_drag is { Started: true }) EndDrag(apply: false);
    }

    /// <summary>Esc во время перетаскивания — отменить. true — было что отменять.</summary>
    public bool CancelDrag()
    {
        if (_drag is not { Started: true }) return false;
        EndDrag(apply: false);
        return true;
    }

    private void EndDrag(bool apply)
    {
        try { EndDragCore(apply); }
        finally { FlushReload(); } // рабочий стол менялся, пока значок был в руке, — перечитать
    }

    private void EndDragCore(bool apply)
    {
        if (_dragDirty && _drag is { Started: true }) ApplyDragPos(); // движение после последнего кадра — до броска

        StopDragFrames();
        ReleaseHeldCache();
        var drag = _drag;
        _drag = null;
        _dwell.Stop();
        _edge.Stop();
        if (drag is null) return;
        var landing = apply && drag.Started && LandGhost(drag);
        if (landing && !drag.LandedTile) _landKey = drag.Vm.Key;
        else
        {
            Ghost.Visibility = Visibility.Collapsed;
            drag.Vm.IsDragged = false;
        }
        DropBar.Visibility = Visibility.Collapsed;
        GroupHint.Visibility = Visibility.Collapsed;
        if (drag.Target is not null) drag.Target.IsDropTarget = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!drag.Started) return;
        if (!apply)
        {
            if (drag.Reordered) Rebuild();
            return;
        }

        var key = drag.Vm.Key;
        if (drag.Vm.Kind == TileVm.Kinds.Hit)
        {
            if (drag.OverDropBar && drag.Vm.Path is { } target)
                if (SystemSearch.AddToDesktop(target, drag.Vm.Name) is null) ShowToast(Loc.T("desktop.search.add_failed")); // об успехе не пишем — ярлык и так появится
            return;
        }
        if (drag.OutOfGroup)
        {
            _store.RemoveFromGroup(key, Loc.T("desktop.undo.remove_from_group"));
            Rebuild();
            return;
        }
        if (drag.Target is { } t)
        {
            if (t.Kind == TileVm.Kinds.Trash)
            {
                // летящий значок удалится, когда долетит; иначе — сразу, в своём потоке (DeleteItem)
                if (drag.Vm.Item is { } item && !_flyingToTrash)
                    DeleteItem(item, done: ok =>
                    {
                        if (!ok) Reload(); // не вышло — значок возвращается
                    });
                else if (drag.Vm.Group is { } gg) DeleteGroup(gg);
                if (drag.Reordered && !_flyingToTrash) Rebuild(); // летящий значок: сетка уже сомкнулась — не перестраиваем, иначе она дёрнется
                return;
            }
            // порядок плиток на экране на момент броска: группа встаёт ровно туда, где стоял принимающий значок
            var cur = _tiles.Where(x => x.Kind is TileVm.Kinds.Item or TileVm.Kinds.Group).Select(x => x.Key)
                .Where(k => !string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (t.Group is { } g) _store.AddToGroup(g, key, Loc.T("desktop.undo.add_to_group"));
            else if (t.Item is { } ti)
            {
                var ng = _store.MakeGroup(ti.Path, key, DefaultGroupName(), Loc.T("desktop.undo.make_group"));
                _mergeTargetKey = "group:" + ng.Id; // летящий значок теперь следует за новой группой
                _holdKey = _mergeTargetKey;
                var at = cur.FindIndex(k => string.Equals(k, ti.Path, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) cur[at] = "group:" + ng.Id;
                else cur.Add("group:" + ng.Id);
            }
            _store.SetOrderSilently(cur);
            RebuildTrain();
            return;
        }
        if (drag.Reordered)
        {
            if (drag.Source == _groupTiles && _openGroupId is not null)
                _store.MoveInGroup(_openGroupId, key, _groupTiles.IndexOf(drag.Vm), Loc.T("desktop.undo.order"));
            else
            {
                _store.SetOrder(_tiles.Where(x => x.Kind is TileVm.Kinds.Item or TileVm.Kinds.Group).Select(x => x.Key),
                    Loc.T("desktop.undo.order"));
                // плитки на экране уже стоят (едут) в новом порядке — пересборка только оборвала бы их ход и посадку призрака
                if (drag.Detached) Rebuild();
                return;
            }
            Rebuild();
        }
    }

    private bool IsInside(FrameworkElement el, Point pos)
    {
        if (!el.IsVisible) return false;
        var p = TranslatePoint(pos, el);
        return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
    }

    /// <summary>«Упорядочить»: снова по алфавиту, как в Проводнике.</summary>
    public void AutoSort()
    {
        if (_store.ClearOrder(Loc.T("desktop.undo.sort")))
        {
            Rebuild();
        }
        else ShowToast(Loc.T("desktop.sort.already"));
    }

    // ---------- меню по правой кнопке ----------
    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TileVm vm } fe) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = fe };
        void Add(string key, Action action, bool enabled = true, params object[] args)
        {
            var mi = new MenuItem { Header = Loc.T(key, args), IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        switch (vm.Kind)
        {
            case TileVm.Kinds.Trash:
                var info = vm.Tip ?? Loc.T("desktop.trash");
                menu.Items.Add(new MenuItem { Header = info, IsEnabled = false });
                menu.Items.Add(new Separator());
                Add("desktop.menu.open", OpenTrash);
                Add("desktop.trash.empty_menu", EmptyTrash, vm.Tip != Loc.T("desktop.trash.empty"));
                UpdateTrash();
                break;

            case TileVm.Kinds.Group:
                var g = vm.Group!;
                Add("desktop.menu.ungroup", () =>
                {
                    var members = g.Items.ToList();
                    // первый значок остаётся на месте группы, остальные встают следом вправо в порядке добавления
                    var gk = "group:" + g.Id;
                    _store.Ungroup(g.Id, Loc.T("desktop.undo.ungroup")); // сначала запоминаем «как было» для отмены
                    var cur = _tiles.Where(x => x.Kind is TileVm.Kinds.Item or TileVm.Kinds.Group).Select(x => x.Key).ToList();
                    var at = cur.FindIndex(k => string.Equals(k, gk, StringComparison.OrdinalIgnoreCase));
                    if (at >= 0)
                    {
                        cur.RemoveAll(k => members.Contains(k, StringComparer.OrdinalIgnoreCase));
                        at = Math.Min(at, cur.Count);
                        cur.RemoveAll(k => string.Equals(k, gk, StringComparison.OrdinalIgnoreCase));
                        cur.InsertRange(Math.Min(at, cur.Count), members);
                        _store.SetOrderSilently(cur);
                    }
                    RebuildTrain(members, "group:" + g.Id);
                });
                break;

            case TileVm.Kinds.Hit:
                var path = vm.Path!;
                Add("desktop.menu.open", () => Open(path));
                Add("desktop.menu.location", () => OpenLocation(path), !SystemSearch.IsShellApp(path));
                Add("desktop.menu.to_desktop", () =>
                {
                    if (SystemSearch.AddToDesktop(path, vm.Name) is null) ShowToast(Loc.T("desktop.search.add_failed"));
                });
                break;

            default:
                var item = vm.Item!;
                var custom = _store.CustomName(item.Path) is not null || _store.HasCustomImage(item.Path);
                Add("desktop.menu.open", () => Open(item.Path));
                Add("desktop.menu.location", () => OpenLocation(item.Path));
                menu.Items.Add(new Separator());
                Add("desktop.menu.rename", () => StartRename(item, null));
                Add("desktop.menu.icon_clipboard", () => SetImageFrom(item, ImageFromClipboard()), ClipboardHasImage());
                Add("desktop.menu.icon_file", () => PickImageFile(item));
                Add("desktop.menu.reset", () =>
                {
                    _store.Reset(item.Path, Loc.T("desktop.undo.reset"));
                    Rebuild();
                }, custom);
                menu.Items.Add(new Separator());
                menu.Items.Add(GroupSubmenu(item.Path));
                if (_store.GroupOf(item.Path) is not null)
                {
                    Add("desktop.menu.remove_from_group", () =>
                    {
                        _store.RemoveFromGroup(item.Path, Loc.T("desktop.undo.remove_from_group"));
                        Rebuild();
                    });
                }
                menu.Items.Add(new Separator());
                // удаление — в своём потоке (DeleteItem); значка сразу нет (_deleting), и соседи смыкаются «паровозиком»
                // (как после броска в корзину), а не через полсекунды по слежению. Не вышло — значок возвращается
                Add("desktop.menu.delete", () =>
                {
                    DeleteItem(item, done: ok =>
                    {
                        if (!ok) Reload();
                    });
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Reload(animate: true)); // после закрытия меню
                });
                break;
        }
        menu.IsOpen = true;
    }

    /// <summary>«Добавить в группу ▸»: существующие группы и новая.</summary>
    private MenuItem GroupSubmenu(string path)
    {
        var sub = new MenuItem { Header = Loc.T("desktop.menu.add_to_group") };
        var current = _store.GroupOf(path);
        foreach (var g in _store.Groups)
        {
            var mi = new MenuItem { Header = Loc.T("desktop.menu.to_group", g.Name), IsEnabled = g.Id != current?.Id };
            var group = g;
            mi.Click += (_, _) =>
            {
                _store.AddToGroup(group, path, Loc.T("desktop.undo.add_to_group"));
                Rebuild();
            };
            sub.Items.Add(mi);
        }
        return sub;
    }

    /// <summary>Правая кнопка по пустому месту рабочего стола.</summary>
    private void Background_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_filter.Length > 0) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = MainArea };
        var sort = new MenuItem { Header = Loc.T("overlay.sort") };
        sort.Click += (_, _) => AutoSort();
        menu.Items.Add(sort);
        menu.IsOpen = true;
    }

    /// <summary>Открыть папку и выделить файл. У ярлыка — то, на что он указывает (если оно есть).</summary>
    private void OpenLocation(string itemPath)
    {
        var target = IconLoader.ShortcutTarget(itemPath);
        var path = target is not null && (File.Exists(target) || Directory.Exists(target)) ? target : itemPath;
        LaunchRequested?.Invoke();
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")); }
        catch (Exception ex) { Log.Warn($"desktop: не открыть расположение {path}: {ex.Message}"); }
    }

    // ---------- смена значка ----------
    private static bool ClipboardHasImage()
    {
        try { return Clipboard.ContainsImage() || Clipboard.ContainsFileDropList(); }
        catch { return false; }
    }

    private static BitmapSource? ImageFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage()) return Clipboard.GetImage();
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files)
                return files[0] is { } f ? IconStore.ImageFromFile(f) : null;
        }
        catch (Exception ex) { Log.Warn($"desktop: буфер обмена: {ex.Message}"); }
        return null;
    }

    private void PickImageFile(DesktopItem item)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("desktop.pick.title"),
            Filter = Loc.T("desktop.pick.filter"),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            SetImageFrom(item, IconStore.ImageFromFile(dlg.FileName));
    }

    private void SetImageFrom(DesktopItem item, BitmapSource? image)
    {
        if (image is null)
        {
            ShowToast(Loc.T("desktop.icon.bad"));
            return;
        }
        try
        {
            _store.SetImage(item.Path, image, Loc.T("desktop.undo.icon"));
            Rebuild();
        }
        catch (Exception ex)
        {
            Log.Error("desktop: не сохранить значок", ex);
            ShowToast(Loc.T("desktop.icon.bad"));
        }
    }

    // ---------- переименование ----------
    private void StartRename(DesktopItem? item, IconGroup? group)
    {
        _renamePath = item?.Path;
        _renameGroupId = group?.Id;
        RenameTitle.Text = Loc.T("desktop.rename.title", item is not null ? item.Name : group!.Name);
        RenameBox.Text = item is not null ? NameFor(item) : group!.Name;
        ShowLayer(RenameLayer, RenameCard);
        Dispatcher.InvokeAsync(() =>
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void FinishRename(bool ok)
    {
        HideLayer(RenameLayer);
        if (!ok) return;
        var text = RenameBox.Text.Trim();
        if (_renamePath is not null)
        {
            var item = ItemByPath(_renamePath);
            // пустое имя или такое же, как в Проводнике, — убираем своё имя
            var custom = text.Length == 0 || (item is not null && text == item.Name) ? null : text;
            if (custom == _store.CustomName(_renamePath)) return;
            _store.Rename(_renamePath, custom, Loc.T("desktop.undo.rename"));
        }
        else if (_renameGroupId is not null && text.Length > 0)
        {
            _store.RenameGroup(_renameGroupId, text, Loc.T("desktop.undo.rename_group"));
            if (_openGroupId == _renameGroupId) GroupName.Text = text;
        }
        Rebuild();
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) FinishRename(true);
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FinishRename(false);
        }
    }

    private void RenameOk_Click(object sender, RoutedEventArgs e) => FinishRename(true);
    private void RenameCancel_Click(object sender, RoutedEventArgs e) => FinishRename(false);
    private void RenameBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => FinishRename(false);

    // ---------- отмена и подсказки ----------
    // Плашки «Сделано: …» после действий на рабочем столе и «Отменено/Возвращено» убраны по просьбе пользователя
    // (отмена — Ctrl+Z / Ctrl+Shift+Z); плашка осталась только для ошибок и «уже по алфавиту».

    private int _toastToken;

    /// <summary>Плашка плавно появляется и так же плавно гаснет (повторный показ посреди угасания подхватывает с текущей прозрачности).</summary>
    private void FadeToast(bool show)
    {
        var token = ++_toastToken;
        if (show && UndoToast.Visibility != Visibility.Visible)
        {
            UndoToast.Opacity = 0;
            UndoToast.Visibility = Visibility.Visible;
            DesktopPanel.UI.Theme.Fx.PopIn(UndoToast, rise: -6); // снизу — чуть поднимается
        }
        Anim.To(UndoToast, OpacityProperty, show ? 1 : 0, show ? 180 : 320, Motion.EaseOut(), show ? null : () =>
        {
            if (token == _toastToken) UndoToast.Visibility = Visibility.Collapsed; // за это время плашку не показали заново
        });
    }
    private void ShowToast(string text, bool withUndo = false)
    {
        UndoText.Text = text;
        UndoButton.Visibility = withUndo && _store.CanUndo ? Visibility.Visible : Visibility.Collapsed;
        FadeToast(true);
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    /// <summary>Ctrl+Z: отменить последнее изменение значков. false — отменять нечего.</summary>
    public bool Undo(bool redo = false)
    {
        // небольшая пауза между отменами, чтобы не ломать анимацию, если Ctrl+Z зажат или нажимают очень часто
        if ((DateTime.Now - _lastUndo).TotalMilliseconds < 150) return true;
        _lastUndo = DateTime.Now;
        // откуда значки взялись: кто был в какой группе до отмены (вылетят из неё, как при ручном расформировании)
        var origin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _store.Groups)
            foreach (var p in g.Items) origin[p] = "group:" + g.Id;
        if (!(redo ? _store.Redo() : _store.Undo())) return false;
        if (_filter.Length > 0) Rebuild();
        else RebuildTrain(popNew: true, origin: origin, flyIn: true);
        if (_openGroupId is not null && _store.Groups.All(g => g.Id != _openGroupId)) CloseGroup();
        else if (_openGroupId is not null) GroupName.Text = _store.Groups.First(g => g.Id == _openGroupId).Name;
        return true;
    }

    private DateTime _lastUndo = DateTime.MinValue;

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
}
