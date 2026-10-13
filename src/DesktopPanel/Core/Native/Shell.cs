using System.Diagnostics;
using System.Globalization;
using System.Text;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Core.Native;

/// <summary>Запуск системных утилит (sc, netsh, ipconfig, taskkill) без окна и с выводом в правильной кодировке.</summary>
public static class Shell
{
    public readonly record struct Result(int ExitCode, string Output)
    {
        public bool Ok => ExitCode == 0;
    }

    private static Encoding? _oem;

    /// <summary>Кодировка консоли (cp866 на русской Windows) — так пишут sc, netsh и другие.</summary>
    public static Encoding Oem
    {
        get
        {
            if (_oem is not null) return _oem;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                _oem = Encoding.UTF8;
            }
            return _oem;
        }
    }

    public static Result Run(string file, params string[] args) => Run(file, 15000, args);

    public static Result Run(string file, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Oem,
                StandardErrorEncoding = Oem,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return new Result(-1, "");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                Log.Warn($"shell: {file} {string.Join(' ', args)} — не уложился в {timeoutMs} мс");
                return new Result(-1, "");
            }
            return new Result(p.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex)
        {
            Log.Warn($"shell: {file}: {ex.Message}");
            return new Result(-1, "");
        }
    }

    /// <summary>Есть ли запущенный процесс с таким именем (без .exe).</summary>
    public static bool IsRunning(string processName)
    {
        var list = Process.GetProcessesByName(processName);
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    }

    /// <summary>Открыть файл, папку или ссылку так, как это сделал бы Проводник.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"shell: не открыть {target}: {ex.Message}"); }
    }

    /// <summary>
    /// Открыть лог: его копию из папки TEMP. Новый Блокнот (из Store) почему-то не открывает файлы из
    /// %APPDATA%\DesktopPanel («Системе не удаётся найти указанный путь»), а копию — открывает. Заодно копия —
    /// снимок на момент открытия: программа и winws спокойно дописывают сам лог.
    /// </summary>
    public static void OpenLog(string file)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "DesktopPanel-logs");
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, Path.GetFileName(file));
            using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = File.Create(copy))
                src.CopyTo(dst);
            Notepad(copy);
        }
        catch (Exception ex) { Log.Warn($"shell: лог не открыть {file}: {ex.Message}"); }
    }

    /// <summary>
    /// Открыть текстовый файл (лог, список) в программе по умолчанию — через Проводник, без прав администратора:
    /// новый Блокнот (приложение из Store) от администратора не запускается («не удаётся найти указанный путь»).
    /// </summary>
    public static void Notepad(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{file}\"") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Warn($"shell: не открыть {file}: {ex.Message}"); }
    }
}
