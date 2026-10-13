using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace DesktopPanel.UI.Theme;

/// <summary>
/// Механизм композиции Windows (Windows.UI.Composition) для обычных окон: один Compositor на поток интерфейса.
/// То, что нарисовано через него (размытие, скругление, тень, обрезка), собирает сама Windows вместе с экраном —
/// без перерисовки окна программой и без пересылки картинки, с частотой монитора.
/// Нужна очередь DispatcherQueue на потоке (создаётся здесь один раз); изменения уходят в Windows сами,
/// когда поток возвращается к обработке сообщений.
/// </summary>
internal static class Comp
{
    private static Compositor? _compositor;
    private static IntPtr _queueController;

    public static Compositor Compositor
    {
        get
        {
            if (_compositor is not null) return _compositor;
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
            {
                var o = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 0 }; // текущий поток
                Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(o, out _queueController));
            }
            _compositor = new Compositor();
            return _compositor;
        }
    }

    /// <summary>Корень дерева композиции окна hwnd (над его обычным содержимым — topmost, иначе под ним).</summary>
    public static unsafe DesktopWindowTarget CreateTarget(IntPtr hwnd, bool topmost)
    {
        var unk = ((IWinRTObject)Compositor).NativeObject.ThisPtr;
        var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"); // ICompositorDesktopInterop
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref iid, out var interop));
        try
        {
            var vtbl = *(IntPtr**)interop;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)vtbl[3];
            IntPtr raw;
            Marshal.ThrowExceptionForHR(create(interop, hwnd, topmost ? 1 : 0, &raw));
            try
            {
                return DesktopWindowTarget.FromAbi(raw);
            }
            finally
            {
                Marshal.Release(raw);
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size, ThreadType, ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
}
