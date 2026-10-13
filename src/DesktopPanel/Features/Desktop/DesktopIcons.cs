using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.Desktop;

/// <summary>Показ и скрытие значков на самом рабочем столе — та же команда, что «Вид → Отображать значки рабочего стола».</summary>
public static class DesktopIcons
{
    private const int ToggleIconsCommand = 0x7402;

    private static IntPtr _defView;

    /// <summary>
    /// Окно со значками рабочего стола (SHELLDLL_DefView). Оно живёт либо в Progman, либо в одном из WorkerW (обои
    /// со слайд-шоу, Wallpaper Engine) — тогда поиск перебирает все окна системы. Шапка панели спрашивает это при каждом
    /// обновлении, поэтому окно запоминаем: оно не пересоздаётся, а только переезжает между Progman и WorkerW;
    /// перезапустили Проводник — ручка умерла, ищем заново.
    /// </summary>
    private static IntPtr FindDefView()
    {
        if (_defView != IntPtr.Zero && NativeMethods.IsWindow(_defView) && NativeMethods.ClassName(_defView) == "SHELLDLL_DefView")
            return _defView;
        return _defView = SearchDefView();
    }

    private static IntPtr SearchDefView()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        var defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero) return defView;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var dv = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv == IntPtr.Zero) return true;
            defView = dv;
            return false;
        }, IntPtr.Zero);
        return defView;
    }

    public static bool AreVisible()
    {
        var defView = FindDefView();
        if (defView == IntPtr.Zero) return true;
        var list = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
        return list == IntPtr.Zero || NativeMethods.IsWindowVisible(list);
    }

    public static void Toggle()
    {
        var defView = FindDefView();
        if (defView == IntPtr.Zero)
        {
            Log.Warn("desktop: окно значков рабочего стола не найдено");
            return;
        }
        NativeMethods.SendMessage(defView, NativeMethods.WM_COMMAND, new IntPtr(ToggleIconsCommand), IntPtr.Zero);
    }

    /// <summary>Сделать значки видимыми (при выходе и при откате после сбоя).</summary>
    public static void EnsureVisible()
    {
        if (!AreVisible()) Toggle();
    }
}
