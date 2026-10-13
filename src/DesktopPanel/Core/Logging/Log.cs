using System.Diagnostics;
using System.Text;

namespace DesktopPanel.Core.Logging;

/// <summary>Лог в файл %APPDATA%\DesktopPanel\logs\app.log. Старый лог (больше 1 МБ) уходит в app.1.log.</summary>
public static class Log
{
    private const long MaxSize = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Folder => Path.Combine(Paths.AppData, "logs");
    public static string FilePath => Path.Combine(Folder, "app.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    /// <summary>Очистить логи: текущий обнуляется, старый и копии для Блокнота удаляются.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            try { File.WriteAllText(FilePath, ""); } catch { }
            try { File.Delete(Path.Combine(Folder, "app.1.log")); } catch { }
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "DesktopPanel-logs"), true); } catch { }
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        Debug.Write(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxSize)
                {
                    var old = Path.Combine(Folder, "app.1.log");
                    File.Copy(FilePath, old, overwrite: true);
                    File.WriteAllText(FilePath, "");
                }
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
            catch
            {
                // лог не должен ронять программу
            }
        }
    }
}
