using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Features.Desktop;

/// <summary>Корзина Windows: сколько в ней лежит, удалить в неё файл, очистить, открыть.</summary>
// ⚠ все вызовы — оболочка Windows, она прокачивает сообщения: не звать из кадра анимации (FrameClock) и посреди
// обработки мыши — в фон (Task.Run) или следом (Dispatcher.BeginInvoke(Background)), иначе внутри пройдёт чужая пересборка
public static class RecycleBin
{
    /// <summary>Как корзину понимает Проводник (для значка и открытия).</summary>
    public const string ShellPath = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>(объектов, байт) во всех корзинах всех дисков.</summary>
    public static (long Count, long Bytes) Info()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? (info.i64NumItems, info.i64Size) : (0, 0);
    }

    /// <summary>Отправить файл или папку в корзину (без вопросов Windows). false — не вышло.</summary>
    public static bool Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else if (File.Exists(path))
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else return false;
            Log.Info($"recycle: в корзину {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"recycle: не удалить {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Отправить в корзину в своём STA-потоке: done(сколько удалось) — из того же потока. Удаление через оболочку занимало
    /// поток интерфейса на 80–90 мс (в perf.log — задание после полёта значка в корзину) и прокачивало его сообщения.
    /// </summary>
    public static void DeleteInBackground(IReadOnlyList<string> paths, Action<int> done)
    {
        var t = new Thread(() =>
        {
            var ok = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var p in paths)
                if (Delete(p)) ok++;
            Log.Info($"recycle: в фоне {ok} из {paths.Count} за {sw.ElapsedMilliseconds} мс");
            done(ok);
        }) { IsBackground = true, Name = "RecycleBin" };
        t.SetApartmentState(ApartmentState.STA); // расширения оболочки любят STA
        t.Start();
    }

    /// <summary>Очистить корзину (подтверждение спрашивает сама программа заранее).</summary>
    public static void Empty()
    {
        var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI);
        Log.Info($"recycle: очистка → 0x{hr:X8}");
    }

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2;

    [StructLayout(LayoutKind.Sequential)] // x64: обычное выравнивание (24 байта)
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);
}
