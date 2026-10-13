using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.Pip;

/// <summary>
/// Поле поиска/адреса мини-браузера: открывается кнопкой «Поиск» над или под окном браузера
/// (со стороны, где до края экрана дальше). Enter — открыть, Esc или щелчок мимо — закрыть.
/// </summary>
public sealed class PipSearchBox : Window
{
    private readonly TextBox _box;

    /// <summary>Что ввели (адрес или запрос).</summary>
    public event Action<string>? Submitted;

    public PipSearchBox()
    {
        WindowStyle = WindowStyle.None;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Foreground = Brushes.White;
        UseLayoutRounding = true;

        _box = new TextBox { Height = BoxHeight - 2, Tag = Loc.T("pip.url_hint") }; // −2: кромка всплывашки сверху и снизу
        _box.SetResourceReference(StyleProperty, "InputBox");
        // само поле без своей рамки и фона: всплывашка — одно скруглённое поле на фоне подсказок (рамку вокруг убрали);
        // тонкая светлая кромка — у самой всплывашки, как у панели кнопок браузера рядом и у подсказок (без неё поле
        // единственное из всплывающего было без кромки — на светлой странице под ним края терялись)
        _box.Background = Brushes.Transparent;
        _box.BorderThickness = new Thickness(0);
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                var text = _box.Text.Trim();
                Hide();
                if (text.Length > 0) Submitted?.Invoke(text);
            }
            else if (e.Key == Key.Escape) Hide();
        };
        var card = new Border { CornerRadius = new CornerRadius(DesktopPanel.UI.Theme.Corners.WindowDip), BorderThickness = new Thickness(1), Child = _box };
        card.SetResourceReference(Border.BackgroundProperty, "TipBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "PopupEdgeBrush");
        DesktopPanel.UI.Theme.Fx.SetPopIn(card, true);
        Content = card;
        DesktopPanel.UI.Theme.Tips.Blur(this);
        Deactivated += (_, _) => Hide(); // щёлкнули мимо
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            NativeMethods.SetExStyle(h, NativeMethods.ExStyle(h) | NativeMethods.WS_EX_TOOLWINDOW);
        };
    }

    /// <summary>Показать по координатам экрана (пиксели) с шириной w; text — текущий адрес (выделен).</summary>
    public void Open(int x, int y, int w, string text)
    {
        var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Width = Math.Max(300, w / s);
        Height = BoxHeight;
        _box.Text = text;
        Show();
        var h = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, x, y, (int)(Width * s), (int)(Height * s), 0);
        Activate();
        NativeMethods.SetForegroundWindow(h);
        _box.Focus();
        Keyboard.Focus(_box);
        _box.SelectAll();
    }

    /// <summary>Высота в пикселях экрана (до показа — по расчёту).</summary>
    public int PixelHeight => (int)(BoxHeight * VisualTreeHelper.GetDpi(this).DpiScaleX);

    private const double BoxHeight = 34;
}
