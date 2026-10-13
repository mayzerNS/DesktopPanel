using System.Windows;
using System.Windows.Media;
using DesktopPanel.Core.Logging;
using Windows.UI.ViewManagement;

namespace DesktopPanel.UI.Theme;

/// <summary>
/// Цвет акцента Windows → ресурсы приложения (AccentBrush, AccentSoftBrush, AccentEdgeBrush).
/// Меняется на лету, если сменить акцент в «Параметры → Персонализация → Цвета».
/// В XAML использовать через {DynamicResource AccentBrush}.
/// </summary>
public static class Accent
{
    private static UISettings? _settings;

    public static Color Color { get; private set; } = Color.FromRgb(0, 120, 215);

    public static event Action? Changed;

    /// <summary>Свой цвет ("#RRGGBB") вместо цвета Windows; null или пусто — цвет Windows.</summary>
    public static string? Custom { get; private set; }

    /// <summary>
    /// Палитра: яркие (из «Цветов Windows», без почти одинаковых), пастельные, тёмные пастельные (приглушённые)
    /// и спокойные серые.
    /// </summary>
    public static readonly string[] Palette =
    {
        // яркие
        "#FFB900", "#FF8C00", "#F7630C", "#DA3B01", "#EF6950", "#D13438", "#E81123", "#EA005E",
        "#E3008C", "#C239B3", "#9A0089", "#8764B8", "#6B69D6", "#0078D7", "#0063B1", "#0099BC",
        "#2D7D9A", "#00B7C3", "#038387", "#00B294", "#00CC6A", "#10893E", "#498205",
        // пастельные
        "#FFD6A5", "#F7C6A3", "#FFADAD", "#F4A6C6", "#FFC6FF", "#D7BDE2", "#BDB2FF", "#A0C4FF",
        "#9BF6FF", "#B8E0D2", "#CAFFBF", "#FDFFB6",
        // тёмные пастельные
        "#C9897A", "#C47A8F", "#B57EA4", "#8E7CC3", "#6F9CC9", "#6E8FA6", "#5FA8A0", "#7FA36B",
        "#A3A35C", "#C7A15C", "#9C8466", "#8A9BB0",
        // серые
        "#7A7574", "#4C4A48", "#68768A", "#4A5459", "#567C73", "#647C64", "#847545",
    };

    /// <summary>Цвет палитры, ближайший к c (по расстоянию в RGB).</summary>
    public static string Nearest(Color c)
    {
        string best = Palette[0];
        var bestD = double.MaxValue;
        foreach (var hex in Palette)
        {
            var p = (Color)ColorConverter.ConvertFromString(hex);
            double dr = p.R - c.R, dg = p.G - c.G, db = p.B - c.B;
            var d = dr * dr * 0.3 + dg * dg * 0.59 + db * db * 0.11;
            if (d < bestD)
            {
                bestD = d;
                best = hex;
            }
        }
        return best;
    }

    /// <summary>Выбрать свой цвет (null — снова цвет Windows) и сразу применить.</summary>
    public static void SetCustom(string? hex)
    {
        Custom = string.IsNullOrWhiteSpace(hex) ? null : hex;
        Apply();
    }

    public static void Init(string? custom = null)
    {
        Custom = string.IsNullOrWhiteSpace(custom) ? null : custom;
        try
        {
            _settings = new UISettings();
            _settings.ColorValuesChanged += (_, _) =>
                Application.Current?.Dispatcher.InvokeAsync(Apply);
        }
        catch (Exception ex)
        {
            Log.Warn($"accent: не получить цвет акцента: {ex.Message}");
        }
        Apply();
    }

    private static void Apply()
    {
        try
        {
            if (Custom is not null && ColorConverter.ConvertFromString(Custom) is Color custom)
                Color = custom;
            else if (_settings is not null)
            {
                var c = _settings.GetColorValue(UIColorType.Accent);
                Color = Color.FromRgb(c.R, c.G, c.B);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"accent: {ex.Message}");
        }

        var res = Application.Current.Resources;
        res["AccentColor"] = Color;
        res["AccentBrush"] = Frozen(Color);
        res["AccentSoftBrush"] = Frozen(Color.FromArgb(150, Color.R, Color.G, Color.B));
        res["AccentHoverBrush"] = Frozen(Color.FromArgb(200, Color.R, Color.G, Color.B));
        res["AccentEdgeBrush"] = Frozen(Mix(Color, Colors.White, 0.35, 215));
        Changed?.Invoke();
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Mix(Color a, Color b, double t, byte alpha) => Color.FromArgb(alpha,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
