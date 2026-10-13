namespace DesktopPanel.Core.Logging;

/// <summary>
/// Самоочистка папки логов (%APPDATA%\DesktopPanel\logs): при запуске и раз в 6 часов, в фоновом потоке.
/// — app.log сам переносится в app.1.log при 1 МБ, perf.log — в perf.1.log при 20 МБ (см. Log, PerfLog);
/// — любой другой лог (winws.log, zapret-test.log, отладочные) больше 5 МБ обрезается до последнего мегабайта;
/// — отладочные и старые логи, которые не менялись 3 дня, удаляются (кроме app.log и логов Zapret);
/// — копии логов для Блокнота в %TEMP%\DesktopPanel-logs старше суток удаляются.
/// </summary>
public static class LogJanitor
{
    private const long TrimOver = 5L * 1024 * 1024, KeepTail = 1024 * 1024;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);
    private static readonly HashSet<string> Keep = new(StringComparer.OrdinalIgnoreCase) { "app.log", "winws.log", "zapret-test.log" };
    private static Timer? _timer;

    public static void Start() => _timer ??= new Timer(_ => Run(), null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));

    public static void Run()
    {
        try
        {
            var dir = new DirectoryInfo(Log.Folder);
            if (dir.Exists)
                foreach (var f in dir.GetFiles("*.log"))
                {
                    try
                    {
                        if (!Keep.Contains(f.Name) && DateTime.Now - f.LastWriteTime > MaxAge) { f.Delete(); continue; }
                        if (f.Length > TrimOver && !f.Name.StartsWith("app", StringComparison.OrdinalIgnoreCase)
                                                && !f.Name.StartsWith("perf", StringComparison.OrdinalIgnoreCase))
                            TrimToTail(f.FullName);
                    }
                    catch { /* файл занят (winws пишет) — в следующий раз */ }
                }
            var temp = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "DesktopPanel-logs"));
            if (temp.Exists && DateTime.Now - temp.LastWriteTime > TimeSpan.FromDays(1)) temp.Delete(true);
        }
        catch (Exception ex) { Log.Warn($"logs: очистка не удалась: {ex.Message}"); }
    }

    /// <summary>Оставить в файле последний мегабайт (с начала ближайшей строки).</summary>
    private static void TrimToTail(string path)
    {
        byte[] tail;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(-KeepTail, SeekOrigin.End);
            tail = new byte[KeepTail];
            var read = 0;
            while (read < tail.Length) { var n = fs.Read(tail, read, tail.Length - read); if (n == 0) break; read += n; }
        }
        var start = Array.IndexOf(tail, (byte)'\n') + 1;
        using var w = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        w.Write(tail, start, tail.Length - start);
        Log.Info($"logs: {Path.GetFileName(path)} обрезан до последнего мегабайта");
    }
}
