using System.Data.OleDb;
using System.Runtime.InteropServices;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.Desktop;

/// <summary>Что нашлось: программа из меню «Пуск» или файл/папка из индекса поиска Windows.</summary>
public sealed record SearchHit(string Name, string Path, bool IsApp);

/// <summary>
/// Поиск по системе, как в «Пуске»: программы (ярлыки меню «Пуск») и файлы с папками из индекса
/// поиска Windows (служба Windows Search, запрос через OLE DB — ничего не перебираем по диску сами).
/// </summary>
public static class SystemSearch
{
    private static List<SearchHit>? _apps;
    private static DateTime _appsAt;
    private static readonly object Gate = new();

    /// <summary>Путь приложения из Microsoft Store: shell:AppsFolder\&lt;AppUserModelID&gt; (открывает Проводник).</summary>
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    public static bool IsShellApp(string path) => path.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Программы меню «Пуск» и приложения из Store (список кэшируется на 5 минут).</summary>
    public static List<SearchHit> Apps()
    {
        lock (Gate)
        {
            if (_apps is not null && DateTime.Now - _appsAt < TimeSpan.FromMinutes(5)) return _apps;
            var list = new List<SearchHit>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // в «Пуске» есть скрытые системные ссылки (Start Menu\Программы) с запретом чтения —
            // без IgnoreInaccessible обход падает на них целиком и список пустой
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                         Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     })
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*.*", opts))
                    {
                        var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                        if (ext is not (".lnk" or ".url" or ".appref-ms")) continue;
                        var name = System.IO.Path.GetFileNameWithoutExtension(f);
                        if (IsJunk(name)) continue;
                        if (seen.Add(name)) list.Add(new SearchHit(name, f, true));
                    }
                }
                catch (Exception ex) { Log.Warn($"search: меню «Пуск» {root}: {ex.Message}"); }
            }
            foreach (var (name, aumid) in StoreApps())
                if (!IsJunk(name) && seen.Add(name)) list.Add(new SearchHit(name, AppsFolderPrefix + aumid, true));
            list.Sort((a, b) => NativeMethods.StrCmpLogicalW(a.Name, b.Name));
            _apps = list;
            _appsAt = DateTime.Now;
            return list;
        }
    }

    /// <summary>Удаление программ, справка и т.п. — не то, что ищут.</summary>
    private static bool IsJunk(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("удал", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Приложения из Microsoft Store (Блокнот, Калькулятор, Параметры…): ярлыков .lnk у них нет,
    /// их видно только в виртуальной папке shell:AppsFolder. У них путь — AppUserModelID с «!».
    /// </summary>
    private static List<(string Name, string Aumid)> StoreApps()
    {
        var res = new List<(string, string)>();
        var t = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type is null) return;
                dynamic shell = Activator.CreateInstance(type)!;
                try
                {
                    dynamic? folder = shell.NameSpace("shell:AppsFolder");
                    if (folder is null) return;
                    foreach (dynamic item in folder.Items())
                    {
                        string name = item.Name;
                        string path = item.Path;
                        if (!string.IsNullOrEmpty(name) && path.Contains('!')) res.Add((name, path));
                    }
                }
                finally { Marshal.FinalReleaseComObject(shell); }
            }
            catch (Exception ex) { Log.Warn($"search: приложения Store: {ex.Message}"); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); // Shell.Application — STA-объект
        t.Start();
        t.Join(TimeSpan.FromSeconds(10));
        return res;
    }

    /// <summary>Программы, в названии которых есть все слова запроса (сначала те, что начинаются с него).</summary>
    public static List<SearchHit> FindApps(string query, int max = 16)
    {
        var words = Words(query);
        if (words.Length == 0) return new();
        return Apps()
            .Where(a => words.All(w => a.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(a => a.Name.StartsWith(words[0], StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .Take(max).ToList();
    }

    /// <summary>Файлы и папки из индекса поиска Windows. Пустой список — ничего или служба поиска выключена.</summary>
    public static List<SearchHit> FindFiles(string query, int max = 40, CancellationToken ct = default)
    {
        var res = new List<SearchHit>();
        var words = Words(query);
        if (words.Length == 0) return res;
        var cond = string.Join(" AND ", words.Select(w => $"\"{w}*\""));
        // System.ItemUrl («file:C:/Users/…») — настоящий путь; ItemPathDisplay показывает русские имена папок
        // («Документы»), которых на диске нет, — по нему файл не открыть и значок не получить
        var sql = $"SELECT TOP {max} System.ItemUrl, System.ItemNameDisplay FROM SystemIndex " +
                  $"WHERE SCOPE='file:' AND CONTAINS(System.FileName, '{cond.Replace("'", "''")}') " +
                  "ORDER BY System.Search.Rank DESC";
        try
        {
            using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            conn.Open();
            using var cmd = new OleDbCommand(sql, conn);
            using var rd = cmd.ExecuteReader();
            while (rd is not null && rd.Read())
            {
                if (ct.IsCancellationRequested) break;
                var path = rd.IsDBNull(0) ? "" : UrlToPath(rd.GetString(0));
                var name = rd.IsDBNull(1) ? System.IO.Path.GetFileName(path) : rd.GetString(1);
                if (path.Length == 0 || path.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase)) continue;
                res.Add(new SearchHit(name, path, false));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"search: индекс Windows недоступен ({ex.Message})");
        }
        return res;
    }

    /// <summary>«file:C:/Users/a/b.docx» → «C:\Users\a\b.docx». Не файл (почта, OneNote…) — пустая строка.</summary>
    private static string UrlToPath(string url)
    {
        if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return "";
        var p = Uri.UnescapeDataString(url[5..]).Replace('/', '\\');
        return p.TrimStart('\\').Length > 1 && p.TrimStart('\\')[1] == ':' ? p.TrimStart('\\') : p;
    }

    /// <summary>Слова запроса без символов, которые ломают запрос к индексу.</summary>
    private static string[] Words(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(c => c is not ('"' or '*' or '\'' or '(' or ')' or '%')).ToArray()))
            .Where(w => w.Length > 0).ToArray();

    /// <summary>
    /// Положить на рабочий стол ярлык на найденное (у ярлыков меню «Пуск» — их копию). Возвращает путь нового ярлыка.
    /// </summary>
    public static string? AddToDesktop(string target, string name)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shellApp = IsShellApp(target);
            var ext = shellApp ? "" : System.IO.Path.GetExtension(target).ToLowerInvariant();
            var baseName = shellApp ? string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars()))
                         : ext is ".lnk" or ".url" ? System.IO.Path.GetFileNameWithoutExtension(target)
                         : System.IO.Path.GetFileName(target);
            var outExt = ext is ".url" ? ".url" : ".lnk";
            var dst = System.IO.Path.Combine(desktop, baseName + outExt);
            for (var i = 2; File.Exists(dst); i++) dst = System.IO.Path.Combine(desktop, $"{baseName} ({i}){outExt}");

            if (ext is ".lnk" or ".url")
            {
                File.Copy(target, dst);
            }
            else
            {
                var link = (NativeMethods.IShellLinkW)new NativeMethods.ShellLink();
                var pidl = IntPtr.Zero;
                try
                {
                    if (shellApp)
                    {
                        // у приложения Store нет файла — ярлык указывает на элемент shell:AppsFolder (как «Пуск» → «На рабочий стол»)
                        Marshal.ThrowExceptionForHR(SHParseDisplayName(target, IntPtr.Zero, out pidl, 0, out _));
                        link.SetIDList(pidl);
                    }
                    else
                    {
                        link.SetPath(target);
                        var dir = System.IO.Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(dir) && File.Exists(target)) link.SetWorkingDirectory(dir);
                    }
                    ((NativeMethods.IPersistFile)link).Save(dst, true);
                }
                finally
                {
                    if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
                    Marshal.FinalReleaseComObject(link);
                }
            }
            Log.Info($"search: ярлык на рабочем столе {dst}");
            return dst;
        }
        catch (Exception ex)
        {
            Log.Warn($"search: не положить на рабочий стол {target}: {ex.Message}");
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
}
