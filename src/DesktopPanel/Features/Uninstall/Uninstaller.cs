using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using DesktopPanel.Core;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Features.Uninstall;

/// <summary>
/// Удаление программы. Программа записывает себя в «Параметры → Приложения» (раздел Uninstall в реестре
/// пользователя): оттуда «Удалить» запускает exe с ключом --uninstall, и программа показывает подтверждение.
/// Сам exe работающая программа удалить не может — после выхода его удаляет маленький .cmd во временной папке.
/// </summary>
public static class Uninstaller
{
    public const string Arg = "--uninstall";
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Paths.AppName;

    /// <summary>Записать (обновить) программу в списке «Приложения» Windows.</summary>
    public static void Register()
    {
        try
        {
            var exe = Paths.Exe;
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            k.SetValue("DisplayName", Paths.AppName);
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("DisplayVersion", typeof(Uninstaller).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
            k.SetValue("Publisher", Paths.AppName);
            k.SetValue("InstallLocation", Path.GetDirectoryName(exe) ?? "");
            k.SetValue("UninstallString", $"\"{exe}\" {Arg}");
            k.SetValue("QuietUninstallString", $"\"{exe}\" {Arg}");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            var size = new FileInfo(exe).Length / 1024;
            k.SetValue("EstimatedSize", (int)Math.Min(size, int.MaxValue), RegistryValueKind.DWord);
        }
        catch (Exception ex) { Log.Warn($"uninstall: не записаться в «Приложения» ({ex.Message})"); }
    }

    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); }
        catch (Exception ex) { Log.Warn($"uninstall: запись в «Приложениях» не убрать ({ex.Message})"); }
    }

    /// <summary>
    /// После выхода программы удалить exe (и, если нужно, настройки). Отдельный процесс из временной папки,
    /// выведенный из-под Планировщика (иначе тот закроет его вместе с программой). До 60 попыток раз в секунду:
    /// антивирус может ненадолго занять exe.
    /// </summary>
    public static void DeleteAfterExit(bool wipeSettings)
    {
        var exe = Paths.Exe;
        var dir = Path.GetDirectoryName(exe) ?? "";
        var temp = Path.GetTempPath();
        var script = Path.Combine(temp, $"{Paths.AppName}_uninstall.cmd");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"set \"EXE={exe}\"");
        sb.AppendLine("for /l %%i in (1,1,60) do (");
        sb.AppendLine("  del /f /q \"%EXE%\" >nul 2>&1");
        sb.AppendLine("  if not exist \"%EXE%\" goto done");
        sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
        sb.AppendLine(")");
        sb.AppendLine(":done");
        // браузерные процессы мини-браузера отпускают свои файлы не сразу — несколько попыток
        sb.AppendLine("for /l %%i in (1,1,15) do (");
        sb.AppendLine($"  rd /s /q \"{Paths.LocalData}\" >nul 2>&1");
        sb.AppendLine($"  if not exist \"{Paths.LocalData}\" goto data");
        sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
        sb.AppendLine(")");
        sb.AppendLine(":data");
        sb.AppendLine($"rd /s /q \"{Paths.LocalData}\" >nul 2>&1");
        sb.AppendLine($"rd /s /q \"{Paths.SharedData}\" >nul 2>&1");
        sb.AppendLine(wipeSettings
            ? $"rd /s /q \"{Paths.AppData}\" >nul 2>&1"
            : $"rd /s /q \"{Path.Combine(Paths.AppData, "logs")}\" >nul 2>&1");
        sb.AppendLine($"rd \"{dir}\" >nul 2>&1"); // папку программы — только если она пустая
        sb.AppendLine($"if exist \"%EXE%\" echo Не удалось удалить: %EXE% > \"{Path.Combine(temp, Paths.AppName + "_uninstall.txt")}\"");
        sb.AppendLine("del \"%~f0\"");
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

        var cmd = $"\"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe\" /c \"\"{script}\"\"";
        if (!Spawn(cmd, temp, CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB) && !Spawn(cmd, temp, CREATE_NO_WINDOW))
            Log.Warn("uninstall: не запустить удаление файлов");
    }

    private static bool Spawn(string commandLine, string cwd, uint flags)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var line = new StringBuilder(commandLine);
        if (!CreateProcessW(null, line, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, cwd, ref si, out var pi))
        {
            Log.Warn($"uninstall: CreateProcess {flags:X}: {Marshal.GetLastWin32Error()}");
            return false;
        }
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return true;
    }

    private const uint CREATE_NO_WINDOW = 0x08000000, CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmdLine, IntPtr procAttr, IntPtr threadAttr,
        bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
