using System.Runtime.InteropServices;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Features.Display;

/// <summary>
/// HDR на мониторах — через штатный DisplayConfig API Windows (то же, что переключатель в «Параметрах → Дисплей»).
/// Windows 11 24H2+ — новый вызов (SET_HDR_STATE), раньше — SET_ADVANCED_COLOR_STATE.
/// </summary>
public static class Hdr
{
    public readonly record struct Display(LUID Adapter, uint TargetId, bool Supported, bool Enabled, bool NewApi);

    /// <summary>Все активные мониторы и их HDR.</summary>
    public static List<Display> Displays()
    {
        var res = new List<Display>();
        PATH_INFO[] paths = Array.Empty<PATH_INFO>();
        uint np = 0, nm = 0;
        var ok = false;
        for (var attempt = 0; attempt < 3 && !ok; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return res;
            paths = new PATH_INFO[np];
            var modes = new MODE_INFO[nm];
            var rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (rc == 0) ok = true;
            else if (rc != ERROR_INSUFFICIENT_BUFFER) return res;
        }
        if (!ok) return res;

        var seen = new HashSet<(uint, int, uint)>();
        for (var i = 0; i < np; i++)
        {
            var t = paths[i].target;
            if (!seen.Add((t.adapterId.LowPart, t.adapterId.HighPart, t.id))) continue;

            var i2 = new COLOR_INFO2();
            i2.header = Header(GET_ADVANCED_COLOR_INFO_2, Marshal.SizeOf<COLOR_INFO2>(), t.adapterId, t.id);
            if (DisplayConfigGetDeviceInfo(ref i2) == 0)
            {
                var v = i2.value;
                res.Add(new Display(t.adapterId, t.id, (v & 0x10) != 0, (v & 0x20) != 0, true));
                continue;
            }
            var i1 = new COLOR_INFO();
            i1.header = Header(GET_ADVANCED_COLOR_INFO, Marshal.SizeOf<COLOR_INFO>(), t.adapterId, t.id);
            if (DisplayConfigGetDeviceInfo(ref i1) == 0)
            {
                var v = i1.value;
                // поддерживается; не «принудительный широкий цвет» (SDR с ACM) и не запрещено политикой
                var hdr = (v & 1) != 0 && (v & 0xC) == 0;
                res.Add(new Display(t.adapterId, t.id, hdr, hdr && (v & 2) != 0, false));
            }
        }
        return res;
    }

    /// <summary>(есть ли мониторы с HDR, включён ли он хотя бы на одном).</summary>
    public static (bool Supported, bool On) State()
    {
        try
        {
            var ds = Displays().Where(d => d.Supported).ToList();
            return (ds.Count > 0, ds.Any(d => d.Enabled));
        }
        catch (Exception ex)
        {
            Log.Warn($"hdr: не узнать состояние ({ex.Message})");
            return (false, false);
        }
    }

    /// <summary>Включить или выключить HDR на всех мониторах, которые его умеют.</summary>
    public static void Set(bool on)
    {
        foreach (var d in Displays())
        {
            if (!d.Supported || d.Enabled == on) continue;
            var s = new SET_STATE
            {
                header = Header(d.NewApi ? SET_HDR_STATE : SET_ADVANCED_COLOR_STATE, Marshal.SizeOf<SET_STATE>(),
                    d.Adapter, d.TargetId),
                value = on ? 1u : 0u,
            };
            var rc = DisplayConfigSetDeviceInfo(ref s);
            Log.Info($"hdr: {(on ? "вкл" : "выкл")} монитор {d.TargetId} → {rc}");
        }
    }

    // ---------- яркость SDR-контента в режиме HDR ----------
    // Как ползунок «Яркость содержимого SDR» в «Параметры → Дисплей → HDR»: 0..100 % = 80..480 нит.
    // Windows хранит «уровень белого» в тысячных от 80 нит: 1000 = 80 нит, 6000 = 480 нит (шаг ползунка — 50).

    /// <summary>Яркость SDR 0..100 (по первому монитору с включённым HDR) или null, если HDR нигде не включён.</summary>
    public static int? SdrBrightness()
    {
        try
        {
            foreach (var d in Displays().Where(d => d.Enabled))
            {
                var w = new SDR_WHITE_LEVEL { header = Header(GET_SDR_WHITE_LEVEL, Marshal.SizeOf<SDR_WHITE_LEVEL>(), d.Adapter, d.TargetId) };
                if (DisplayConfigGetDeviceInfo(ref w) == 0 && w.SDRWhiteLevel >= 1000)
                    return (int)Math.Clamp(Math.Round((w.SDRWhiteLevel - 1000) / 50.0), 0, 100);
            }
        }
        catch (Exception ex) { Log.Warn($"hdr: яркость SDR не прочитать ({ex.Message})"); }
        return null;
    }

    /// <summary>Мониторы с включённым HDR — список снимаем один раз, пока открыт ползунок (перечисление мониторов медленное).</summary>
    public static List<Display> SdrTargets() => Displays().Where(d => d.Enabled).ToList();

    /// <summary>Поставить яркость SDR 0..100 на всех мониторах с включённым HDR.</summary>
    public static void SetSdrBrightness(int percent) => SetSdrBrightness(percent, SdrTargets());

    /// <summary>То же на заранее найденных мониторах: на каждый шаг ползунка остаётся один вызов Windows.</summary>
    public static void SetSdrBrightness(int percent, List<Display> targets, bool final = true)
    {
        var level = 1000u + (uint)Math.Clamp(percent, 0, 100) * 50u;
        foreach (var d in targets)
        {
            var s = new SET_SDR_WHITE_LEVEL
            {
                header = Header(SET_SDR_WHITE_LEVEL_TYPE, Marshal.SizeOf<SET_SDR_WHITE_LEVEL>(), d.Adapter, d.TargetId),
                SDRWhiteLevel = level,
                finalValue = (byte)(final ? 1 : 0), // как в «Параметрах»: пока ползунок тянут — промежуточные значения
            };
            var rc = DisplayConfigSetDeviceInfo(ref s);
            if (rc != 0) Log.Warn($"hdr: яркость SDR монитор {d.TargetId} → {rc}");
        }
    }

    // ---------- WinAPI ----------
    private const uint GET_SDR_WHITE_LEVEL = 11;
    /// <summary>Недокументированный тип (−18): его вызывают сами «Параметры» Windows при сдвиге ползунка.</summary>
    private const uint SET_SDR_WHITE_LEVEL_TYPE = 0xFFFFFFEE;

    [StructLayout(LayoutKind.Sequential)]
    private struct SDR_WHITE_LEVEL { public DEVICE_INFO_HEADER header; public uint SDRWhiteLevel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SET_SDR_WHITE_LEVEL { public DEVICE_INFO_HEADER header; public uint SDRWhiteLevel; public byte finalValue; }

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SDR_WHITE_LEVEL info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref SET_SDR_WHITE_LEVEL info);

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint GET_ADVANCED_COLOR_INFO = 9, SET_ADVANCED_COLOR_STATE = 10,
        GET_ADVANCED_COLOR_INFO_2 = 15, SET_HDR_STATE = 16;

    private static DEVICE_INFO_HEADER Header(uint type, int size, LUID adapter, uint id) =>
        new() { type = type, size = (uint)size, adapterId = adapter, id = id };

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE_INFO source; public PATH_TARGET_INFO target; public uint flags; }

    /// <summary>DISPLAYCONFIG_MODE_INFO — 64 байта, содержимое нам не нужно.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct MODE_INFO { public uint infoType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct COLOR_INFO { public DEVICE_INFO_HEADER header; public uint value, colorEncoding, bitsPerColorChannel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct COLOR_INFO2
    {
        public DEVICE_INFO_HEADER header;
        public uint value, colorEncoding, bitsPerColorChannel, activeColorMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SET_STATE { public DEVICE_INFO_HEADER header; public uint value; }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths,
        ref uint numModes, [Out] MODE_INFO[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO2 info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref SET_STATE info);
}
