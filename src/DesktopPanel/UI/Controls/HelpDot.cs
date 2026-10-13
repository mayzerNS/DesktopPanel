using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DesktopPanel.Core.Localization;

namespace DesktopPanel.UI.Controls;

/// <summary>
/// Кружок «?» с подсказкой. Текст — из словаря: help.{Key}.title / .body / .plus / .minus
/// (плюс и минус необязательны). Пустой Key — кружок не показывается.
/// </summary>
public sealed class HelpDot : Border
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(string), typeof(HelpDot), new PropertyMetadata("", (d, _) => ((HelpDot)d).Rebuild()));

    public string Key
    {
        get => (string)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public HelpDot()
    {
        Width = Height = 16;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
        Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        Margin = new Thickness(8, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        // не Cursors.Help: у него свой «?» рядом со стрелкой — выглядит как второй вопросик рядом с подсказкой
        Child = new TextBlock
        {
            Text = "?",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetBetweenShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, int.MaxValue);
        Visibility = Visibility.Collapsed;
    }

    private void Rebuild()
    {
        var k = Key;
        if (string.IsNullOrEmpty(k))
        {
            Visibility = Visibility.Collapsed;
            ToolTip = null;
            return;
        }
        Visibility = Visibility.Visible;
        ToolTip = Build(k);
    }

    /// <summary>Содержимое подсказки (его же можно повесить на любую кнопку).</summary>
    public static TextBlock Build(string key)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 380, LineHeight = 18 };
        var title = Opt($"help.{key}.title");
        if (title is not null) tb.Inlines.Add(new Run(title) { FontWeight = FontWeights.SemiBold });
        var body = Opt($"help.{key}.body");
        if (body is not null)
        {
            if (tb.Inlines.Count > 0) tb.Inlines.Add(new LineBreak());
            tb.Inlines.Add(new Run(body));
        }
        AddLine(tb, "help.plus", Opt($"help.{key}.plus"), Color.FromRgb(0x9F, 0xD0, 0xFF));
        AddLine(tb, "help.minus", Opt($"help.{key}.minus"), Color.FromRgb(0xF0, 0xB0, 0x70));
        return tb;
    }

    private static void AddLine(TextBlock tb, string labelKey, string? text, Color color)
    {
        if (text is null) return;
        tb.Inlines.Add(new LineBreak());
        tb.Inlines.Add(new Run(Loc.T(labelKey) + " ") { Foreground = new SolidColorBrush(color) });
        tb.Inlines.Add(new Run(text));
    }

    /// <summary>Строка словаря или null, если её нет (без предупреждения в лог).</summary>
    private static string? Opt(string key) => Loc.Has(key) ? Loc.T(key) : null;
}
