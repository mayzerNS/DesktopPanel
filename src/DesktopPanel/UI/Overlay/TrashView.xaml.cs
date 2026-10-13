using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Logging;
using DesktopPanel.Features.Desktop;

namespace DesktopPanel.UI.Overlay;

/// <summary>Строка таблицы корзины.</summary>
public sealed class TrashRow : INotifyPropertyChanged
{
    private bool _selected;

    public TrashRow(TrashEntry entry) => Entry = entry;

    public TrashEntry Entry { get; }
    public string Name => Entry.Name;
    public string Origin => Entry.Origin;
    public string Deleted => Entry.Deleted;
    public string SizeText => Entry.Size > 0 || !Entry.IsFolder ? DesktopView.FormatBytes(Entry.Size) : "";
    public string Glyph => Entry.IsFolder ? "" : "";
    private ImageSource? _icon;
    /// <summary>Настоящий значок файла (приходит из фона); пока его нет, видна заглушка-глиф.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlyphVisibility)));
        }
    }
    public Visibility IconVisibility => _icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Выделена — слой выделения строки проявляется (Fx.Show в разметке).</summary>
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Своя корзина: таблица удалённого (имя, откуда, когда, размер), выделение (щелчок, Ctrl, Shift, Ctrl+A),
/// «Восстановить», «Восстановить всё», «Удалить насовсем», «Очистить». Как диспетчер задач — страница панели.
/// </summary>
public partial class TrashView : UserControl
{
    private static readonly string[] Cols = { "name", "origin", "date", "size" };

    private readonly ObservableCollection<TrashRow> _rows = new();
    private List<TrashEntry> _all = new();
    private readonly Dictionary<string, TextBlock> _headerLabels = new();
    private string _sortCol = "date";
    private bool _sortAsc; // свежее удалённое — сверху
    private string _filter = "";
    private int _anchor = -1;
    private int _loadToken;
    private bool _busy;
    private IconLoader? _loader;
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public void Init(IconLoader loader) => _loader = loader;
    private string _note = ""; // итог последнего действия — показывается в сводке, пока не выделят что-то новое

    public TrashView()
    {
        InitializeComponent();
        RowsList.ItemsSource = _rows;
        BuildHeader();
        PreviewKeyDown += OnKey;
        MouseDown += (_, _) => Focus();
    }

    /// <summary>Страница открылась (или закрылась): при открытии список читаем заново.</summary>
    public void SetActive(bool on)
    {
        if (on) Reload();
    }

    // ---------- загрузка ----------
    public void Reload()
    {
        var token = ++_loadToken;
        if (_all.Count == 0) EmptyText.Text = Loc.T("trash.loading");
        EmptyText.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Task.Run(TrashBin.List).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (token != _loadToken || t.IsFaulted) return;
            _all = t.Result;
            Apply();
        }));
    }

    /// <summary>Фильтр + сортировка → таблица. Выделение сохраняем по пути объекта.</summary>
    private void Apply()
    {
        var keep = _rows.Where(r => r.IsSelected).Select(r => r.Entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<TrashEntry> q = _all;
        if (_filter.Length > 0)
            q = q.Where(e => e.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
                             || e.Origin.Contains(_filter, StringComparison.CurrentCultureIgnoreCase));
        Func<TrashEntry, object> key = _sortCol switch
        {
            "name" => e => e.Name.ToLowerInvariant(),
            "origin" => e => e.Origin.ToLowerInvariant(),
            "size" => e => e.Size,
            _ => e => e.DeletedAt ?? DateTime.MinValue,
        };
        var list = (_sortAsc ? q.OrderBy(key) : q.OrderByDescending(key)).ToList();

        _rows.Clear();
        foreach (var e in list)
        {
            var row = new TrashRow(e) { IsSelected = keep.Contains(e.Path) };
            _rows.Add(row);
            LoadIcon(row);
        }
        _anchor = -1;

        EmptyText.Text = _all.Count == 0 ? Loc.T("trash.none") : _rows.Count == 0 ? Loc.T("trash.nothing_found") : "";
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeader();
        UpdateButtons();
    }

    /// <summary>Значок файла: свой для каждого пути (у ярлыков и программ он разный), грузится в фоне и запоминается.</summary>
    private void LoadIcon(TrashRow row)
    {
        var path = row.Entry.Path;
        if (_iconCache.TryGetValue(path, out var cached))
        {
            row.Icon = cached;
            return;
        }
        _loader?.Request(path, 32, img => Dispatcher.InvokeAsync(() =>
        {
            _iconCache[path] = img;
            row.Icon = img;
        }));
    }

    // ---------- шапка таблицы ----------
    private void BuildHeader()
    {
        for (var i = 0; i < Cols.Length; i++)
        {
            var col = Cols[i];
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var b = new Button
            {
                Content = label,
                HorizontalContentAlignment = HorizontalAlignment.Left, // все колонки — от левого края, как подписи под ними
                Margin = new Thickness(1, 0, 1, 0),
                Height = 36,
                Padding = new Thickness(10, 0, 10, 0),
            };
            b.SetResourceReference(StyleProperty, "PanelButton");
            b.Click += (_, _) => SortBy(col);
            Grid.SetColumn(b, i);
            HeaderGrid.Children.Add(b);
            _headerLabels[col] = label;
        }
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        foreach (var (col, label) in _headerLabels)
            label.Text = Loc.T("trash.col." + col) + (col == _sortCol ? (_sortAsc ? "  ▲" : "  ▼") : "");
        InfoText.Text = _note.Length > 0 && SelectedRows().Count == 0 ? _note : SelectedRows().Count > 0
            ? Loc.T("trash.selected", SelectedRows().Count)
            : _all.Count == 0
                ? Loc.T("desktop.trash.empty")
                : Loc.T("desktop.trash.info", Loc.Plural(_all.Count, "desktop.trash.items"), DesktopView.FormatBytes(_all.Sum(e => e.Size)));
    }

    private void SortBy(string col)
    {
        if (_sortCol == col) _sortAsc = !_sortAsc;
        else
        {
            _sortCol = col;
            _sortAsc = col is "name" or "origin"; // имена — от А до Я; даты и размеры — сначала новые и большие
        }
        Apply();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text.Trim();
        Apply();
    }

    // ---------- выделение ----------
    private List<TrashRow> SelectedRows() => _rows.Where(r => r.IsSelected).ToList();

    private void UpdateButtons()
    {
        var any = _rows.Any(r => r.IsSelected);
        RestoreButton.IsEnabled = any && !_busy;
        DeleteButton.IsEnabled = any && !_busy;
        RestoreAllButton.IsEnabled = _all.Count > 0 && !_busy;
        EmptyButton.IsEnabled = _all.Count > 0 && !_busy;
        UpdateHeader();
    }

    private void Row_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrashRow row) return;
        Focus();
        _note = "";
        var idx = _rows.IndexOf(row);
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift) && _anchor >= 0)
        {
            var (a, b) = (Math.Min(_anchor, idx), Math.Max(_anchor, idx));
            for (var i = 0; i < _rows.Count; i++) _rows[i].IsSelected = i >= a && i <= b || (mods.HasFlag(ModifierKeys.Control) && _rows[i].IsSelected);
        }
        else if (mods.HasFlag(ModifierKeys.Control))
        {
            row.IsSelected = !row.IsSelected;
            _anchor = idx;
        }
        else
        {
            foreach (var r in _rows) r.IsSelected = r == row;
            _anchor = idx;
        }
        UpdateButtons();
    }

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrashRow row) return;
        e.Handled = true;
        if (!row.IsSelected)
        {
            foreach (var r in _rows) r.IsSelected = r == row;
            _anchor = _rows.IndexOf(row);
            UpdateButtons();
        }
        var menu = new ContextMenu();
        var restore = new MenuItem { Header = Loc.T("trash.restore") };
        restore.Click += (_, _) => RestoreSelected();
        var del = new MenuItem { Header = Loc.T("trash.delete") };
        del.Click += (_, _) => DeleteSelected();
        menu.Items.Add(restore);
        menu.Items.Add(del);
        menu.IsOpen = true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            foreach (var r in _rows) r.IsSelected = true;
            UpdateButtons();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Enter) { RestoreSelected(); e.Handled = true; }
    }

    // ---------- действия ----------
    private void Restore_Click(object sender, RoutedEventArgs e) => RestoreSelected();
    private void RestoreAll_Click(object sender, RoutedEventArgs e) => Run(_all.Select(x => x.Path).ToList(), restore: true);
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void RestoreSelected()
    {
        var paths = SelectedRows().Select(r => r.Entry.Path).ToList();
        if (paths.Count > 0) Run(paths, restore: true);
    }

    private void DeleteSelected()
    {
        var paths = SelectedRows().Select(r => r.Entry.Path).ToList();
        if (paths.Count == 0) return;
        App.Instance.Overlay?.Confirm(Loc.T("trash.delete_q"), Loc.T("trash.delete_text", Loc.Plural(paths.Count, "desktop.trash.items")),
            Loc.T("trash.delete"), _ =>
            {
                App.Instance.Overlay?.ShowPage(OverlayWindow.PageTrash);
                Run(paths, restore: false);
            });
    }

    private void Empty_Click(object sender, RoutedEventArgs e)
    {
        if (_all.Count == 0) return;
        App.Instance.Overlay?.Confirm(Loc.T("desktop.trash.empty_q"),
            Loc.T("desktop.trash.empty_text", Loc.Plural(_all.Count, "desktop.trash.items")), Loc.T("desktop.trash.empty_ok"), _ =>
            {
                App.Instance.Overlay?.ShowPage(OverlayWindow.PageTrash);
                _busy = true;
                UpdateButtons();
                Task.Run(RecycleBin.Empty).ContinueWith(_ => Dispatcher.InvokeAsync(Finished));
            });
    }

    /// <summary>Восстановить или стереть насовсем в фоне, потом обновить список.</summary>
    private void Run(List<string> paths, bool restore)
    {
        if (_busy || paths.Count == 0) return;
        _busy = true;
        UpdateButtons();
        Task.Run(() => restore ? TrashBin.Restore(paths) : (TrashBin.DeleteForever(paths), 0)).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            var (done, skipped) = t.IsFaulted ? (0, 0) : t.Result;
            _note = !restore ? ""
                : skipped > 0 ? Loc.T("trash.restored_skipped", done, skipped)
                : Loc.T("trash.restored", done);
            Finished();
        }));
    }

    private void Finished()
    {
        _busy = false;
        Reload();
        App.Instance.Overlay?.DesktopPage.RefreshTrashInfo();
    }

    /// <summary>Переход на страницу: строка с кнопками, шапка таблицы и сама таблица появляются «лесенкой».</summary>
    public void Enter() => DesktopPanel.UI.Theme.Fx.Enter(new FrameworkElement[] { TopBar, HeaderGrid, Scroll });
}
