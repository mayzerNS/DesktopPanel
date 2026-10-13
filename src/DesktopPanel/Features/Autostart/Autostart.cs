using System.Diagnostics;
using System.Security;
using System.Text;
using DesktopPanel.Core;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Features.Autostart;

/// <summary>
/// Автозапуск при входе в Windows через Планировщик заданий (schtasks.exe — штатная утилита Windows).
/// Задание запускает программу сразу с правами администратора, поэтому окна UAC при входе нет.
/// </summary>
public static class Autostart
{
    private const string TaskName = Paths.AppName;

    public static bool IsEnabled() => Run("/Query", "/TN", TaskName) == 0;

    public static void Enable()
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "DesktopPanel.task.xml");
        try
        {
            File.WriteAllText(xmlPath, TaskXml(), Encoding.Unicode); // schtasks ждёт UTF-16
            var code = Run("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");
            if (code != 0) Log.Warn($"autostart: schtasks /Create вернул {code}");
            else Log.Info("autostart: включён");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    public static void Disable()
    {
        var code = Run("/Delete", "/TN", TaskName, "/F");
        Log.Info(code == 0 ? "autostart: выключен" : $"autostart: задания не было ({code})");
    }

    private static string TaskXml()
    {
        var user = SecurityElement.Escape($@"{Environment.UserDomainName}\{Environment.UserName}");
        var exe = SecurityElement.Escape(Paths.Exe);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>DesktopPanel — панель рабочего стола (Win+C)</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command></Exec></Actions>
            </Task>
            """;
    }

    private static int Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error("autostart: schtasks не запустился", ex);
            return -1;
        }
    }
}
