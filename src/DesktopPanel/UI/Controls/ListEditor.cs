using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopPanel.Core.Localization;

namespace DesktopPanel.UI.Controls;

/// <summary>
/// Редактор списка прямо в панели (вместо Блокнота): по строке на запись, строки с # — комментарии.
/// Каждая строка проверяется на ходу (Check): ошибка — внизу пишется, какая строка и что с ней, «Сохранить» недоступна;
/// то, что можно поправить само (ссылку вместо домена и т.п.), поправляется при сохранении — внизу видно, сколько строк.
/// </summary>
public sealed class ListEditor : Border
{
    /// <summary>Результат проверки строки: Fixed — как записать (null — оставить как есть), Error — что не так (null — всё хорошо).</summary>
    public readonly record struct LineCheck(string? Fixed, string? Error);

    private readonly TextBox _box;
    private readonly TextBlock _status;
    private readonly Button _save;
    private readonly Func<string, LineCheck> _check;
    private bool _loading;

    /// <summary>Сохранить: текст уже с поправками, без пустых строк в конце.</summary>
    public event Action<string>? Saved;

    public event Action? Cancelled;

    public Button SaveButton => _save;

    /// <param name="check">проверка одной строки (уже без пробелов по краям; пустые строки и комментарии сюда не попадают)</param>
    /// <param name="helpKey">подсказка «?» (help.{key}) — что сюда писать, с примерами</param>
    /// <param name="hint">серый текст в пустом поле</param>
    public ListEditor(Func<string, LineCheck> check, string helpKey, string hint)
    {
        _check = check;
        Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(10);
        Margin = new Thickness(0, 0, 0, 8);
        Visibility = Visibility.Collapsed;

        _box = new TextBox
        {
            Height = 180, AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(10, 8, 10, 8),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, Tag = hint,
        };
        _box.SetResourceReference(StyleProperty, "InputBox");
        ScrollViewer.SetVerticalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        _box.TextChanged += (_, _) =>
        {
            if (!_loading) Recheck();
        };

        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) };
        _status.SetResourceReference(StyleProperty, "RowSubText");

        var cancel = new Button { Content = Loc.T("zapret.edit.cancel"), Margin = new Thickness(8, 0, 0, 0) };
        cancel.SetResourceReference(StyleProperty, "PanelButton");
        cancel.Click += (_, _) => Cancelled?.Invoke();
        _save = new Button { Content = Loc.T("zapret.edit.save"), Margin = new Thickness(8, 0, 0, 0) };
        _save.SetResourceReference(StyleProperty, "AccentButton");
        _save.Click += (_, _) =>
        {
            if (Normalize(out var text)) Saved?.Invoke(text);
        };

        var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(new HelpDot { Key = helpKey, Margin = new Thickness(0, 0, 4, 0) });
        buttons.Children.Add(cancel);
        buttons.Children.Add(_save);
        Grid.SetColumn(buttons, 1);
        bottom.Children.Add(_status);
        bottom.Children.Add(buttons);

        var root = new StackPanel();
        root.Children.Add(_box);
        root.Children.Add(bottom);
        Child = root;
    }

    /// <summary>Показать текст файла (без проверки «с нуля» — сразу видно, что в нём не так).</summary>
    public void Load(string text)
    {
        _loading = true;
        _box.Text = text.Replace("\r\n", "\n").TrimEnd('\n').Replace("\n", Environment.NewLine);
        _loading = false;
        Recheck();
        _box.CaretIndex = _box.Text.Length;
    }

    public void FocusText()
    {
        _box.Focus();
        System.Windows.Input.Keyboard.Focus(_box);
    }

    private void Recheck()
    {
        var entries = 0;
        var fixes = 0;
        string? error = null;
        var lines = _box.Text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var c = _check(t);
            if (c.Error is not null)
            {
                error ??= Loc.T("zapret.edit.bad_line", i + 1, Short(t), c.Error);
                continue;
            }
            entries++;
            if (c.Fixed is not null && c.Fixed != t) fixes++;
        }
        _save.IsEnabled = error is null;
        _status.Foreground = error is null ? (Brush)FindResource("SubTextBrush") : new SolidColorBrush(Color.FromRgb(0xF0, 0xB0, 0x70));
        _status.Text = error ?? (Loc.Plural(entries, "zapret.entries") + (fixes > 0 ? " · " + Loc.T("zapret.edit.fixes", fixes) : ""));
    }

    private static string Short(string s) => s.Length <= 40 ? s : s[..37] + "…";

    /// <summary>Текст с поправками; false — есть ошибка.</summary>
    private bool Normalize(out string text)
    {
        var lines = _box.Text.Replace("\r\n", "\n").Split('\n');
        var res = new List<string>();
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.Length == 0 || t.StartsWith('#'))
            {
                res.Add(t);
                continue;
            }
            var c = _check(t);
            if (c.Error is not null)
            {
                text = "";
                return false;
            }
            res.Add(c.Fixed ?? t);
        }
        // одинаковые записи — один раз
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        res = res.Where(l => l.Length == 0 || l.StartsWith('#') || seen.Add(l)).ToList();
        while (res.Count > 0 && res[^1].Length == 0) res.RemoveAt(res.Count - 1);
        text = string.Join("\n", res) + "\n";
        return true;
    }
}
