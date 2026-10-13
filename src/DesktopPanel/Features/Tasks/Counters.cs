using System.Runtime.InteropServices;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Features.Tasks;

/// <summary>Общие счётчики Windows (PDH): загрузка ЦП, диска, сеть по всем адаптерам. Имена английские — работают на русской Windows.</summary>
internal sealed class Pdh : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();

    public Pdh()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
            {
                _query = IntPtr.Zero;
                return;
            }
            foreach (var (key, path) in new[]
                     {
                         ("cpu", @"\Processor Information(_Total)\% Processor Utility"),
                         ("cpu2", @"\Processor(_Total)\% Processor Time"),
                         ("disk", @"\PhysicalDisk(_Total)\% Idle Time"),
                         ("net", @"\Network Interface(*)\Bytes Total/sec"),
                     })
            {
                if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var h) == 0) _counters[key] = h;
            }
            PdhCollectQueryData(_query);
        }
        catch (Exception ex)
        {
            Log.Warn($"tasks: счётчики Windows недоступны ({ex.Message})");
            _query = IntPtr.Zero;
        }
    }

    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    public double? Value(string key)
    {
        if (!_counters.TryGetValue(key, out var h)) return null;
        if (PdhGetFormattedCounterValue(h, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, IntPtr.Zero, out var v) != 0 || v.CStatus > 1)
            return null;
        return v.doubleValue;
    }

    /// <summary>Сумма по всем экземплярам (например, по всем сетевым адаптерам).</summary>
    public double? Total(string key)
    {
        if (!_counters.TryGetValue(key, out var h)) return null;
        uint size = 0, count = 0;
        var r = PdhGetFormattedCounterArrayW(h, PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
        if ((uint)r != PDH_MORE_DATA || size == 0) return null;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(h, PDH_FMT_DOUBLE, ref size, ref count, buf) != 0) return null;
            double sum = 0;
            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (var i = 0; i < count; i++)
            {
                var it = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * itemSize);
                if (it.FmtValue.CStatus <= 1) sum += it.FmtValue.doubleValue;
            }
            return sum;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue; // объединение long/double/… — нам нужен double (выравнивание 8 байт)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize,
        ref uint itemCount, IntPtr items);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}

/// <summary>
/// Сеть по процессам: байты по каждому TCP-соединению (Windows отдаёт их после включения сбора статистики
/// для соединения — SetPerTcpConnectionEStats). UDP (звонки, игры, QUIC) так не видно.
/// </summary>
internal sealed class TcpNet
{
    private readonly Dictionary<string, bool> _enabled = new();
    private readonly Dictionary<string, ulong> _last = new();
    private bool _ok = true;

    /// <summary>pid → байт с прошлого раза.</summary>
    public Dictionary<int, double> Sample()
    {
        var res = new Dictionary<int, double>();
        if (!_ok) return res;
        var alive = new HashSet<string>();
        try
        {
            foreach (var r in Table4())
            {
                if (r.state != TCP_ESTABLISHED || r.pid == 0 || Loopback4(r.raddr)) continue;
                var key = $"4|{r.laddr}|{r.lport}|{r.raddr}|{r.rport}";
                alive.Add(key);
                var row = new MIB_TCPROW { state = r.state, laddr = r.laddr, lport = r.lport, raddr = r.raddr, rport = r.rport };
                if (!Enabled(key, () => EnableStats4(ref row))) continue;
                if (Read4(ref row) is { } total) Count(res, key, (int)r.pid, total);
            }
            foreach (var r in Table6())
            {
                if (r.state != TCP_ESTABLISHED || r.pid == 0 || Loopback6(r.raddr)) continue;
                var key = $"6|{Convert.ToHexString(r.laddr)}|{r.lport}|{Convert.ToHexString(r.raddr)}|{r.rport}";
                alive.Add(key);
                var row = new MIB_TCP6ROW
                {
                    state = r.state, laddr = r.laddr, lscope = r.lscope, lport = r.lport,
                    raddr = r.raddr, rscope = r.rscope, rport = r.rport,
                };
                if (!Enabled(key, () => EnableStats6(ref row))) continue;
                if (Read6(ref row) is { } total) Count(res, key, (int)r.pid, total);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"tasks: сеть по процессам недоступна ({ex.Message})");
            _ok = false;
        }
        foreach (var k in _enabled.Keys.Where(k => !alive.Contains(k)).ToList()) _enabled.Remove(k);
        foreach (var k in _last.Keys.Where(k => !alive.Contains(k)).ToList()) _last.Remove(k);
        return res;
    }

    /// <summary>
    /// Включён ли сбор статистики для соединения (включаем один раз). Без прав администратора Windows его
    /// не включает, а чтение всё равно «удаётся» и отдаёт мусор (терабиты в секунду) — такие соединения не считаем.
    /// </summary>
    private bool Enabled(string key, Func<bool> enable)
    {
        if (!_enabled.TryGetValue(key, out var ok)) _enabled[key] = ok = enable();
        return ok;
    }

    private void Count(Dictionary<int, double> res, string key, int pid, ulong total)
    {
        // prev == 0 — статистика ещё не собиралась (или её выключили): первое ненулевое значение — это всё,
        // что прошло с начала соединения, а не за секунду. Его берём только как точку отсчёта.
        if (_last.TryGetValue(key, out var prev) && prev > 0 && total >= prev)
            res[pid] = res.GetValueOrDefault(pid) + (total - prev);
        _last[key] = total;
    }

    /// <summary>127.x.x.x — трафик внутри компьютера (локальные прокси, VPN-клиенты), не сеть.</summary>
    private static bool Loopback4(uint addr) => (addr & 0xFF) == 127;

    private static readonly byte[] Loopback6Addr = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };

    private static bool Loopback6(byte[] addr) => addr.AsSpan().SequenceEqual(Loopback6Addr);

    // ---------- таблицы соединений ----------
    private static List<MIB_TCPROW_OWNER_PID> Table4() => Table<MIB_TCPROW_OWNER_PID>(AF_INET);
    private static List<MIB_TCP6ROW_OWNER_PID> Table6() => Table<MIB_TCP6ROW_OWNER_PID>(AF_INET6);

    private static List<T> Table<T>(int af) where T : struct
    {
        var res = new List<T>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size == 0) return res;
        size += 4096;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return res;
            var n = Marshal.ReadInt32(buf);
            var rowSize = Marshal.SizeOf<T>();
            for (var i = 0; i < n; i++) res.Add(Marshal.PtrToStructure<T>(buf + 4 + i * rowSize));
        }
        finally { Marshal.FreeHGlobal(buf); }
        return res;
    }

    // ---------- статистика соединения ----------
    private static bool EnableStats4(ref MIB_TCPROW row)
    {
        var rw = new TCP_ESTATS_DATA_RW_v0 { EnableCollection = 1 };
        return SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, ref rw, 0,
            (uint)Marshal.SizeOf<TCP_ESTATS_DATA_RW_v0>(), 0) == 0;
    }

    private static bool EnableStats6(ref MIB_TCP6ROW row)
    {
        var rw = new TCP_ESTATS_DATA_RW_v0 { EnableCollection = 1 };
        return SetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, ref rw, 0,
            (uint)Marshal.SizeOf<TCP_ESTATS_DATA_RW_v0>(), 0) == 0;
    }

    private static ulong? Read4(ref MIB_TCPROW row)
    {
        var rod = new TCP_ESTATS_DATA_ROD_v0();
        var rc = GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0,
            ref rod, 0, (uint)Marshal.SizeOf<TCP_ESTATS_DATA_ROD_v0>());
        return rc == 0 ? rod.DataBytesIn + rod.DataBytesOut : null;
    }

    private static ulong? Read6(ref MIB_TCP6ROW row)
    {
        var rod = new TCP_ESTATS_DATA_ROD_v0();
        var rc = GetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0,
            ref rod, 0, (uint)Marshal.SizeOf<TCP_ESTATS_DATA_ROD_v0>());
        return rc == 0 ? rod.DataBytesIn + rod.DataBytesOut : null;
    }

    private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint TCP_ESTABLISHED = 5;
    private const int TcpConnectionEstatsData = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID { public uint state, laddr, lport, raddr, rport, pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW { public uint state, laddr, lport, raddr, rport; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] laddr;
        public uint lscope, lport;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] raddr;
        public uint rscope, rport, state, pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW
    {
        public uint state;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] laddr;
        public uint lscope, lport;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] raddr;
        public uint rscope, rport;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TCP_ESTATS_DATA_RW_v0 { public byte EnableCollection; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TCP_ESTATS_DATA_ROD_v0
    {
        public ulong DataBytesOut, DataSegsOut, DataBytesIn, DataSegsIn, SegsOut, SegsIn;
        public uint SoftErrors, SoftErrorReason, SndUna, SndNxt, SndMax;
        public ulong ThruBytesAcked;
        public uint RcvNxt;
        public ulong ThruBytesReceived;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, ref TCP_ESTATS_DATA_RW_v0 rw,
        uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcp6ConnectionEStats(ref MIB_TCP6ROW row, int type, ref TCP_ESTATS_DATA_RW_v0 rw,
        uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, IntPtr rw, uint rwVersion,
        uint rwSize, IntPtr ros, uint rosVersion, uint rosSize, ref TCP_ESTATS_DATA_ROD_v0 rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcp6ConnectionEStats(ref MIB_TCP6ROW row, int type, IntPtr rw, uint rwVersion,
        uint rwSize, IntPtr ros, uint rosVersion, uint rosSize, ref TCP_ESTATS_DATA_ROD_v0 rod, uint rodVersion, uint rodSize);
}
