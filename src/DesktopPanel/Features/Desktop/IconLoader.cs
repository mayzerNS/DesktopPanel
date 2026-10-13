using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.Desktop;

/// <summary>
/// Значки в хорошем качестве и без стрелочки ярлыка.
/// Порядок: у ярлыка берём значок того, на что он указывает (или его собственный значок из exe/dll/ico);
/// если не вышло — просим Проводник (IShellItemImageFactory, как в его «огромных значках»).
/// Маленький значок посреди большого пустого холста обрезаем, чтобы он не выглядел крошечным.
/// Работает в отдельном потоке (STA — так любят расширения Проводника), готовые картинки заморожены.
/// </summary>
public sealed class IconLoader : IDisposable
{
    private readonly BlockingCollection<(string Path, int Size, Action<ImageSource?> Done)> _queue = new();
    private readonly ConcurrentDictionary<(string, int), ImageSource?> _cache = new();
    private readonly Thread _thread;

    public IconLoader()
    {
        _thread = new Thread(Work) { IsBackground = true, Name = "IconLoader" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Поставить значок в очередь. done вызывается в фоновом потоке.</summary>
    public void Request(string path, int sizePx, Action<ImageSource?> done)
    {
        if (_cache.TryGetValue((path.ToLowerInvariant(), sizePx), out var cached))
        {
            done(cached);
            return;
        }
        _queue.Add((path, sizePx, done));
    }

    private void Work()
    {
        foreach (var (path, size, done) in _queue.GetConsumingEnumerable())
        {
            ImageSource? img = null;
            try { img = Load(path, size); }
            catch (Exception ex) { Log.Warn($"icon: {System.IO.Path.GetFileName(path)}: {ex.Message}"); }
            if (img is not null) _cache[(path.ToLowerInvariant(), size)] = img; // неудачу не запоминаем — в следующий раз попробуем снова
            try { done(img); }
            catch (Exception ex) { Log.Warn($"icon: обработчик значка упал: {ex.Message}"); } // иначе поток значков умер бы насовсем
        }
    }

    private static ImageSource? Load(string path, int size)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        BitmapSource? bmp = null;

        if (ext == ".lnk" && ResolveShortcut(path) is { } lnk)
        {
            if (lnk.IconFile is { } iconFile)
                bmp = FromIconFileBest(iconFile, lnk.IconIndex, size);
            // значок exe берём из него самого: Проводник маленькую иконку exe рисует в белой рамке на большом холсте
            if (bmp is null && lnk.Target is { } t && File.Exists(t) && IsIconFile(t))
                bmp = FromIconFileBest(t, 0, size);
            if (bmp is null && lnk.Target is { } target && (File.Exists(target) || Directory.Exists(target)))
                bmp = FromShell(target, size);
        }
        else if (ext == ".url" && ReadUrlIcon(path) is { } url)
        {
            bmp = FromIconFileBest(url.File, url.Index, size);
        }
        else if (IsIconFile(path) && File.Exists(path))
        {
            bmp = FromIconFileBest(path, 0, size);
        }

        bmp ??= FromShell(path, size);
        if (bmp is null) return null;
        bmp = CropEmptyMargins(bmp);
        bmp.Freeze();
        return bmp;
    }

    // ---------- откуда брать значок ----------
    /// <summary>Куда указывает ярлык .lnk (null — не ярлык или цель не прочитать).</summary>
    public static string? ShortcutTarget(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        try { return ResolveShortcut(path)?.Target; }
        catch { return null; }
    }

    /// <summary>Значок любого файла прямо сейчас (в текущем потоке; для «сменить значок» из exe/dll).</summary>
    public static ImageSource? LoadNow(string path, int size)
    {
        try { return Load(path, size); }
        catch { return null; }
    }

    private sealed record ShortcutInfo(string? Target, string? IconFile, int IconIndex);

    private static ShortcutInfo? ResolveShortcut(string lnkPath)
    {
        object? obj = null;
        try
        {
            obj = new NativeMethods.ShellLink();
            ((NativeMethods.IPersistFile)obj).Load(lnkPath, 0);
            var link = (NativeMethods.IShellLinkW)obj;

            var sb = new StringBuilder(1024);
            link.GetIconLocation(sb, sb.Capacity, out var index);
            var iconFile = Expand(sb.ToString());

            sb.Clear();
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = Expand(sb.ToString());

            return new ShortcutInfo(target, iconFile is not null && File.Exists(iconFile) ? iconFile : null, index);
        }
        catch (Exception ex)
        {
            Log.Warn($"icon: ярлык {System.IO.Path.GetFileName(lnkPath)} не читается: {ex.Message}");
            return null;
        }
        finally
        {
            if (obj is not null) Marshal.ReleaseComObject(obj);
        }
    }

    private static (string File, int Index)? ReadUrlIcon(string urlPath)
    {
        string? file = null;
        var index = 0;
        foreach (var line in File.ReadLines(urlPath))
        {
            if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) file = Expand(line[9..]);
            else if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[10..], out index);
        }
        return file is not null && File.Exists(file) ? (file, index) : null;
    }

    private static string? Expand(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));

    private static bool IsIconFile(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" or ".ico";

    /// <summary>
    /// Значок из файла, но не больше, чем в нём есть: маленькую иконку (у Steam-ярлыков часто 32×32) система
    /// растягивает «лесенкой», а WPF при показе увеличит её со сглаживанием.
    /// </summary>
    private static BitmapSource? FromIconFileBest(string file, int index, int size)
    {
        var native = IconSizes.Largest(file, index);
        var px = native is > 0 && native < size ? native.Value : size;
        return FromIconFile(file, index, px);
    }

    // ---------- добыча картинки ----------
    /// <summary>Значок нужного размера прямо из exe/dll/ico — система сама выберет лучший из имеющихся.</summary>
    private static BitmapSource? FromIconFile(string file, int index, int size)
    {
        var icons = new IntPtr[1];
        var ids = new uint[1];
        var n = NativeMethods.PrivateExtractIcons(file, index, size, size, icons, ids, 1, 0);
        if (n == 0 || n == 0xFFFFFFFF || icons[0] == IntPtr.Zero) return null;
        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            NativeMethods.DestroyIcon(icons[0]);
        }
    }

    /// <summary>Как значок показывает Проводник (для файлов, папок, Store-приложений; у картинок — миниатюра).</summary>
    private static BitmapSource? FromShell(string path, int size)
    {
        var iid = typeof(NativeMethods.IShellItemImageFactory).GUID;
        NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        try
        {
            var hr = factory.GetImage(new NativeMethods.SIZE { cx = size, cy = size }, NativeMethods.SIIGBF_BIGGERSIZEOK,
                out var hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            try { return HBitmapToSource(hbm); }
            finally { NativeMethods.DeleteObject(hbm); }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>HBITMAP с прозрачностью → BitmapSource (стандартное преобразование WPF прозрачность теряет).</summary>
    private static BitmapSource? HBitmapToSource(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var info) == 0) return null;
        var w = info.bmWidth;
        var h = Math.Abs(info.bmHeight);
        if (w <= 0 || h <= 0) return null;
        var data = new byte[w * h * 4];
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            var bi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
            };
            if (GetDIBits(hdc, hbm, 0, (uint)h, data, ref bi, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        // некоторые картинки приходят без альфы (все нули) — тогда считаем их непрозрачными
        var anyAlpha = false;
        for (var i = 3; i < data.Length; i += 4)
            if (data[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha)
            for (var i = 3; i < data.Length; i += 4) data[i] = 255;

        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, data, w * 4);
    }

    /// <summary>Если значок занимает меньше половины холста — вырезаем квадрат вокруг него.</summary>
    private static BitmapSource CropEmptyMargins(BitmapSource src)
    {
        var w = src.PixelWidth;
        var h = src.PixelHeight;
        if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32)
            src = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (px[(y * w + x) * 4 + 3] == 0) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        if (maxX < 0) return src;
        int bw = maxX - minX + 1, bh = maxY - minY + 1;
        if (bw >= w / 2 || bh >= h / 2) return src;

        var side = Math.Min(Math.Max(bw, bh) + 4, Math.Min(w, h));
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        var x0 = Math.Clamp(cx - side / 2, 0, w - side);
        var y0 = Math.Clamp(cy - side / 2, 0, h - side);
        return new CroppedBitmap(src, new Int32Rect(x0, y0, side, side));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits,
        ref BITMAPINFOHEADER bi, uint usage);

    public void Dispose() => _queue.CompleteAdding();
}
