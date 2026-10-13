using System.Reflection;
using System.Text.Json;
using System.Windows.Markup;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Core.Localization;

/// <summary>
/// Все строки интерфейса — в словарях Strings/ru.json, Strings/en.json (вшиты в exe).
/// В коде: Loc.T("key") или Loc.T("key", arg0, arg1) для строк с {0}, {1}.
/// В XAML: Text="{loc:L key}".
/// Если строки нет в выбранном языке — берётся русская; если нет и там — показывается [key].
/// </summary>
public static class Loc
{
    public const string Fallback = "ru";

    /// <summary>Языки, которые можно выбрать (у каждого свой Strings/«язык».json).</summary>
    public static readonly string[] Available = { "ru", "en" };

    /// <summary>Все языки, которые показываем в мастере (недоступные, если появятся, — серыми).</summary>
    public static readonly string[] All = { "ru", "en" };

    private static Dictionary<string, string> _strings = new();
    private static Dictionary<string, string> _fallback = new();

    public static string Current { get; private set; } = Fallback;

    public static void Init(string language)
    {
        _fallback = LoadDictionary(Fallback);
        Current = Available.Contains(language) ? language : Fallback;
        _strings = Current == Fallback ? _fallback : LoadDictionary(Current);
        Log.Info($"loc: язык {Current}, строк {_strings.Count}");
    }

    public static string T(string key)
    {
        if (_strings.TryGetValue(key, out var s) || _fallback.TryGetValue(key, out s))
            return s;
        Log.Warn($"loc: нет строки «{key}»");
        return $"[{key}]";
    }

    /// <summary>Есть ли такая строка (для необязательных строк — без предупреждения в лог).</summary>
    public static bool Has(string key) => _strings.ContainsKey(key) || _fallback.ContainsKey(key);

    public static string T(string key, params object[] args)
    {
        try { return string.Format(T(key), args); }
        catch (FormatException) { return T(key); }
    }

    /// <summary>
    /// Число со словом в нужной форме: ключи {key}.one / .few / .many («1 запись», «2 записи», «5 записей»).
    /// Строки — с {0}: "{0} запись".
    /// </summary>
    public static string Plural(long n, string key)
    {
        string form;
        if (Current == "ru")
        {
            var m10 = Math.Abs(n) % 10;
            var m100 = Math.Abs(n) % 100;
            form = m10 == 1 && m100 != 11 ? "one"
                : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "few"
                : "many";
        }
        else
        {
            form = n == 1 ? "one" : "many";
        }
        return T($"{key}.{form}", n);
    }

    private static Dictionary<string, string> LoadDictionary(string lang)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Strings.{lang}.json");
            if (stream is null) return new();
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(stream,
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return dict ?? new();
        }
        catch (Exception ex)
        {
            Log.Error($"loc: словарь {lang} не читается", ex);
            return new();
        }
    }
}

/// <summary>Строка из словаря прямо в XAML: Text="{loc:L overlay.title}".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class L : MarkupExtension
{
    public L() { }
    public L(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
