using System.Runtime.InteropServices;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.Desktop;

/// <summary>Что лежит на рабочем столе (свой и общий для всех пользователей) + слежение за изменениями.</summary>
public sealed class DesktopScanner : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Threading.Timer _debounce;

    /// <summary>Что-то на рабочем столе изменилось (вызывается в фоновом потоке, не чаще раза в 0,5 с).</summary>
    public event Action? Changed;

    public DesktopScanner()
    {
        _debounce = new System.Threading.Timer(_ => Changed?.Invoke());
        foreach (var dir in Folders())
        {
            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
                w.Created += OnFsEvent;
                w.Deleted += OnFsEvent;
                w.Renamed += OnFsEvent;
                w.Changed += OnFsEvent;
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Log.Warn($"desktop: не слежу за {dir}: {ex.Message}");
            }
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) =>
        _debounce.Change(500, Timeout.Infinite);

    public static IEnumerable<string> Folders()
    {
        var own = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (Directory.Exists(own)) yield return own;
        if (Directory.Exists(common) && !string.Equals(own, common, StringComparison.OrdinalIgnoreCase))
            yield return common;
    }

    /// <summary>Список ярлыков: сначала папки, потом остальное, по имени «как в Проводнике» (2 раньше 10).</summary>
    public static List<DesktopItem> Scan()
    {
        var items = new List<DesktopItem>();
        foreach (var dir in Folders())
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch (Exception ex)
            {
                Log.Warn($"desktop: {dir}: {ex.Message}");
                continue;
            }

            foreach (var path in entries)
            {
                try
                {
                    var attr = File.GetAttributes(path);
                    if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System)) continue;
                    if (string.Equals(System.IO.Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var isFolder = attr.HasFlag(FileAttributes.Directory);
                    items.Add(new DesktopItem(path, DisplayName(path), isFolder));
                }
                catch (Exception ex)
                {
                    Log.Warn($"desktop: пропускаю {path}: {ex.Message}");
                }
            }
        }

        items.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            return NativeMethods.StrCmpLogicalW(a.Name, b.Name);
        });
        return items;
    }

    /// <summary>Имя, как в Проводнике: без .lnk/.url, а расширения файлов — по настройке Windows.</summary>
    private static string DisplayName(string path)
    {
        var info = new NativeMethods.SHFILEINFO();
        var ok = NativeMethods.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), NativeMethods.SHGFI_DISPLAYNAME);
        return ok != IntPtr.Zero && !string.IsNullOrEmpty(info.szDisplayName)
            ? info.szDisplayName
            : System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _debounce.Dispose();
    }
}
