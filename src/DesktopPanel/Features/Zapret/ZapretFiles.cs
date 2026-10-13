using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.Zapret;

/// <summary>Файлы настроек Zapret: свои списки, игровой фильтр, IPSet, фейковые пакеты, hosts.</summary>
public static class ZapretFiles
{
    // ---------- свои списки ----------
    /// <summary>
    /// Свои списки. В архиве Zapret их нет — их создаёт его service.bat; без них winws.exe сразу падает.
    /// Пустыми их оставлять нельзя, поэтому кладём строку-заглушку.
    /// </summary>
    public static readonly (string File, string HelpKey, string Default)[] UserLists =
    {
        ("list-general-user.txt", "l_general", "# Never leave this file empty\ndomain.example.abc\n"),
        ("list-exclude-user.txt", "l_exclude", "domain.example.abc\n"),
        ("ipset-exclude-user.txt", "l_ipex", "203.0.113.113/32\n"),
    };

    private static readonly HashSet<string> Placeholders = new() { "domain.example.abc", "203.0.113.113/32" };

    public static void EnsureUserLists(string root)
    {
        var dir = Path.Combine(root, "lists");
        Directory.CreateDirectory(dir);
        foreach (var (file, _, def) in UserLists)
        {
            var p = Path.Combine(dir, file);
            var empty = !File.Exists(p) || !File.ReadLines(p).Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
            if (empty) File.WriteAllText(p, def);
        }
        // списки правят в Блокноте, а он открывается без прав администратора (см. Shell.Notepad) —
        // разрешаем пользователям менять файлы этой папки (Zapret лежит в ProgramData, там по умолчанию только чтение)
        Shell.Run("icacls", dir, "/grant", "*S-1-5-32-545:(OI)(CI)M", "/T", "/Q");
    }

    /// <summary>Текст своего списка для редактора — без строк-заглушек (их программа вернёт сама, если список пуст).</summary>
    public static string ReadUserList(string path)
    {
        try
        {
            return string.Join("\n", File.ReadAllLines(path).Where(l =>
            {
                var t = l.Trim();
                return !Placeholders.Contains(t) && t != "# Never leave this file empty";
            }));
        }
        catch { return ""; }
    }

    /// <summary>Записать свой список; пустой — со строкой-заглушкой (пустым Zapret его не принимает).</summary>
    public static void WriteUserList(string root, string file, string text)
    {
        var p = Path.Combine(root, "lists", file);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var real = text.Split('\n').Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
        if (!real) text = (text.Trim().Length > 0 ? text.TrimEnd() + "\n" : "") + UserLists.First(u => u.File == file).Default;
        File.WriteAllText(p, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
    }

    private static readonly Regex DomainRe = new(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$");

    /// <summary>
    /// Строка списка доменов. Ссылку (https://www.youtube.com/watch?v=…) превращает в домен (youtube.com): без «www.»
    /// (поддомены Zapret учитывает сам), без порта и пути; русские домены — в виде xn--… (так их понимает Zapret).
    /// </summary>
    public static (string? Fixed, string? Error) CheckDomain(string line)
    {
        var t = line.Trim().ToLowerInvariant();
        var i = t.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) t = t[(i + 3)..];
        var cut = t.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) t = t[..cut];
        var at = t.LastIndexOf('@');
        if (at >= 0) t = t[(at + 1)..];
        var colon = t.LastIndexOf(':');
        if (colon >= 0 && t.IndexOf(':') == colon) t = t[..colon];
        t = t.TrimStart('*', '.').TrimEnd('.');
        if (t.StartsWith("www.")) t = t[4..];
        try { t = new System.Globalization.IdnMapping().GetAscii(t); }
        catch { return (null, T("zapret.edit.not_domain")); }
        if (System.Net.IPAddress.TryParse(t, out _)) return (null, T("zapret.edit.ip_in_domains"));
        return DomainRe.IsMatch(t) ? (t, null) : (null, T("zapret.edit.not_domain"));
    }

    /// <summary>Строка списка IP: адрес, подсеть (адрес/длина) или диапазон (адрес-адрес); IPv4 и IPv6.</summary>
    public static (string? Fixed, string? Error) CheckIp(string line)
    {
        var t = line.Trim().Replace(" ", "");
        bool Ip(string s, out System.Net.IPAddress a) => System.Net.IPAddress.TryParse(s, out a!) && (s.Contains(':') || s.Count(c => c == '.') == 3);
        var dash = t.IndexOf('-');
        if (dash > 0)
            return Ip(t[..dash], out var a1) && Ip(t[(dash + 1)..], out var a2) && a1.AddressFamily == a2.AddressFamily
                ? (t, null) : (null, T("zapret.edit.not_ip"));
        var slash = t.IndexOf('/');
        if (slash > 0)
        {
            if (!Ip(t[..slash], out var a) || !int.TryParse(t[(slash + 1)..], out var len)) return (null, T("zapret.edit.not_ip"));
            var max = a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            return len >= 0 && len <= max ? (t, null) : (null, T("zapret.edit.bad_prefix"));
        }
        return Ip(t, out _) ? (t, null) : (null, T("zapret.edit.not_ip"));
    }

    private static string T(string key) => DesktopPanel.Core.Localization.Loc.T(key);

    /// <summary>Сколько настоящих записей в списке (без комментариев и заглушек).</summary>
    public static int CountEntries(string path)
    {
        try
        {
            return File.ReadLines(path).Count(l =>
            {
                var t = l.Trim();
                return t.Length > 0 && !t.StartsWith('#') && !Placeholders.Contains(t);
            });
        }
        catch { return 0; }
    }

    // ---------- игровой фильтр (формат 1.10.3: mode=… / tcp=… / udp=…) ----------
    public static readonly string[] GameModes = { "disabled", "all", "tcp", "udp" };
    public const string AllPorts = "1024-65535";
    public const string NoRtmp = "1024-1934,1936-65535";

    private static readonly Regex RangeRe = new(@"^\d{1,5}(-\d{1,5})?(,\d{1,5}(-\d{1,5})?)*$");

    /// <summary>Проверить диапазон портов вида «1024-1934,1936-65535». null — неверный.</summary>
    public static string? ValidRange(string? s)
    {
        s = (s ?? "").Replace(" ", "");
        if (!RangeRe.IsMatch(s)) return null;
        foreach (var part in s.Split(','))
        {
            var ab = part.Split('-');
            var a = int.Parse(ab[0]);
            var b = ab.Length > 1 ? int.Parse(ab[1]) : a;
            if (a <= 0 || a > b || b > 65535) return null;
        }
        return s;
    }

    public static string GameFile(string root) => Path.Combine(root, "utils", "game_filter.enabled");

    public static (string Mode, string Tcp, string Udp) ReadGame(string root)
    {
        string mode = "disabled", tcp = AllPorts, udp = AllPorts;
        string? tc = null, uc = null;
        string[] lines;
        try { lines = File.ReadAllLines(GameFile(root)); }
        catch { return (mode, tcp, udp); }

        foreach (var ln in lines)
        {
            var i = ln.IndexOf('=');
            var k = (i < 0 ? ln : ln[..i]).Trim().ToLowerInvariant();
            var v = i < 0 ? "" : ln[(i + 1)..].Trim();
            switch (k)
            {
                case "mode": mode = v.ToLowerInvariant(); break;
                case "all": mode = "all"; break;
                case "tcp" or "udp" when v.Length == 0: mode = k; break;
                case "tcp": tc = v; break;
                case "udp": uc = v; break;
            }
        }
        if (mode is not ("all" or "tcp" or "udp")) mode = "disabled";
        return (mode, ValidRange(tc) ?? tcp, ValidRange(uc) ?? udp);
    }

    public static void WriteGame(string root, string mode, string tcp, string udp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(root))!);
        File.WriteAllText(GameFile(root), $"mode={mode}\ntcp={tcp}\nudp={udp}\n", Encoding.ASCII);
    }

    // ---------- IPSet ----------
    public static readonly string[] IpsetModes = { "loaded", "none", "any" };
    private const string IpsetNone = "203.0.113.113/32";

    private static string IpsetFile(string root) => Path.Combine(root, "lists", "ipset-all.txt");

    /// <summary>loaded — по списку; none — выключен (заглушка); any — пустой файл = любые IP.</summary>
    public static string IpsetMode(string root)
    {
        string text;
        try { text = File.ReadAllText(IpsetFile(root)); }
        catch { return "any"; }
        if (string.IsNullOrWhiteSpace(text)) return "any";
        return text.Contains(IpsetNone) ? "none" : "loaded";
    }

    public static async Task UpdateIpsetAsync(string root, HttpClient http)
    {
        var data = await http.GetByteArrayAsync(ZapretPaths.RawService + "ipset-service.txt");
        if (data.Length < 100) throw new InvalidOperationException(Core.Localization.Loc.T("zapret.err.ipset_empty"));
        // в режимах «Выкл»/«Все IP» обновляем запасную копию, режим не меняем
        var dst = IpsetMode(root) == "loaded" ? IpsetFile(root) : IpsetFile(root) + ".backup";
        await File.WriteAllBytesAsync(dst, data);
    }

    public static async Task SetIpsetAsync(string root, string mode, HttpClient http)
    {
        var f = IpsetFile(root);
        var b = f + ".backup";
        var cur = IpsetMode(root);
        if (cur == mode) return;
        if (cur == "loaded") File.Move(f, b, overwrite: true);
        switch (mode)
        {
            case "none": File.WriteAllText(f, IpsetNone + "\n", Encoding.ASCII); break;
            case "any": File.WriteAllText(f, ""); break;
            default:
                if (File.Exists(b)) File.Move(b, f, overwrite: true);
                else
                {
                    File.WriteAllText(f, "");
                    await UpdateIpsetAsync(root, http);
                }
                break;
        }
    }

    // ---------- фейковые пакеты ----------
    /// <summary>Слоты фейков: файл в bin и ключ строки с его названием.</summary>
    public static readonly (string Slot, string TitleKey, string HelpKey)[] FakeSlots =
    {
        ("ACTIVE_DISCORD_UDP.bin", "zapret.fake.discord", "x_fake_discord"),
        ("ACTIVE_GAME_UDP.bin", "zapret.fake.game", "x_fake_game"),
    };

    public static List<string> FakeNames(string root)
    {
        try
        {
            var names = Directory.GetFiles(Path.Combine(root, "bin"), "*.bin")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null && !n.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!)
                .ToList();
            names.Sort(NativeMethods.StrCmpLogicalW);
            return names;
        }
        catch { return new(); }
    }

    private static byte[]? Hash(string path)
    {
        try { return SHA256.HashData(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    /// <summary>Какой фейк сейчас стоит в слоте (null — свой файл или слота нет).</summary>
    public static string? ActiveFake(string root, string slot)
    {
        var h = Hash(Path.Combine(root, "bin", slot));
        if (h is null) return null;
        return FakeNames(root).FirstOrDefault(n => Hash(Path.Combine(root, "bin", n + ".bin")) is { } x && x.SequenceEqual(h));
    }

    public static void SetFake(string root, string slot, string name)
    {
        var dst = Path.Combine(root, "bin", slot);
        File.Copy(Path.Combine(root, "bin", name + ".bin"), dst, overwrite: true);
        // копия получает дату исходника, а у всех фейков из архива она одна и та же: интерфейс по дате не замечал смену
        // и показывал старое название (переключение при этом работало). Ставим текущую дату.
        File.SetLastWriteTimeUtc(dst, DateTime.UtcNow);
    }

    // ---------- hosts ----------
    private static string HostsText()
    {
        try { return File.ReadAllText(ZapretPaths.Hosts); }
        catch { return ""; }
    }

    private static string StripBlock(string text) => Regex.Replace(text,
        $@"\r?\n?{Regex.Escape(ZapretPaths.HostsBegin)}.*?{Regex.Escape(ZapretPaths.HostsEnd)}\r?\n?", "\n",
        RegexOptions.Singleline);

    /// <summary>Записи hosts, которые советует Flowseal (для голоса Discord и т.п.).</summary>
    public static async Task<List<string>> RecommendedHostsAsync(HttpClient http)
    {
        var text = await http.GetStringAsync(ZapretPaths.RawService + "hosts");
        return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
    }

    private static string[] Words(string line) => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static List<string> MissingHosts(IEnumerable<string> lines)
    {
        var have = HostsText().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => string.Join(' ', Words(l))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return lines.Where(l => !have.Contains(string.Join(' ', Words(l)))).ToList();
    }

    public static bool HostsHasBlock() => HostsText().Contains(ZapretPaths.HostsBegin);

    private static void WriteHosts(string text)
    {
        File.WriteAllText(ZapretPaths.Hosts, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        Shell.Run("ipconfig", "/flushdns");
    }

    public static void AddHosts(IEnumerable<string> lines)
    {
        var text = StripBlock(HostsText()).TrimEnd('\r', '\n');
        WriteHosts($"{text}\n\n{ZapretPaths.HostsBegin}\n{string.Join("\n", lines)}\n{ZapretPaths.HostsEnd}\n");
    }

    public static void RemoveHosts() => WriteHosts(StripBlock(HostsText()).TrimEnd('\r', '\n') + "\n");

    /// <summary>Есть ли в hosts записи YouTube (мешают ему работать).</summary>
    public static bool HostsHasYoutube() =>
        Regex.IsMatch(HostsText(), @"^\s*[^#\s].*\b(youtube\.com|youtu\.be)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase);
}
