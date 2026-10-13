using DesktopPanel.Core;

namespace DesktopPanel.Features.Zapret;

/// <summary>Где лежит Zapret и откуда он берётся.</summary>
public static class ZapretPaths
{
    public const string ReleaseApi = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";
    public const string RawService = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/";

    /// <summary>Имя службы Windows, которую ставит Zapret (как в его service.bat).</summary>
    public const string ServiceName = "zapret";

    public const string HostsBegin = "# >>> zapret-discord-youtube (DesktopPanel)";
    public const string HostsEnd = "# <<< zapret-discord-youtube (DesktopPanel)";

    /// <summary>Не чаще раза в 3 часа (без токена GitHub даёт 60 запросов в час).</summary>
    public static readonly TimeSpan UpdateEvery = TimeSpan.FromHours(3);

    /// <summary>C:\ProgramData\DesktopPanel\zapret — сюда распаковываются версии Zapret.</summary>
    public static string Dir => Path.Combine(Paths.SharedData, "zapret");

    /// <summary>Старое место (%LOCALAPPDATA%\DesktopPanel\zapret): оттуда WinDivert не загружался — переносим.</summary>
    public static string OldDir => Path.Combine(Paths.LocalData, "zapret");

    public static string WinwsLog => Path.Combine(Paths.AppData, "logs", "winws.log");
    public static string TestLog => Path.Combine(Paths.AppData, "logs", "zapret-test.log");

    public static string Hosts => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "drivers", "etc", "hosts");

    public static string Winws(string root) => Path.Combine(root, "bin", "winws.exe");
}
