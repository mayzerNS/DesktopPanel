using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DesktopPanel.Core.Logging;
using DesktopPanel.Core.Native;

namespace DesktopPanel.Features.Tasks;

/// <summary>Один процесс в снимке.</summary>
public sealed record ProcInfo(int Pid, int ParentPid, string Exe, string Path, long Created,
    double Cpu, long Memory, double Disk, double Net);

/// <summary>Группа: программа со всеми её процессами (Steam + steamwebhelper…) или фоновый процесс.</summary>
public sealed record ProcGroup(string Key, bool IsApp, string Title, string Path, List<int> Pids);

/// <summary>Итоги по компьютеру: ЦП %, память %, занято/всего байт, диск %, сеть бит/с.</summary>
public sealed record Totals(double Cpu, int MemoryLoad, ulong MemoryUsed, ulong MemoryTotal, double Disk, double NetBits);

public sealed record ProcSnapshot(Dictionary<int, ProcInfo> Procs, Dictionary<string, ProcGroup> Groups, Totals Totals);

/// <summary>
/// Свой диспетчер задач: процессы и нагрузка через WinAPI.
/// Sample() раз в секунду возвращает снимок: процессы с ЦП/памятью/диском/сетью, группы «Приложения»
/// (у процесса есть окно на панели задач) и «Фоновые процессы», общая загрузка.
/// Системные процессы Windows (из папки Windows и служебные) не показываем — для них есть Диспетчер задач Windows.
/// Сеть по процессам — только TCP (так её отдаёт Windows без ETW), общая сеть — по всем адаптерам.
/// </summary>
public sealed class ProcMonitor : IDisposable
{
    private static readonly string WinDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system idle process", "system", "registry", "memory compression", "secure system", "smss.exe",
        "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe", "svchost.exe",
        "fontdrvhost.exe", "dwm.exe", "lsaiso.exe", "vmmem", "vmmemwsl",
    };

    /// <summary>Дочерние процессы Проводника — отдельные программы, не «часть» Проводника.</summary>
    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase) { "explorer.exe" };

    private readonly int _ncpu = Environment.ProcessorCount;
    private readonly Dictionary<int, (IntPtr Handle, long Created)> _handles = new();
    private readonly Dictionary<int, string> _paths = new();
    private readonly Dictionary<int, (long CpuTime, long DiskBytes)> _prev = new();
    private readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Pdh _pdh = new();
    private readonly TcpNet _net = new();
    private long _prevTicks;
    private (long Idle, long Busy)? _prevSys;

    public ProcSnapshot Sample()
    {
        var now = Stopwatch.GetTimestamp();
        double? dt = _prevTicks == 0 ? null : (now - _prevTicks) / (double)Stopwatch.Frequency;
        _prevTicks = now;

        var list = Snapshot();
        var live = list.Select(p => p.Pid).ToHashSet();
        foreach (var pid in _handles.Keys.Where(p => !live.Contains(p)).ToList()) Forget(pid);

        var net = _net.Sample();
        var procs = new Dictionary<int, ProcInfo>();
        foreach (var (pid, ppid, exe) in list)
        {
            if (pid is 0 or 4) continue;
            if (!_handles.ContainsKey(pid) && Open(pid) is { } opened) _handles[pid] = opened;
            if (_handles.TryGetValue(pid, out var hc))
            {
                // pid мог достаться новому процессу — тогда старая ручка «мёртвая»
                if (GetExitCodeProcess(hc.Handle, out var code) && code != STILL_ACTIVE)
                {
                    Forget(pid);
                    if (Open(pid) is { } again) _handles[pid] = again;
                }
            }

            long cpuT = 0, diskB = 0, mem = 0, created = 0;
            if (_handles.TryGetValue(pid, out var h))
            {
                created = h.Created;
                if (GetProcessTimes(h.Handle, out _, out _, out var k, out var u)) cpuT = k + u;
                if (GetProcessIoCounters(h.Handle, out var io)) diskB = (long)(io.ReadTransferCount + io.WriteTransferCount);
                var pm = new PMC_EX2 { cb = (uint)Marshal.SizeOf<PMC_EX2>() };
                if (K32GetProcessMemoryInfo(h.Handle, ref pm, pm.cb))
                    mem = (long)(pm.PrivateWorkingSetSize != UIntPtr.Zero ? pm.PrivateWorkingSetSize : pm.WorkingSetSize);
            }

            double cpu = 0, disk = 0;
            if (_prev.TryGetValue(pid, out var prev) && dt is { } d && d > 0)
            {
                cpu = Math.Max(0, (cpuT - prev.CpuTime) / (d * 1e7 * _ncpu) * 100);
                disk = Math.Max(0, diskB - prev.DiskBytes) / d;
            }
            _prev[pid] = (cpuT, diskB);

            var path = PathOf(pid, h.Handle);
            var netRate = dt is { } dd && dd > 0 && net.TryGetValue(pid, out var nb) ? nb / dd : 0;
            procs[pid] = new ProcInfo(pid, ppid, exe, path, created, Math.Min(cpu, 100), mem, disk, netRate);
        }
        return new ProcSnapshot(procs, Group(procs), TotalsNow(procs));
    }

    private void Forget(int pid)
    {
        if (_handles.Remove(pid, out var h)) CloseHandle(h.Handle);
        _paths.Remove(pid);
        _prev.Remove(pid);
    }

    private static (IntPtr, long)? Open(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        var created = GetProcessTimes(h, out var c, out _, out _, out _) ? c : 0;
        return (h, created);
    }

    private string PathOf(int pid, IntPtr h)
    {
        if (_paths.TryGetValue(pid, out var p)) return p;
        var path = "";
        if (h != IntPtr.Zero)
        {
            var sb = new StringBuilder(1024);
            var n = sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref n)) path = sb.ToString();
        }
        _paths[pid] = path;
        return path;
    }

    /// <summary>«Описание файла» из свойств exe — так Диспетчер задач называет программы (Firefox, Steam…).</summary>
    public string TitleOf(string path, string exe)
    {
        if (path.Length > 0 && _titles.TryGetValue(path, out var t)) return t;
        var title = "";
        try
        {
            if (path.Length > 0) title = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? "";
        }
        catch { }
        if (title.Length == 0) title = System.IO.Path.GetFileNameWithoutExtension(exe);
        if (path.Length > 0) _titles[path] = title;
        return title;
    }

    // ---------- группы ----------
    private static bool IsWindows(ProcInfo p)
    {
        if (SystemNames.Contains(p.Exe)) return true;
        return p.Path.Length == 0 || p.Path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, ProcGroup> Group(Dictionary<int, ProcInfo> procs)
    {
        var appExes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in AppWindowPids())
            if (procs.TryGetValue(pid, out var p)) appExes.TryAdd(p.Exe, pid);

        string? Owner(ProcInfo p)
        {
            var seen = new HashSet<int>();
            var cur = p;
            for (var i = 0; i < 32; i++)
            {
                if (!procs.TryGetValue(cur.ParentPid, out var par) || !seen.Add(par.Pid)) return null;
                if (par.Created != 0 && cur.Created != 0 && par.Created > cur.Created) return null; // pid переиспользован
                if (appExes.ContainsKey(par.Exe) && !ShellNames.Contains(par.Exe)) return par.Exe;
                cur = par;
            }
            return null;
        }

        var groups = new Dictionary<string, ProcGroup>();
        foreach (var p in procs.Values)
        {
            string key;
            bool app;
            if (appExes.ContainsKey(p.Exe)) (key, app) = ("app:" + p.Exe.ToLowerInvariant(), true);
            else if (Owner(p) is { } o) (key, app) = ("app:" + o.ToLowerInvariant(), true);
            else if (IsWindows(p)) continue;
            else (key, app) = ("bg:" + p.Exe.ToLowerInvariant(), false);

            if (!groups.TryGetValue(key, out var g))
            {
                var root = app && appExes.TryGetValue(key[4..], out var rp) && procs.TryGetValue(rp, out var r) ? r : p;
                g = new ProcGroup(key, app, TitleOf(root.Path, root.Exe), root.Path, new List<int>());
                groups[key] = g;
            }
            g.Pids.Add(p.Pid);
        }
        foreach (var g in groups.Values) g.Pids.Sort();
        return groups;
    }

    /// <summary>PID процессов, у которых есть «настоящее» окно (то, что видно на панели задач).</summary>
    private static HashSet<int> AppWindowPids()
    {
        var pids = new HashSet<int>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
                    return true;
                var ex = NativeMethods.ExStyle(hwnd);
                if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (ex & NativeMethods.WS_EX_APPWINDOW) == 0) return true;
                if (NativeMethods.GetWindowTextLength(hwnd) <= 0) return true;
                if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, 4) == 0
                    && cloaked != 0) return true;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (NativeMethods.ClassName(hwnd) == "ApplicationFrameWindow")
                {
                    // Store-приложения: окно держит ApplicationFrameHost, а программа — внутри
                    uint inner = 0;
                    var frame = pid;
                    NativeMethods.EnumChildWindows(hwnd, (child, _) =>
                    {
                        NativeMethods.GetWindowThreadProcessId(child, out var cp);
                        if (cp != 0 && cp != frame)
                        {
                            inner = cp;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                    if (inner != 0) pid = inner;
                }
                if (pid != 0) pids.Add((int)pid);
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    // ---------- итоги ----------
    private Totals TotalsNow(Dictionary<int, ProcInfo> procs)
    {
        _pdh.Collect();
        var cpu = _pdh.Value("cpu") ?? _pdh.Value("cpu2");
        if (cpu is null)
        {
            cpu = 0;
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var cur = (idle, kernel + user);
                if (_prevSys is { } ps)
                {
                    var di = cur.idle - ps.Idle;
                    var dall = cur.Item2 - ps.Busy;
                    cpu = dall > 0 ? (1 - di / (double)dall) * 100 : 0;
                }
                _prevSys = cur;
            }
        }
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        int load = 0;
        ulong used = 0, total = 0;
        if (GlobalMemoryStatusEx(ref ms))
        {
            load = (int)ms.dwMemoryLoad;
            total = ms.ullTotalPhys;
            used = ms.ullTotalPhys - ms.ullAvailPhys;
        }
        var idleDisk = _pdh.Value("disk");
        var disk = idleDisk is { } i ? Math.Clamp(100 - i, 0, 100) : 0;
        var netBytes = _pdh.Total("net") ?? procs.Values.Sum(p => p.Net);
        return new Totals(Math.Clamp(cpu.Value, 0, 100), load, used, total, disk, netBytes * 8);
    }

    // ---------- снять задачу и прочее ----------
    /// <summary>Немедленно завершить процессы. Возвращает, сколько удалось.</summary>
    public static int Terminate(IEnumerable<int> pids)
    {
        var done = 0;
        foreach (var pid in pids)
        {
            var h = OpenProcess(PROCESS_TERMINATE, false, pid);
            if (h == IntPtr.Zero) continue;
            try { if (TerminateProcess(h, 1)) done++; }
            finally { CloseHandle(h); }
        }
        return done;
    }

    /// <summary>Процесс и все его потомки (по снимку).</summary>
    public static List<int> Tree(ProcSnapshot snap, int root)
    {
        var res = new List<int> { root };
        for (var i = 0; i < res.Count; i++)
        {
            var parent = res[i];
            if (!snap.Procs.TryGetValue(parent, out var pp)) continue;
            foreach (var p in snap.Procs.Values)
                if (p.ParentPid == parent && !res.Contains(p.Pid) && (p.Created == 0 || pp.Created == 0 || p.Created >= pp.Created))
                    res.Add(p.Pid);
        }
        return res;
    }

    public static bool SetPriority(int pid, ProcessPriorityClass priority)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.PriorityClass = priority;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"tasks: приоритет {pid}: {ex.Message}");
            return false;
        }
    }

    public static ProcessPriorityClass? GetPriority(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.PriorityClass;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        foreach (var h in _handles.Values) CloseHandle(h.Handle);
        _handles.Clear();
        _pdh.Dispose();
    }

    // ---------- список процессов ----------
    private static List<(int Pid, int ParentPid, string Exe)> Snapshot()
    {
        var res = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return res;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            var ok = Process32FirstW(snap, ref e);
            while (ok)
            {
                res.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile));
                ok = Process32NextW(snap, ref e);
            }
        }
        finally { CloseHandle(snap); }
        return res;
    }

    // ---------- WinAPI ----------
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_TERMINATE = 0x0001;
    private const uint TH32CS_SNAPPROCESS = 2, STILL_ACTIVE = 259;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
            ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    /// <summary>PROCESS_MEMORY_COUNTERS_EX2 (Windows 10 1803+).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PMC_EX2
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage,
            PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual,
            ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS io);

    [DllImport("kernel32.dll")]
    private static extern bool K32GetProcessMemoryInfo(IntPtr h, ref PMC_EX2 pmc, uint cb);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX ms);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr h, uint code);
}
