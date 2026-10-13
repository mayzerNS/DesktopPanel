using System.Text.RegularExpressions;
using Microsoft.Win32;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.Zapret;

/// <summary>Одна строка диагностики. Fix — ключ кнопки «Исправить» (null — исправлять нечем).</summary>
public sealed record DiagItem(DiagLevel Level, string Title, string Detail, string? Fix);

public enum DiagLevel { Ok, Warn, Error }

/// <summary>Проверка всего, что мешает Zapret работать, и исправление того, что можно исправить.</summary>
public static class ZapretDiagnostics
{
    public const string FixUpdate = "update", FixLists = "lists", FixDefender = "defender", FixBfe = "bfe",
        FixConflicts = "conflicts", FixWinDivert = "windivert";

    private static readonly string[] OtherBypass = { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" };

    public static List<DiagItem> Run(string root)
    {
        var res = new List<DiagItem>();
        void Add(DiagLevel l, string titleKey, string detail = "", string? fix = null, params object[] args) =>
            res.Add(new DiagItem(l, Loc.T(titleKey, args), detail, fix));

        if (string.IsNullOrEmpty(root))
        {
            Add(DiagLevel.Error, "zapret.diag.not_downloaded", fix: FixUpdate);
        }
        else
        {
            if (!File.Exists(Path.Combine(root, "bin", "WinDivert64.sys")))
                Add(DiagLevel.Error, "zapret.diag.no_driver", Loc.T("zapret.diag.no_driver.sub"), FixUpdate);
            else
                Add(DiagLevel.Ok, "zapret.diag.files_ok");
            var miss = ZapretFiles.UserLists.Select(u => u.File)
                .Where(f => !File.Exists(Path.Combine(root, "lists", f))).ToList();
            if (miss.Count > 0)
                Add(DiagLevel.Error, "zapret.diag.no_lists", Loc.T("zapret.diag.no_lists.sub", string.Join(", ", miss)), FixLists);
        }

        var ex = DefenderExcluded();
        if (ex == false)
            Add(DiagLevel.Warn, "zapret.diag.defender", Loc.T("zapret.diag.defender.sub"), FixDefender);
        else if (ex == true)
            Add(DiagLevel.Ok, "zapret.diag.defender_ok");

        if (Shell.Run("sc", "query", "BFE").Output.Contains("RUNNING"))
            Add(DiagLevel.Ok, "zapret.diag.bfe_ok");
        else
            Add(DiagLevel.Error, "zapret.diag.bfe", Loc.T("zapret.diag.bfe.sub"), FixBfe);

        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (k?.GetValue("ProxyEnable") is int on && on != 0)
                Add(DiagLevel.Warn, "zapret.diag.proxy", Loc.T("zapret.diag.proxy.sub"), null, k.GetValue("ProxyServer") ?? "");
        }
        catch { }

        var svcs = Services();
        if (Shell.IsRunning("AdguardSvc"))
            Add(DiagLevel.Error, "zapret.diag.adguard", Loc.T("zapret.diag.adguard.sub"));

        var bad = new (string Title, Func<string, string, bool> Test)[]
        {
            ("Killer", (n, d) => n.Contains("killer") || d.Contains("killer")),
            ("Intel Connectivity Network Service", (n, d) => d.Contains("intel") && d.Contains("connectivity") && d.Contains("network")),
            ("Check Point", (n, d) => n.Contains("tracsrvwrapper") || n == "epwd"),
            ("SmartByte", (n, d) => n.Contains("smartbyte") || d.Contains("smartbyte")),
        };
        foreach (var (title, test) in bad)
            if (svcs.Any(s => test(s.Name.ToLowerInvariant(), s.Display.ToLowerInvariant())))
                Add(DiagLevel.Error, "zapret.diag.conflict", Loc.T("zapret.diag.conflict.sub"), null, title);

        var other = svcs.Where(s => OtherBypass.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).Select(s => s.Name).ToList();
        if (other.Count > 0)
            Add(DiagLevel.Error, "zapret.diag.other", Loc.T("zapret.diag.other.sub"), FixConflicts, string.Join(", ", other));

        var vpn = svcs.Where(s => s.State == "RUNNING" && (s.Name + s.Display).Contains("vpn", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Display.Length > 0 ? s.Display : s.Name).Take(3).ToList();
        if (vpn.Count > 0)
            Add(DiagLevel.Warn, "zapret.diag.vpn", Loc.T("zapret.diag.vpn.sub"), null, string.Join(", ", vpn));

        var wd = svcs.FirstOrDefault(s => s.Name.Equals("WinDivert", StringComparison.OrdinalIgnoreCase)).State ?? "";
        if (!ZapretRuntime.IsRunning && wd is "RUNNING" or "STOP_PENDING")
            Add(DiagLevel.Warn, "zapret.diag.windivert", Loc.T("zapret.diag.windivert.sub"), FixWinDivert);

        if (ZapretRuntime.ServiceState() is not null)
            Add(DiagLevel.Warn, "zapret.diag.service",
                Loc.T("zapret.diag.service.sub", ZapretRuntime.ServiceStrategy() ?? "?"));

        if (Regex.IsMatch(ZapretPaths.Dir, "[а-яё]", RegexOptions.IgnoreCase))
            Add(DiagLevel.Warn, "zapret.diag.cyrillic", ZapretPaths.Dir);
        var od = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(od) && ZapretPaths.Dir.StartsWith(od, StringComparison.OrdinalIgnoreCase))
            Add(DiagLevel.Error, "zapret.diag.onedrive");

        if (!DohEnabled())
            Add(DiagLevel.Warn, "zapret.diag.doh", Loc.T("zapret.diag.doh.sub"));
        if (ZapretFiles.HostsHasYoutube())
            Add(DiagLevel.Warn, "zapret.diag.hosts_youtube", Loc.T("zapret.diag.hosts_youtube.sub"));
        return res;
    }

    /// <summary>Исправить то, что нашла диагностика. update и defender делает ZapretService/интерфейс.</summary>
    public static void Fix(string key, string root)
    {
        switch (key)
        {
            case FixLists:
                ZapretFiles.EnsureUserLists(root);
                break;
            case FixBfe:
                Shell.Run("sc", "config", "BFE", "start=", "auto");
                Shell.Run("sc", "start", "BFE");
                break;
            case FixConflicts or FixWinDivert:
                var names = (key == FixConflicts ? OtherBypass : Array.Empty<string>()).Concat(new[] { "WinDivert", "WinDivert14" });
                foreach (var n in names)
                {
                    Shell.Run("sc", "stop", n);
                    Shell.Run("sc", "delete", n);
                }
                break;
        }
    }

    // ---------- Defender ----------
    /// <summary>
    /// Папка Zapret в исключениях Defender? null — не узнать (Defender выключен или заменён другим антивирусом).
    /// Только читаем: добавлять исключение программа сама не будет — это делает пользователь
    /// (см. OpenDefenderSettings).
    /// </summary>
    public static bool? DefenderExcluded()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths");
            if (k is null) return null;
            var dir = ZapretPaths.Dir.TrimEnd('\\');
            return k.GetValueNames().Any(p =>
            {
                var x = Environment.ExpandEnvironmentVariables(p).TrimEnd('\\');
                return x.Length > 0 && (dir.Equals(x, StringComparison.OrdinalIgnoreCase)
                                        || dir.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase));
            });
        }
        catch { return null; }
    }

    /// <summary>Открыть «Безопасность Windows» → параметры защиты от вирусов (там «Исключения»).</summary>
    public static void OpenDefenderSettings() => Shell.Open("windowsdefender://threatsettings");

    // ---------- службы ----------
    private readonly record struct Svc(string Name, string Display, string State);

    /// <summary>Все службы из sc query (вывод бывает на русском — берём только то, что после двоеточия).</summary>
    private static List<Svc> Services()
    {
        var r = Shell.Run("sc", 30000, "query", "type=", "service", "state=", "all");
        var res = new List<Svc>();
        foreach (var block in Regex.Split(r.Output, @"\r?\n\s*\r?\n"))
        {
            var lines = block.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count < 2 || !lines[0].Contains(':')) continue;
            var name = lines[0][(lines[0].IndexOf(':') + 1)..].Trim();
            var disp = lines[1].Contains(':') ? lines[1][(lines[1].IndexOf(':') + 1)..].Trim() : "";
            var m = ZapretRuntime.StateRe.Match(block);
            res.Add(new Svc(name, disp, m.Success ? m.Groups[1].Value : ""));
        }
        return res;
    }

    // ---------- DNS через HTTPS ----------
    private static bool DohEnabled()
    {
        static bool Walk(RegistryKey? k, int depth)
        {
            if (k is null) return false;
            using (k)
            {
                try
                {
                    if (k.GetValue("DohFlags") is int f && f != 0) return true;
                    if (k.GetValue("DohFlags") is long l && l != 0) return true;
                }
                catch { }
                if (depth >= 6) return false;
                string[] subs;
                try { subs = k.GetSubKeyNames(); }
                catch { return false; }
                foreach (var s in subs)
                {
                    RegistryKey? sub = null;
                    try { sub = k.OpenSubKey(s); }
                    catch { }
                    if (Walk(sub, depth + 1)) return true;
                }
                return false;
            }
        }
        try
        {
            return Walk(Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters"), 0);
        }
        catch { return false; }
    }

    // ---------- кэш Discord ----------
    /// <summary>Закрыть Discord и удалить его кэш. Возвращает, какие версии Discord почистили.</summary>
    public static List<string> ClearDiscordCache()
    {
        var done = new List<string>();
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var folder in new[] { "discord", "discordptb", "discordcanary", "discorddevelopment" })
        {
            var b = Path.Combine(appdata, folder);
            if (!Directory.Exists(b)) continue;
            var exe = folder switch
            {
                "discordptb" => "DiscordPTB", "discordcanary" => "DiscordCanary",
                "discorddevelopment" => "DiscordDevelopment", _ => "Discord",
            };
            if (Shell.IsRunning(exe))
            {
                Shell.Run("taskkill", "/F", "/IM", exe + ".exe");
                Thread.Sleep(500);
            }
            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                try { Directory.Delete(Path.Combine(b, sub), true); }
                catch { }
            }
            done.Add(folder);
        }
        return done;
    }
}
