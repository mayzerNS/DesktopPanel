using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Features.Desktop;

/// <summary>Один объект в корзине.</summary>
/// <param name="Path">Где он лежит в служебной папке корзины ($Recycle.Bin\…\$R…) — по этому пути его находим.</param>
public sealed record TrashEntry(string Path, string Name, string Origin, string Deleted, DateTime? DeletedAt, long Size, bool IsFolder);

/// <summary>
/// Содержимое корзины: список, восстановление, удаление насовсем. Работает через Shell.Application (то же, что окно корзины
/// в Проводнике); все вызовы — в отдельном STA-потоке. Удаление насовсем стирает файлы корзины сами ($R и $I) —
/// без запросов Windows (программа работает от администратора).
/// </summary>
public static class TrashBin
{
    private const int SsfBitBucket = 10;

    public static List<TrashEntry> List() => Sta(() =>
    {
        var res = new List<TrashEntry>();
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            dynamic bin = ((dynamic)shell!).NameSpace(SsfBitBucket);
            dynamic items = bin.Items();
            int n = items.Count;
            for (var i = 0; i < n; i++)
            {
                dynamic it = items.Item(i);
                try
                {
                    string path = it.Path;
                    string name = it.Name;
                    bool folder = it.IsFolder;
                    string origin = Clean((string?)bin.GetDetailsOf(it, 1));
                    string deleted = Clean((string?)bin.GetDetailsOf(it, 2));
                    long size = 0;
                    try { size = Convert.ToInt64(it.Size); } catch { }
                    if (size <= 0 && folder) size = DirSize(path);
                    DateTime? at = DateTime.TryParse(deleted, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d) ? d : null;
                    res.Add(new TrashEntry(path, name, origin, deleted, at, size, folder));
                }
                // элементы не освобождаем вручную (ReleaseComObject): оболочка отдаёт один и тот же объект разным вызовам,
                // и освобождённый в одном месте ломался в другом («COM object … separated from its underlying RCW»)
                catch (Exception ex) { Log.Warn($"trash: элемент не прочитан: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"trash: список не получен: {ex.Message}"); }
        finally { if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
        return res;
    });

    /// <summary>
    /// Вернуть объекты на прежние места. Возвращает, сколько вернули и сколько пропустили: на прежнем месте уже лежит
    /// файл или папка с тем же именем (например, удалили файл, а потом положили туда такой же). Такие не трогаем —
    /// иначе Windows сама спрашивала «Заменить или пропустить» со служебным именем корзины ($R…) вместо настоящего,
    /// и восстановление стояло, пока не ответят.
    /// </summary>
    public static (int Done, int Skipped) Restore(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return Sta(() =>
        {
            int done = 0, skipped = 0;
            object? shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                dynamic items = ((dynamic)shell!).NameSpace(SsfBitBucket).Items();
                int n = items.Count;
                for (var i = 0; i < n; i++)
                {
                    dynamic it = items.Item(i);
                    try
                    {
                        string path = it.Path;
                        if (!set.Contains(path)) continue;
                        if (OriginalPath(path) is { } orig && (File.Exists(orig) || Directory.Exists(orig)))
                        {
                            skipped++;
                            Log.Info($"trash: не восстанавливаю {orig} — на этом месте уже есть объект с таким именем");
                            continue;
                        }
                        it.InvokeVerb("undelete");
                        done++;
                    }
                    catch (Exception ex) { Log.Warn($"trash: не восстановить: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log.Warn($"trash: восстановление не удалось: {ex.Message}"); }
            finally { if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
            Log.Info($"trash: восстановлено {done}, пропущено {skipped}");
            return (done, skipped);
        });
    }

    /// <summary>
    /// Прежний полный путь объекта корзины — из служебного файла $I рядом с ним ($R1234.txt → $I1234.txt). Формат $I:
    /// версия (8 байт), размер (8), время удаления (8), дальше путь в UTF-16: у версии 2 (Windows 10+) — длина (4 байта)
    /// и строка, у версии 1 — строка на 260 символов. null — не прочитать.
    /// </summary>
    public static string? OriginalPath(string recycledPath)
    {
        try
        {
            var name = System.IO.Path.GetFileName(recycledPath);
            if (!name.StartsWith("$R", StringComparison.OrdinalIgnoreCase)) return null;
            var info = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(recycledPath)!, "$I" + name[2..]);
            var b = File.ReadAllBytes(info);
            if (b.Length < 26) return null;
            string s;
            if (BitConverter.ToInt64(b, 0) == 2 && b.Length >= 28)
            {
                var len = BitConverter.ToInt32(b, 24);
                s = Encoding.Unicode.GetString(b, 28, Math.Clamp(len * 2, 0, b.Length - 28));
            }
            else s = Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
            var z = s.IndexOf('\0');
            if (z >= 0) s = s[..z];
            return s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    /// <summary>Стереть объекты из корзины насовсем. Возвращает, сколько получилось.</summary>
    public static int DeleteForever(IEnumerable<string> paths)
    {
        var done = 0;
        foreach (var path in paths)
        {
            try
            {
                Strip(path);
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
                // рядом лежит служебный файл с прежним путём и датой: $R1234.txt → $I1234.txt
                var name = System.IO.Path.GetFileName(path);
                if (name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
                {
                    var info = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "$I" + name[2..]);
                    if (File.Exists(info)) { Strip(info); File.Delete(info); }
                }
                done++;
            }
            catch (Exception ex) { Log.Warn($"trash: не стереть {path}: {ex.Message}"); }
        }
        Log.Info($"trash: стёрто насовсем {done}");
        return done;
    }

    /// <summary>Снять «только чтение» (у папок — со всего содержимого), иначе удаление упрётся в доступ.</summary>
    private static void Strip(string path)
    {
        try
        {
            if (Directory.Exists(path))
                foreach (var f in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch { }
    }

    private static long DirSize(string path)
    {
        try
        {
            long sum = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)) sum += f.Length;
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>В подписях Проводника попадаются невидимые метки направления текста.</summary>
    private static string Clean(string? s) => (s ?? "").Replace("‎", "").Replace("‏", "").Trim();

    // Все вызовы Shell.Application — в одном постоянном STA-потоке, строго по очереди. Раньше на каждый вызов был свой
    // поток, и два чтения списка шли одновременно (подтверждение «Удалить насовсем» открывает страницу корзины — чтение,
    // и тут же конец удаления — ещё одно): оболочка отдаёт один и тот же объект, и поток, закончивший первым, освобождал
    // его, пока второй ещё читал («COM object … released while it was still in use on another thread» — список пустой).
    // ⚠ не возвращать поток на вызов и ReleaseComObject для объектов оболочки
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> Jobs = new();
    private static readonly object WorkerGate = new();
    private static Thread? _worker;

    private static T Sta<T>(Func<T> work)
    {
        lock (WorkerGate)
        {
            if (_worker is null)
            {
                _worker = new Thread(() =>
                {
                    foreach (var job in Jobs.GetConsumingEnumerable()) job();
                }) { IsBackground = true, Name = "TrashBin" };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
        }
        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        Jobs.Add(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; } // поток очереди не должен умереть
            finally { done.Set(); }
        });
        done.Wait();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return result;
    }
}
