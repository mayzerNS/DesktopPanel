using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DesktopPanel.UI.Controls;

/// <summary>
/// Кнопка шапки: значок + подпись (Compact — только значок, подпись уходит в подсказку).
/// IsChecked («включено», подсветка цветом акцента) меняет только программа, а не щелчок.
/// </summary>
public sealed class IconButton : ToggleButton
{
    private readonly TextBlock _glyph = new()
    {
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private bool _compact;
    private object? _extraTip;

    public IconButton()
    {
        SetResourceReference(StyleProperty, "PanelToggle");
        _glyph.SetResourceReference(TextBlock.FontFamilyProperty, "GlyphFont");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(_glyph);
        sp.Children.Add(_text);
        Content = sp;
        Margin = new Thickness(6, 0, 0, 0);
        Update();
    }

    /// <summary>Значок из шрифта Segoe Fluent Icons, например "".</summary>
    public string Glyph
    {
        get => _glyph.Text;
        set { _glyph.Text = value; Update(); }
    }

    public string Text
    {
        get => _text.Text;
        set { _text.Text = value; Update(); }
    }

    /// <summary>Только значок.</summary>
    public bool Compact
    {
        get => _compact;
        set { _compact = value; Update(); }
    }

    /// <summary>Подсказка (в компактном виде к ней добавляется подпись).</summary>
    public object? Tip
    {
        get => _extraTip;
        set { _extraTip = value; Update(); }
    }

    /// <summary>Квадратная кнопка заданного размера (боковая панель браузера): подпись не растягивает её.</summary>
    public double FixedSize
    {
        get => _fixed;
        set { _fixed = value; Update(); }
    }

    private double _fixed, _height;

    /// <summary>Высота кнопки шапки (0 — из стиля); значок без подписи тогда квадратный.</summary>
    public double ButtonHeight
    {
        get => _height;
        set { _height = value; Update(); }
    }

    /// <summary>«Включено» — то же, что IsChecked.</summary>
    public bool IsOn
    {
        get => IsChecked == true;
        set => IsChecked = value;
    }

    private void Update()
    {
        var hasText = !_compact && _text.Text.Length > 0;
        _text.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        _glyph.Visibility = _glyph.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _text.Margin = new Thickness(_glyph.Text.Length > 0 ? 8 : 0, 0, 0, 0);
        Padding = hasText ? new Thickness(12, 0, 14, 0) : new Thickness(0);
        Width = _fixed > 0 ? _fixed : hasText ? double.NaN : _height > 0 ? 40 : 36; // с высотой шапки значок — прямоугольник 40×высота
        if (_fixed > 0) Height = _fixed;
        else if (_height > 0) Height = _height;
        else ClearValue(HeightProperty); // вернуть высоту из стиля (после уголка кнопки оставались низкими)
        if (_extraTip is not null) ToolTip = _extraTip;
        else ToolTip = hasText || _text.Text.Length == 0 ? null : _text.Text;
    }

    /// <summary>Щелчок не переключает вид сам — состояние ставит программа, когда действие удалось.</summary>
    protected override void OnToggle() { }
}
