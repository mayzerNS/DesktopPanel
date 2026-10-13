using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopPanel.Core.Native;

namespace DesktopPanel.UI.Theme;

/// <summary>
/// Фон под окном. Размытие рисует сама Windows на видеокарте — без скриншотов и нагрузки на процессор,
/// и оно не гаснет, когда окно теряет фокус (SetWindowCompositionAttribute).
///
/// Размытие — простое размытие Windows (BlurBehind), без настроек: акрил отставал при движении окон.
/// Ползунок «Затемнение» — тёмный слой поверх (0 — без затемнения, 100 — чёрная панель).
/// Окно должно быть без рамки (WindowStyle=None), без AllowsTransparency и с прозрачным фоном.
/// </summary>
public static class Glass
{
    private static int Build => Environment.OSVersion.Version.Build;

    /// <summary>Windows 11 (скруглённые углы окон).</summary>
    public static bool IsWin11 => Build >= 22000;

    /// <summary>Один раз после создания окна: прозрачный фон, тёмная тема, скруглённые углы, тонкая рамка.</summary>
    public static void Prepare(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;

        var dark = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        if (IsWin11)
        {
            var round = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            // системную рамку убираем (DWMWA_COLOR_NONE): сплошной серый цвет на тёмном фоне не виден.
            // Обводку рисует само окно — полупрозрачной светлой линией, она всегда чуть светлее фона под ней
            // ⚠ значит, у каждого такого окна должна быть своя обводка: WindowFrameBrush, радиус Corners.WindowDip
            uint border = 0xFFFFFFFE;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(uint));
        }
        var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>Включить размытие под окном (можно звать повторно).</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero) SetAccent(hwnd, AccentBlur);
    }

    /// <summary>
    /// Размытие под всплывающим окошком (подсказка, меню). Углы скругляем средствами Windows 11 —
    /// иначе размытый прямоугольник выглядывал бы из-под скруглённой подложки.
    /// </summary>
    public static void BlurBehind(IntPtr hwnd)
    {
        if (IsWin11)
        {
            var round = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            uint none = 0xFFFFFFFE; // без системной рамки
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref none, sizeof(uint));
        }
        SetAccent(hwnd, AccentBlur);
    }

    /// <summary>Тёмный слой: «Затемнение» 0..100 → 0..1.</summary>
    public static double DarkOpacity(int darkness) => Math.Clamp(darkness / 100.0, 0, 1);

    private const int AccentBlur = 3;

    private static void SetAccent(IntPtr hwnd, int state)
    {
        var accent = new NativeMethods.ACCENT_POLICY
        {
            AccentState = state,
            GradientColor = 0,
        };
        var size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new NativeMethods.WINDOWCOMPOSITIONATTRIBDATA { Attribute = 19, Data = ptr, SizeOfData = size };
            NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
