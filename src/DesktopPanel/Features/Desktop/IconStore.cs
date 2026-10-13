using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPanel.Core;
using DesktopPanel.Core.Config;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Features.Desktop;

/// <summary>
/// Свои имена, картинки и группы значков. Файлы на рабочем столе не трогаем: всё хранится в настройках,
/// картинки — в %APPDATA%\DesktopPanel\icons. Каждое изменение можно отменить (Ctrl+Z).
/// </summary>
public sealed class IconStore
{
    private readonly AppConfig _cfg;
    private readonly Action _save;
    private readonly Stack<(string What, string State)> _undo = new();
    private readonly Stack<(string What, string State)> _redo = new(); // отменённое — можно вернуть (Ctrl+Shift+Z)

    public IconStore(AppConfig cfg, Action save)
    {
        _cfg = cfg;
        _save = save;
    }

    public static string Dir => Path.Combine(Paths.AppData, "icons");

    public IReadOnlyList<IconGroup> Groups => _cfg.IconGroups;

    public string? CustomName(string path) => _cfg.IconNames.TryGetValue(path, out var n) ? n : null;

    public bool HasCustomImage(string path) => _cfg.IconImages.ContainsKey(path);

    public IconGroup? GroupOf(string path) =>
        _cfg.IconGroups.FirstOrDefault(g => g.Items.Contains(path, StringComparer.OrdinalIgnoreCase));

    // ---------- отмена ----------
    private sealed record State(Dictionary<string, string> Names, Dictionary<string, string> Images, List<IconGroup> Groups,
        List<string>? Order);

    /// <summary>Запомнить, как было, перед изменением. what — текст для «Отменить: …».</summary>
    private string Snapshot() =>
        JsonSerializer.Serialize(new State(_cfg.IconNames, _cfg.IconImages, _cfg.IconGroups, _cfg.IconOrder));

    private void Remember(string what)
    {
        var json = Snapshot();
        _redo.Clear(); // новое действие — «вперёд» уже нечего
        _undo.Push((what, json));
        if (_undo.Count > 50)
        {
            var keep = _undo.Take(30).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
    }

    public bool CanUndo => _undo.Count > 0;

    public string? LastAction => _undo.Count > 0 ? _undo.Peek().What : null;

    /// <summary>Вернуть как было до последнего изменения.</summary>
    public bool Undo() => Step(_undo, _redo, "отменено");

    /// <summary>Вернуть то, что только что отменили.</summary>
    public bool Redo() => Step(_redo, _undo, "возвращено");

    public bool CanRedo => _redo.Count > 0;

    private bool Step(Stack<(string What, string State)> from, Stack<(string What, string State)> to, string verb)
    {
        if (from.Count == 0) return false;
        var (what, json) = from.Pop();
        var s = JsonSerializer.Deserialize<State>(json);
        if (s is null) return false;
        to.Push((what, Snapshot())); // как было до этого шага — чтобы можно было шагнуть обратно
        _cfg.IconNames = s.Names ?? new();
        _cfg.IconImages = s.Images ?? new();
        _cfg.IconGroups = s.Groups ?? new();
        _cfg.IconOrder = s.Order ?? new();
        _save();
        Log.Info($"icons: {verb} «{what}»");
        return true;
    }

    // ---------- порядок ----------
    /// <summary>Свой порядок плиток (пусто — по алфавиту).</summary>
    public IReadOnlyList<string> Order => _cfg.IconOrder;

    public void SetOrder(IEnumerable<string> keys, string what)
    {
        var list = keys.ToList();
        if (list.SequenceEqual(_cfg.IconOrder, StringComparer.OrdinalIgnoreCase)) return;
        Remember(what);
        _cfg.IconOrder = list;
        _save();
    }

    /// <summary>Поправить порядок без отдельного шага отмены (часть другого действия).</summary>
    public void SetOrderSilently(List<string> keys)
    {
        _cfg.IconOrder = keys;
        _save();
    }

    /// <summary>«Упорядочить»: вернуть порядок как в Проводнике (по алфавиту, папки первыми). false — и так по алфавиту.</summary>
    public bool ClearOrder(string what)
    {
        if (_cfg.IconOrder.Count == 0) return false;
        Remember(what);
        _cfg.IconOrder = new();
        _save();
        return true;
    }

    /// <summary>Переставить значок внутри группы.</summary>
    public void MoveInGroup(string groupId, string path, int index, string what)
    {
        var g = _cfg.IconGroups.FirstOrDefault(x => x.Id == groupId);
        if (g is null) return;
        var cur = g.Items.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (cur < 0 || cur == index) return;
        Remember(what);
        var item = g.Items[cur];
        g.Items.RemoveAt(cur);
        g.Items.Insert(Math.Clamp(index, 0, g.Items.Count), item);
        _save();
    }

    // ---------- имена ----------
    public void Rename(string path, string? name, string what)
    {
        Remember(what);
        if (string.IsNullOrWhiteSpace(name)) _cfg.IconNames.Remove(path);
        else _cfg.IconNames[path] = name.Trim();
        _save();
    }

    // ---------- картинки ----------
    /// <summary>Сохранить картинку для значка (уменьшаем до 256 px) и назначить её.</summary>
    public void SetImage(string path, BitmapSource image, string what)
    {
        Directory.CreateDirectory(Dir);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant() + DateTime.Now.Ticks)));
        var file = Path.Combine(Dir, hash[..16] + ".png");

        BitmapSource src = image;
        var max = Math.Max(src.PixelWidth, src.PixelHeight);
        if (max > 256)
        {
            var k = 256.0 / max;
            src = new TransformedBitmap(src, new ScaleTransform(k, k));
        }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using (var fs = File.Create(file)) enc.Save(fs);

        Remember(what);
        _cfg.IconImages[path] = Path.GetFileName(file);
        _save();
    }

    /// <summary>Своя картинка значка (null — нет).</summary>
    public ImageSource? CustomImage(string path)
    {
        if (!_cfg.IconImages.TryGetValue(path, out var name)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(Path.Combine(Dir, name));
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"icons: своя картинка не читается ({ex.Message})");
            return null;
        }
    }

    /// <summary>Картинка из файла: png/jpg/bmp/gif/ico/tiff — как есть, exe/dll/ярлык — их значок.</summary>
    public static BitmapSource? ImageFromFile(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        try
        {
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".ico" or ".tif" or ".tiff" or ".webp")
            {
                using var fs = File.OpenRead(file);
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                // в .ico берём самый большой кадр
                var frame = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
                frame.Freeze();
                return frame;
            }
            return IconLoader.LoadNow(file, 256) as BitmapSource;
        }
        catch (Exception ex)
        {
            Log.Warn($"icons: не картинка {file}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Убрать свои имя и картинку (вернуть как в Проводнике).</summary>
    public void Reset(string path, string what)
    {
        Remember(what);
        _cfg.IconNames.Remove(path);
        _cfg.IconImages.Remove(path);
        _save();
    }

    // ---------- группы ----------
    /// <summary>Положить значок dragged на значок target → новая группа из двух.</summary>
    public IconGroup MakeGroup(string target, string dragged, string defaultName, string what)
    {
        Remember(what);
        RemoveFromGroups(dragged);
        RemoveFromGroups(target);
        var g = new IconGroup { Name = defaultName, Items = new List<string> { target, dragged } };
        _cfg.IconGroups.Add(g);
        _save();
        return g;
    }

    public void AddToGroup(IconGroup g, string path, string what)
    {
        if (g.Items.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        Remember(what);
        RemoveFromGroups(path);
        var live = _cfg.IconGroups.FirstOrDefault(x => x.Id == g.Id);
        if (live is null) return;
        live.Items.Add(path);
        _save();
    }

    public void RemoveFromGroup(string path, string what)
    {
        Remember(what);
        RemoveFromGroups(path);
        _save();
    }

    public void RenameGroup(string groupId, string name, string what)
    {
        var g = _cfg.IconGroups.FirstOrDefault(x => x.Id == groupId);
        if (g is null || g.Name == name || string.IsNullOrWhiteSpace(name)) return;
        Remember(what);
        g.Name = name.Trim();
        _save();
    }

    public void Ungroup(string groupId, string what)
    {
        Remember(what);
        _cfg.IconGroups.RemoveAll(g => g.Id == groupId);
        _save();
    }

    /// <summary>Убрать значок из групп. Группа, в которой остался один значок (или ни одного), расформировывается: групп из одного не бывает.</summary>
    private void RemoveFromGroups(string path)
    {
        var emptied = _cfg.IconGroups
            .Where(g => g.Items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0 && g.Items.Count <= 1)
            .ToList();
        _cfg.IconGroups.RemoveAll(emptied.Contains);
    }

    /// <summary>Значки, которых больше нет на рабочем столе, из групп убираем (сами настройки имён оставляем).</summary>
    public void Prune(IReadOnlyCollection<string> existing)
    {
        if (existing.Count == 0) return; // рабочий стол не прочитался — группы не трогаем
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var emptied = new List<IconGroup>();
        foreach (var g in _cfg.IconGroups)
            if (g.Items.RemoveAll(p => !set.Contains(p)) > 0)
            {
                changed = true;
                if (g.Items.Count <= 1) emptied.Add(g); // на рабочем столе осталось меньше двух значков группы
            }
        _cfg.IconGroups.RemoveAll(emptied.Contains);
        if (_cfg.IconGroups.RemoveAll(g => g.Items.Count <= 1) > 0) changed = true; // старые группы из одного значка
        if (changed) _save();
    }
}
