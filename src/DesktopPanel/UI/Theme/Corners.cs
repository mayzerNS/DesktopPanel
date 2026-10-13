namespace DesktopPanel.UI.Theme;

/// <summary>
/// Скругление, как у окон Windows 11. Windows скругляет окна (DWMWCP_ROUND) на 8 «эффективных пикселей» — DIP: при
/// масштабе 125 % это 10 пикселей экрана, при 150 % — 12. Всё, что рисуется по краю такого окна (рамка, подложка,
/// слой страницы), — с этим же радиусом в DIP, иначе Windows «прикусывает» рамку на углах (или под ней виден фон).
/// </summary>
public static class Corners
{
    public const double WindowDip = 8;
}
