namespace DesktopPanel.Core;

/// <summary>Где программа хранит свои файлы.</summary>
public static class Paths
{
    public const string AppName = "DesktopPanel";

    /// <summary>%APPDATA%\DesktopPanel — настройки, журнал отката, логи.</summary>
    public static string AppData { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    /// <summary>%LOCALAPPDATA%\DesktopPanel — большие скачиваемые файлы (Zapret, кэш).</summary>
    public static string LocalData { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>
    /// C:\ProgramData\DesktopPanel — то, что запускается от системы (драйвер WinDivert для Zapret):
    /// из профиля пользователя Windows его не загружает (ошибка 3 «путь не найден»).
    /// </summary>
    public static string SharedData { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);

    /// <summary>Путь к самому exe.</summary>
    public static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppName + ".exe");
}
