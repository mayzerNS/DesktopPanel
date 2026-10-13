using System.Runtime.InteropServices;

namespace DesktopPanel.Features.Desktop;

/// <summary>
/// Самый большой размер картинки внутри значка: у .ico — из заголовка файла, у exe/dll — из ресурса RT_GROUP_ICON.
/// null — не удалось узнать (тогда берём значок нужного размера как есть).
/// </summary>
internal static class IconSizes
{
    public static int? Largest(string file, int index)
    {
        try
        {
            return System.IO.Path.GetExtension(file).Equals(".ico", StringComparison.OrdinalIgnoreCase)
                ? FromIco(file)
                : FromModule(file, index);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>ICONDIR: reserved, type, count, затем записи по 16 байт (ширина 0 = 256).</summary>
    private static int? FromIco(string file)
    {
        using var fs = File.OpenRead(file);
        using var br = new BinaryReader(fs);
        if (br.ReadUInt16() != 0 || br.ReadUInt16() != 1) return null;
        int count = br.ReadUInt16(), best = 0;
        for (var i = 0; i < count; i++)
        {
            var w = br.ReadByte();
            br.ReadBytes(15);
            best = Math.Max(best, w == 0 ? 256 : w);
        }
        return best > 0 ? best : null;
    }

    /// <summary>Группа значков в ресурсах модуля: index ≥ 0 — по порядку, &lt; 0 — по номеру ресурса.</summary>
    private static int? FromModule(string file, int index)
    {
        var h = LoadLibraryEx(file, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (h == IntPtr.Zero) return null;
        try
        {
            IntPtr found = IntPtr.Zero;
            var n = 0;
            EnumResourceNames(h, RT_GROUP_ICON, (_, _, name, _) =>
            {
                var match = index >= 0 ? n == index : name == (IntPtr)(-index);
                n++;
                if (!match) return true;
                found = FindResource(h, name, RT_GROUP_ICON);
                return false;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return null;
            var data = LockResource(LoadResource(h, found));
            var size = (int)SizeofResource(h, found);
            if (data == IntPtr.Zero || size < 6) return null;
            // GRPICONDIR: reserved, type, count, записи по 14 байт (ширина 0 = 256)
            int count = Marshal.ReadInt16(data, 4), best = 0;
            for (var i = 0; i < count && 6 + i * 14 < size; i++)
            {
                var w = Marshal.ReadByte(data, 6 + i * 14);
                best = Math.Max(best, w == 0 ? 256 : w);
            }
            return best > 0 ? best : null;
        }
        finally
        {
            FreeLibrary(h);
        }
    }

    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x2, LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;
    private static readonly IntPtr RT_GROUP_ICON = (IntPtr)14;

    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc proc, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr res);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr data);

    [DllImport("kernel32.dll")]
    private static extern uint SizeofResource(IntPtr module, IntPtr res);
}
