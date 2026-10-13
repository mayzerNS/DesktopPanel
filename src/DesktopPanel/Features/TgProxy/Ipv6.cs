using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.TgProxy;

/// <summary>
/// IPv6 на сетевых адаптерах Windows (компонент ms_tcpip6). Если он включён, Telegram может пытаться подключаться
/// по IPv6 мимо прокси. Выключаем только там, где он был включён, и запоминаем адаптеры (по GUID — имена бывают
/// русскими, а вывод консоли их портит), чтобы потом вернуть ровно как было.
/// </summary>
public static class Ipv6
{
    /// <summary>Выключить на всех адаптерах, где включён. Возвращает GUID тех, где выключили.</summary>
    public static List<string> Disable()
    {
        const string script =
            "Get-NetAdapter -IncludeHidden | Where-Object { ($_ | Get-NetAdapterBinding -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue).Enabled } | " +
            "ForEach-Object { $_ | Disable-NetAdapterBinding -ComponentID ms_tcpip6 -Confirm:$false; $_.InterfaceGuid }";
        var r = Run(script);
        var guids = r.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{') && l.EndsWith('}')).ToList();
        Log.Info($"ipv6: выключен на {guids.Count} адаптерах");
        return guids;
    }

    /// <summary>Включить обратно там, где выключали мы.</summary>
    public static void Restore(IReadOnlyCollection<string> guids)
    {
        if (guids.Count == 0) return;
        var list = string.Join(",", guids.Select(g => $"'{g.Replace("'", "")}'"));
        Run($"Get-NetAdapter -IncludeHidden | Where-Object {{ @({list}) -contains $_.InterfaceGuid }} | " +
            "Enable-NetAdapterBinding -ComponentID ms_tcpip6");
        Log.Info($"ipv6: включён обратно на {guids.Count} адаптерах");
    }

    private static string Run(string script)
    {
        var r = Shell.Run("powershell", 60000, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script);
        if (!r.Ok) Log.Warn($"ipv6: powershell вернул {r.ExitCode}");
        return r.Output;
    }
}
