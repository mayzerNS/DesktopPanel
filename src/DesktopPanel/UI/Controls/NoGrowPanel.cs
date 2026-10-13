using System.Windows;
using System.Windows.Controls;

namespace DesktopPanel.UI.Controls;

/// <summary>
/// Место, которое не просит себе ширины: детей раскладывает по тому месту, что ему дали, а родителю говорит «мне
/// ширина не нужна». Так содержимое (поле поиска в шапке, которое на ходу меняет ширину) никогда не раздвигает
/// соседей — даже на долю пикселя от округления: правый край шапки и кнопки на нём стоят жёстко.
/// </summary>
public sealed class NoGrowPanel : Panel
{
    // ⚠ ширина 0 в Measure — нарочно: иначе меняющаяся на ходу ширина содержимого раздвигает шапку и толкает кнопки
    protected override Size MeasureOverride(Size availableSize)
    {
        double h = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(availableSize);
            h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(0, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement c in InternalChildren) c.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
