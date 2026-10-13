using Microsoft.Win32;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Core.Hotkeys;

/// <summary>
/// Сочетания Win+буква, которые занимает сам Проводник (Win+C — Copilot и т.п.).
/// У Проводника есть штатная настройка DisabledHotkeys: перечисленные в ней буквы он не занимает.
/// Работает после перезапуска Проводника. Удаление программы возвращает значение как было.
/// </summary>
public static class ShellHotkeys
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "DisabledHotkeys";

    /// <summary>Буква/цифра, которую можно освободить у Проводника (для Win+буква), иначе null.</summary>
    public static char? ShellLetter(Hotkey hk)
    {
        if (hk.Modifiers != System.Windows.Input.ModifierKeys.Windows) return null;
        var s = hk.ToString(); // "Win+C"
        var last = s[(s.LastIndexOf('+') + 1)..];
        return last.Length == 1 && char.IsLetterOrDigit(last[0]) ? char.ToUpperInvariant(last[0]) : null;
    }

    public static string Current()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(ValueName) as string ?? "";
        }
        catch { return ""; }
    }

    public static bool IsDisabled(char letter) => Current().Contains(letter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Попросить Проводник не занимать Win+letter. Возвращает прежнее значение (для отката).</summary>
    public static string Disable(char letter)
    {
        var before = Current();
        if (before.Contains(letter, StringComparison.OrdinalIgnoreCase)) return before;
        using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
        k.SetValue(ValueName, before + char.ToUpperInvariant(letter), RegistryValueKind.String);
        Log.Info($"shellhotkeys: Проводник больше не занимает Win+{letter}");
        return before;
    }

    /// <summary>Вернуть значение, которое было до программы (null — значения не было).</summary>
    public static void Restore(string? original)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (string.IsNullOrEmpty(original)) k.DeleteValue(ValueName, throwOnMissingValue: false);
            else k.SetValue(ValueName, original, RegistryValueKind.String);
            Log.Info("shellhotkeys: настройка Проводника возвращена");
        }
        catch (Exception ex) { Log.Warn($"shellhotkeys: не вернуть ({ex.Message})"); }
    }

    /// <summary>
    /// Перезапустить Проводник: панель задач и рабочий стол на пару секунд пропадут. Windows сама запускает
    /// Проводник заново после его закрытия; если этого не случилось — запускаем сами.
    /// </summary>
    public static void RestartExplorer()
    {
        Log.Info("shellhotkeys: перезапускаю Проводник");
        Shell.Run("taskkill", "/F", "/IM", "explorer.exe");
        Thread.Sleep(800); // старый процесс ещё может завершаться
        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(150);
            if (Shell.IsRunning("explorer")) return;
        }
        // Windows сама не перезапустила — запускаем без прав администратора (программа работает с ними,
        // а Проводник с правами администратора запускать нельзя)
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("runas.exe")
            {
                ArgumentList = { "/trustlevel:0x20000", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") },
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch (Exception ex) { Log.Warn($"shellhotkeys: Проводник не запустить ({ex.Message})"); }
    }
}
