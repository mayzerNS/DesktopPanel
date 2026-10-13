using System.Text;
using System.Text.RegularExpressions;
using DesktopPanel.Core.Localization;

namespace DesktopPanel.Features.Zapret;

/// <summary>Ошибка Zapret с уже переведённым понятным текстом — её показываем пользователю как есть.</summary>
public sealed class ZapretException : Exception
{
    public ZapretException(string message) : base(message) { }
}

/// <summary>
/// Достаём из .bat-стратегии строку запуска winws.exe, чтобы запускать его напрямую — без cmd и без окна.
/// Переменные, которые стратегии берут из service.bat, считаем сами.
/// </summary>
public static class WinwsCommand
{
    /// <summary>Значение «порт 12» = игровой фильтр выключен (так делает service.bat).</summary>
    private const string GameOff = "12";

    public static Dictionary<string, string> Variables(string root)
    {
        var (mode, tcp, udp) = ZapretFiles.ReadGame(root);
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        env["BIN"] = Path.Combine(root, "bin") + "\\";
        env["LISTS"] = Path.Combine(root, "lists") + "\\";
        env["GAMEFILTER"] = mode switch { "all" or "tcp" => tcp, "udp" => udp, _ => GameOff };
        env["GAMEFILTERTCP"] = mode is "all" or "tcp" ? tcp : GameOff;
        env["GAMEFILTERUDP"] = mode is "all" or "udp" ? udp : GameOff;
        return env;
    }

    /// <summary>Аргументы winws.exe из стратегии (уже с подставленными переменными).</summary>
    public static string Arguments(string root, string bat)
    {
        string text;
        try { text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, bat))); }
        catch { throw new ZapretException(Loc.T("zapret.err.no_bat", bat)); }

        // строки, продолжающиеся через ^ в конце, склеиваем
        var lines = new List<string>();
        var buf = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var s = raw.TrimEnd('\r', ' ', '\t');
            if (s.EndsWith('^'))
            {
                buf.Append(s, 0, s.Length - 1).Append(' ');
                continue;
            }
            lines.Add(buf + s);
            buf.Clear();
        }

        string? args = null;
        foreach (var ln in lines)
        {
            var i = ln.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && ln.TrimStart().StartsWith("start", StringComparison.OrdinalIgnoreCase))
            {
                args = ln[(i + "winws.exe".Length)..];
                if (args.StartsWith('"')) args = args[1..];
                break;
            }
        }
        if (args is null) throw new ZapretException(Loc.T("zapret.err.no_winws_line", bat));

        var env = Variables(root);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string Sub(Match m)
        {
            if (env.TryGetValue(m.Groups[1].Value, out var v)) return v;
            missing.Add(m.Groups[1].Value);
            return m.Value;
        }

        var r = Regex.Replace(args, "%~dp0", _ => root + "\\", RegexOptions.IgnoreCase).Replace("%%", "\0");
        r = Regex.Replace(r, @"%([A-Za-z_]\w*)%", Sub);
        r = Regex.Replace(r, @"!([A-Za-z_]\w*)!", Sub);
        r = Regex.Replace(r, @"\^(.)", "$1").Replace("\0", "%").Trim();
        if (missing.Count > 0)
            throw new ZapretException(Loc.T("zapret.err.unknown_vars", string.Join(", ", missing)));
        return r;
    }

    /// <summary>Полная командная строка (для службы Windows).</summary>
    public static string Full(string root, string bat) => $"\"{ZapretPaths.Winws(root)}\" {Arguments(root, bat)}";
}
