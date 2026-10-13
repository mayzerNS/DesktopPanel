using System.Runtime.InteropServices;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.Games;

/// <summary>
/// Игра на весь экран под панелью. Ничего в игре не меняем (в оконный режим не переводим) —
/// только сворачиваем и разворачиваем её окно, если панель иначе не видна (эксклюзивный полноэкранный режим).
/// </summary>
public static class GameWindow
{
    /// <summary>Окно на переднем плане, если оно занимает весь монитор (игра, видео на весь экран). Иначе Zero.</summary>
    public static IntPtr FullscreenForeground(IntPtr own)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == own || fg == NativeMethods.GetShellWindow() || fg == NativeMethods.GetDesktopWindow())
            return IntPtr.Zero;
        var cls = NativeMethods.ClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return IntPtr.Zero;
        if (!NativeMethods.GetWindowRect(fg, out var r)) return IntPtr.Zero;
        var mon = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom ? fg : IntPtr.Zero;
    }

    public static void Minimize(IntPtr game)
    {
        if (game == IntPtr.Zero || !NativeMethods.IsWindow(game)) return;
        Log.Info("games: сворачиваю игру — панель иначе не видна");
        NativeMethods.ShowWindow(game, NativeMethods.SW_MINIMIZE);
    }

    /// <summary>Вернуть игру: развернуть (если её свернули) и отдать ей фокус.</summary>
    public static void Restore(IntPtr game, bool unminimize)
    {
        if (game == IntPtr.Zero || !NativeMethods.IsWindow(game)) return;
        if (unminimize && NativeMethods.IsIconic(game)) NativeMethods.ShowWindow(game, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(game);
    }

    /// <summary>Игры часто «запирают» курсор в своём окне — отпускаем, чтобы им можно было пользоваться в панели.</summary>
    public static void ReleaseCursor() => NativeMethods.ClipCursor(IntPtr.Zero);
}
