using DesktopPanel.Core.Logging;

namespace DesktopPanel.Core;

/// <summary>
/// Один экземпляр программы. Второй запуск не открывает вторую копию, а просит первую показать панель.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\DesktopPanel.SingleInstance";
    private const string ShowEventName = @"Local\DesktopPanel.Show";
    private const string UninstallEventName = @"Local\DesktopPanel.Uninstall";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly EventWaitHandle _uninstallEvent;
    private RegisteredWaitHandle? _waitUninstall;
    private readonly bool _owner;
    private RegisteredWaitHandle? _wait;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _owner);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _uninstallEvent = new EventWaitHandle(false, EventResetMode.AutoReset, UninstallEventName);
    }

    /// <summary>true — мы первая копия; false — программа уже запущена.</summary>
    public bool IsFirst => _owner;

    /// <summary>Попросить уже запущенную копию показать панель.</summary>
    public void SignalFirst(bool uninstall = false)
    {
        Log.Info(uninstall ? "single: программа уже запущена — прошу её показать удаление"
                           : "single: программа уже запущена — прошу её показать панель");
        (uninstall ? _uninstallEvent : _showEvent).Set();
    }

    /// <summary>Слушать просьбы второй копии. onShow вызывается в фоновом потоке.</summary>
    public void Listen(Action onShow, Action onUninstall)
    {
        _waitUninstall = ThreadPool.RegisterWaitForSingleObject(_uninstallEvent, (_, _) => onUninstall(), null,
            Timeout.Infinite, executeOnlyOnce: false);
        _wait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => onShow(), null, Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _waitUninstall?.Unregister(null);
        if (_owner)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
        _showEvent.Dispose();
        _uninstallEvent.Dispose();
    }
}
