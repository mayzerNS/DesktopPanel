using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopPanel.Core.Native;

namespace DesktopPanel.UI.Overlay;

/// <summary>
/// Уголок — два окна. Снизу само окно панели: только размытие и тень Windows (рисовать в нём нечего). Сверху это окно —
/// прозрачное, с затемнением, рамкой и кнопками (их переносят сюда из шапки панели).
///
/// Зачем так: при сворачивании у правого края левый край окна сдвигается каждый кадр. Windows двигает окно сразу, а картинку
/// WPF присылает кадром позже и в этот кадр рисует старую от левого края — кнопки дрожали. Это окно во время сворачивания
/// стоит на месте и не меняет размер (оно шириной с развёрнутый уголок), сужается только рисунок внутри него — в одном кадре
/// с кнопками. Нижнее окно сужается вслед; в нём нет содержимого, которому дрожать.
/// Невидимая часть окна полностью прозрачна — мышь проходит сквозь неё.
/// </summary>
internal sealed class MiniCorner : Window
{
    private readonly Grid _root = new();
    private readonly Grid _back = new();
    private readonly Border _hit = new() { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) }; // ловит мышь и при «Затемнении» 0
    private readonly Border _frame = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false }; // рамка — WindowFrameBrush, как у панели

    /// <summary>Затемнение (ползунок «Затемнение») — как у панели.</summary>
    public readonly Border Tint = new() { Background = Brushes.Black, IsHitTestVisible = false };

    /// <summary>Сюда кладутся кнопки шапки; поле 6 — как у уголка раньше (рамка 1 + поле 5).</summary>
    public readonly Border Host = new() { Margin = new Thickness(6) };

    /// <summary>Появление/исчезновение — тот же масштаб, что у содержимого панели.</summary>
    public readonly ScaleTransform Scale = new();

    public IntPtr Handle { get; private set; }

    public MiniCorner()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Foreground = Brushes.White;
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);

        _frame.SetResourceReference(Border.BorderBrushProperty, "WindowFrameBrush");
        _back.Children.Add(_hit);
        _back.Children.Add(Tint);
        _root.Children.Add(_back);
        _root.Children.Add(Host);
        _root.Children.Add(_frame); // рамка — поверх кнопок, как у панели
        _root.RenderTransform = Scale;
        _root.RenderTransformOrigin = new Point(0.5, 0.5);
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetExStyle(Handle, NativeMethods.ExStyle(Handle) | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
            // Прозрачное окно WPF рисует видеокартой и потом каждый кадр забирает картинку обратно в память (для Windows) —
            // видеокарта на это время останавливается, и кадров при сворачивании выходило мало. Окно маленькое и простое:
            // процессор нарисует его быстрее, сразу в память, без этой остановки.
            if (HwndSource.FromHwnd(Handle)?.CompositionTarget is { } ct) ct.RenderMode = RenderMode.SoftwareOnly;
        };
    }

    /// <summary>Содержимое целиком (прозрачность — вслед за панелью).</summary>
    public FrameworkElement Root => _root;

    /// <summary>Видимая часть: ширина width у края right (иначе у левого) — подложка и рамка; кнопки прижаты к тому же краю.</summary>
    public void SetVisible(double width, bool right)
    {
        var a = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _back.Width = _frame.Width = Math.Max(0, width);
        _back.HorizontalAlignment = _frame.HorizontalAlignment = a;
        if (Host.Child is FrameworkElement c) c.HorizontalAlignment = a;
    }

    /// <summary>Скругление — как у окна под ним (Corners.WindowDip, 8 DIP — растёт с масштабом).</summary>
    public void SetRadius(double r)
    {
        var cr = new CornerRadius(r);
        _hit.CornerRadius = Tint.CornerRadius = _frame.CornerRadius = cr;
    }
}
