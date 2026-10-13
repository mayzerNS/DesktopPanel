using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopPanel.UI.Controls;

/// <summary>Общее для строк и блоков: подложка-«плашка».</summary>
public abstract class PlateBase : Border
{
    protected PlateBase()
    {
        SetResourceReference(BackgroundProperty, "PlateBrush");
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(14, 10, 14, 10);
        Margin = new Thickness(0, 0, 0, 8);
    }

    protected static TextBlock Text(string style)
    {
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(StyleProperty, style);
        return t;
    }
}

/// <summary>
/// Строка настроек: слева заголовок (+ «?») и пояснение, справа — содержимое (кнопки).
/// Lead — то, что стоит перед заголовком (например, цветная точка).
/// </summary>
[ContentProperty(nameof(Right))]
public class Row : PlateBase
{
    private readonly TextBlock _title = Text("RowTitleText");
    private readonly TextBlock _sub = Text("RowSubText");
    private readonly HelpDot _help = new();
    private readonly ContentPresenter _right = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentPresenter _lead = new() { VerticalAlignment = VerticalAlignment.Center };

    public Row()
    {
        _sub.Visibility = Visibility.Collapsed;
        _sub.Margin = new Thickness(0, 2, 0, 0);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(_title);
        head.Children.Add(_help);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(head);
        left.Children.Add(_sub);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 1);
        Grid.SetColumn(_right, 2);
        g.Children.Add(_lead);
        g.Children.Add(left);
        g.Children.Add(_right);
        Child = g;
    }

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public string Sub
    {
        get => _sub.Text;
        set
        {
            _sub.Text = value ?? "";
            _sub.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Цвет пояснения (null — обычный).</summary>
    public Brush? SubBrush
    {
        set
        {
            if (value is null) _sub.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            else _sub.Foreground = value;
        }
    }

    public string Help
    {
        get => _help.Key;
        set => _help.Key = value;
    }

    public object? Right
    {
        get => _right.Content;
        set => _right.Content = value;
    }

    public object? Lead
    {
        get => _lead.Content;
        set => _lead.Content = value;
    }
}

/// <summary>Блок: заголовок (+ «?» и что-нибудь справа от него), под ним — содержимое столбиком.</summary>
[ContentProperty(nameof(Children))]
public class Box : PlateBase
{
    private readonly TextBlock _title = Text("RowTitleText");
    private readonly HelpDot _help = new();
    private readonly ContentPresenter _headerRight = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _body = new();

    public Box()
    {
        var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_title);
        left.Children.Add(_help);
        Grid.SetColumn(_headerRight, 1);
        head.Children.Add(left);
        head.Children.Add(_headerRight);

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(_body);
        Child = root;
    }

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public string Help
    {
        get => _help.Key;
        set => _help.Key = value;
    }

    public object? HeaderRight
    {
        get => _headerRight.Content;
        set => _headerRight.Content = value;
    }

    public UIElementCollection Children => _body.Children;
}

/// <summary>Цветная точка-статус: зелёная — хорошо, жёлтая — так себе, красная — плохо.</summary>
public static class StatusDot
{
    public static readonly Color Green = Color.FromRgb(0x5C, 0xD6, 0x7A);
    public static readonly Color Yellow = Color.FromRgb(0xF0, 0xC0, 0x50);
    public static readonly Color Red = Color.FromRgb(0xF0, 0x6E, 0x64);

    public static Ellipse Make(Color c) => new()
    {
        Width = 10,
        Height = 10,
        Fill = new SolidColorBrush(c),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
}

/// <summary>
/// Переключатель из нескольких кнопок (выбрана одна). SetItems задаёт варианты, Picked — выбор пользователя.
/// </summary>
public sealed class Segmented : StackPanel
{
    private readonly Dictionary<string, ToggleButton> _buttons = new();
    private string? _selected;

    public event Action<string>? Picked;

    public Segmented()
    {
        Orientation = Orientation.Horizontal;
    }

    public void SetItems(IEnumerable<(string Key, string Text)> items)
    {
        Children.Clear();
        _buttons.Clear();
        foreach (var (key, text) in items)
        {
            var b = new ToggleButton { Content = text, Margin = new Thickness(0, 0, 4, 0), Tag = key };
            b.SetResourceReference(StyleProperty, "PanelToggle");
            b.Click += (_, _) =>
            {
                b.IsChecked = key == _selected; // вид меняет только Selected — после ответа программы
                Picked?.Invoke(key);
            };
            _buttons[key] = b;
            Children.Add(b);
        }
        Selected = _selected;
    }

    /// <summary>Выбранный вариант (null — ни один).</summary>
    public string? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            foreach (var (k, b) in _buttons) b.IsChecked = k == value;
        }
    }

    public IEnumerable<ButtonBase> Buttons => _buttons.Values;
}
