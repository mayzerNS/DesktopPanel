using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.Zapret;

/// <summary>Запуск и остановка winws.exe, драйвер WinDivert, служба Windows «zapret».</summary>
public static class ZapretRuntime
{
    private const string WinwsName = "winws";

    public static bool IsRunning => Shell.IsRunning(WinwsName);

    // ---------- запуск winws.exe ----------
    /// <summary>
    /// Стратегии с --dpi-desync-fooling=ts без меток времени TCP не работают (service.bat тоже их включает).
    /// </summary>
    public static void EnableTcpTimestamps() =>
        Shell.Run("netsh", "interface", "tcp", "set", "global", "timestamps=enabled");

    /// <summary>
    /// Запустить winws.exe без окна, вывод — в winws.log. true, если он пережил первую секунду
    /// (кривая стратегия или нет файла — он падает сразу).
    /// </summary>
    public static bool Launch(string root, string bat)
    {
        var cmd = WinwsCommand.Full(root, bat);
        Log.Info($"zapret: запуск {cmd}");
        Directory.CreateDirectory(Path.GetDirectoryName(ZapretPaths.WinwsLog)!);

        // Вывод пишем прямо в файл (наследуемый дескриптор), а не через канал: если программа закроется,
        // winws.exe продолжит работать и не упрётся в оборванный канал.
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        using var log = CreateFileW(ZapretPaths.WinwsLog, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, ref sa,
            CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (log.IsInvalid) throw new ZapretException(Loc.T("zapret.err.launch", new Win32Exception().Message));

        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESTDHANDLES,
            hStdOutput = log.DangerousGetHandle(),
            hStdError = log.DangerousGetHandle(),
        };
        var line = new StringBuilder(cmd);
        if (!CreateProcessW(null, line, IntPtr.Zero, IntPtr.Zero, true, CREATE_NO_WINDOW, IntPtr.Zero,
                Path.Combine(root, "bin"), ref si, out var pi))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_VIRUS_INFECTED) throw new ZapretException(Loc.T("zapret.err.antivirus"));
            throw new ZapretException(Loc.T("zapret.err.launch", new Win32Exception(err).Message));
        }
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);

        for (var i = 0; i < 20 && !IsRunning; i++) Thread.Sleep(100);
        Thread.Sleep(1000);
        return IsRunning;
    }

    /// <summary>Запустить стратегию; если winws.exe сразу закрылся — исключение с понятной причиной.</summary>
    public static void Start(string root, string bat)
    {
        ZapretFiles.EnsureUserLists(root);
        EnableTcpTimestamps();
        if (!Launch(root, bat)) throw new ZapretException(ExplainCrash());
        // пока обхода не было, Windows запомнила «имя не найдено» для заблокированных сайтов — забываем
        Shell.Run("ipconfig", "/flushdns");
    }

    private static readonly Dictionary<int, string> Codes = new()
    {
        [2] = "zapret.err.code2",
        [3] = "zapret.err.code3",
        [5] = "zapret.err.code5",
        [87] = "zapret.err.code87",
        [577] = "zapret.err.code577",
        [654] = "zapret.err.code654",
        [1058] = "zapret.err.code1058",
        [1275] = "zapret.err.code1275",
    };

    /// <summary>Хвост winws.log (последние непустые строки).</summary>
    public static List<string> LogTail(int count = 6)
    {
        try
        {
            using var fs = new FileStream(ZapretPaths.WinwsLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var rd = new StreamReader(fs, Encoding.UTF8);
            return rd.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(count).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Текст системной ошибки (winws пишет его словами, без кода) → код из Codes.</summary>
    private static readonly (string Text, int Code)[] Texts =
    {
        ("cannot find the path", 3),
        ("cannot find the file", 2),
        ("access is denied", 5),
        ("parameter is incorrect", 87),
        ("digital signature", 577),
        ("blocked from loading", 1275),
    };

    /// <summary>
    /// Понятная причина, по которой winws.exe закрылся, — по его логу. Смотрим только последнюю строку:
    /// выше идёт обычная загрузка списков, и имя файла оттуда — не причина.
    /// </summary>
    public static string ExplainCrash()
    {
        var tail = LogTail();
        if (tail.Count == 0) return Loc.T("zapret.err.closed");
        var last = tail[^1];
        var text = last.ToLowerInvariant();
        var m = Regex.Match(text, @"(?:error|code)[^\d]{0,12}(\d{1,5})");
        if (m.Success && Codes.TryGetValue(int.Parse(m.Groups[1].Value), out var key))
            return Loc.T(key);
        foreach (var (t, code) in Texts)
            if (text.Contains(t) && Codes.TryGetValue(code, out key))
                return Loc.T(key);
        m = Regex.Match(last, @"([\w\-]+\.(?:txt|bin))");
        if (m.Success && new[] { "could not", "cannot", "can't", "failed", "error" }.Any(text.Contains))
            return Loc.T("zapret.err.read_file", m.Groups[1].Value);
        return Loc.T("zapret.err.closed_with", last);
    }

    // ---------- остановка ----------
    public static void Kill()
    {
        foreach (var p in Process.GetProcessesByName(WinwsName))
        {
            try { p.Kill(); }
            catch (Exception ex) { Log.Warn($"zapret: не остановить winws.exe ({ex.Message})"); }
            finally { p.Dispose(); }
        }
        for (var i = 0; i < 30 && IsRunning; i++) Thread.Sleep(100);
        if (IsRunning) Shell.Run("taskkill", "/F", "/IM", "winws.exe");
    }

    /// <summary>Выгрузить драйвер WinDivert (если служба zapret не установлена — её драйвер не трогаем).</summary>
    public static void StopWinDivert()
    {
        if (ServiceState() is not null) return;
        foreach (var svc in new[] { "WinDivert", "WinDivert14" })
        {
            Shell.Run("sc", "stop", svc);
            Shell.Run("sc", "delete", svc);
        }
    }

    /// <summary>Остановить то, что запустили сами.</summary>
    public static void Stop()
    {
        Kill();
        StopWinDivert();
    }

    // ---------- служба Windows ----------
    internal static readonly Regex StateRe =
        new(@":\s*\d+\s+(RUNNING|STOPPED|START_PENDING|STOP_PENDING|PAUSED|PAUSE_PENDING|CONTINUE_PENDING)\b");

    /// <summary>null — службы zapret нет; иначе RUNNING / STOPPED / …</summary>
    public static string? ServiceState()
    {
        var r = Shell.Run("sc", "query", ZapretPaths.ServiceName);
        if (!r.Ok) return null;
        var m = StateRe.Match(r.Output);
        return m.Success ? m.Groups[1].Value : "UNKNOWN";
    }

    private static string ServiceKey => $@"SYSTEM\CurrentControlSet\Services\{ZapretPaths.ServiceName}";

    /// <summary>Какая стратегия записана в службе (её пишет и service.bat Zapret).</summary>
    public static string? ServiceStrategy()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ServiceKey);
            return k?.GetValue("zapret-discord-youtube") as string;
        }
        catch { return null; }
    }

    public static void StartService() => Shell.Run("sc", "start", ZapretPaths.ServiceName);
    public static void StopService() => Shell.Run("sc", "stop", ZapretPaths.ServiceName);

    public static void InstallService(string root, string bat)
    {
        var cmd = WinwsCommand.Full(root, bat);
        ZapretFiles.EnsureUserLists(root);
        EnableTcpTimestamps();
        Shell.Run("sc", "stop", ZapretPaths.ServiceName);
        Shell.Run("sc", "delete", ZapretPaths.ServiceName);
        Kill();
        StopWinDivert();
        Thread.Sleep(500);

        var r = Shell.Run("sc", "create", ZapretPaths.ServiceName, "binPath=", cmd, "DisplayName=", "zapret", "start=", "auto");
        if (!r.Ok)
        {
            var last = r.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "?";
            throw new ZapretException(Loc.T("zapret.err.service_create", last));
        }
        Shell.Run("sc", "description", ZapretPaths.ServiceName, "Zapret DPI bypass software");
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: true);
            k?.SetValue("zapret-discord-youtube", Path.GetFileNameWithoutExtension(bat), RegistryValueKind.String);
        }
        catch (Exception ex) { Log.Warn($"zapret: не записать стратегию службы ({ex.Message})"); }

        StartService();
        Thread.Sleep(1500);
        if (!IsRunning) throw new ZapretException(Loc.T("zapret.err.service_no_winws"));
        Log.Info($"zapret: служба установлена ({bat})");
    }

    public static void RemoveService()
    {
        Shell.Run("sc", "stop", ZapretPaths.ServiceName);
        Shell.Run("sc", "delete", ZapretPaths.ServiceName);
        Kill();
        Thread.Sleep(300);
        StopWinDivert();
        Log.Info("zapret: служба удалена");
    }

    // ---------- WinAPI ----------
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint CREATE_ALWAYS = 2, FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESTDHANDLES = 0x100;
    private const int ERROR_VIRUS_INFECTED = 225;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

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
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmdLine, IntPtr procAttr, IntPtr threadAttr,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFO si,
        out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);
}
