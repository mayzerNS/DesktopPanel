using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DesktopPanel.Core.Native;

namespace DesktopPanel.UI.Theme;

/// <summary>
/// Окно нельзя утащить или растянуть за край экрана: пока его двигают (WM_MOVING) или тянут за край (WM_SIZING),
/// прямоугольник поджимается к рабочей области монитора, на котором находится середина окна.
/// </summary>
public static class ScreenClamp
{
    private const int WM_SIZING = 0x0214, WM_MOVING = 0x0216;

    public static void Attach(Window window)
    {
        void Hook()
        {
            var src = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            src?.AddHook(WndProc);
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Hook();
        else window.SourceInitialized += (_, _) => Hook();
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOVING && msg != WM_SIZING) return IntPtr.Zero;
        var r = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
        var center = new NativeMethods.POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 };
        var mon = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;
        var wa = mi.rcWork;

        if (msg == WM_MOVING)
        {
            int w = r.Width, h = r.Height;
            var x = Math.Max(wa.Left, Math.Min(r.Left, wa.Right - w));
            var y = Math.Max(wa.Top, Math.Min(r.Top, wa.Bottom - h));
            r = new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        }
        else
        {
            r.Left = Math.Max(r.Left, wa.Left);
            r.Top = Math.Max(r.Top, wa.Top);
            r.Right = Math.Min(r.Right, wa.Right);
            r.Bottom = Math.Min(r.Bottom, wa.Bottom);
        }
        Marshal.StructureToPtr(r, lParam, false);
        handled = true;
        return new IntPtr(1);
    }
}
