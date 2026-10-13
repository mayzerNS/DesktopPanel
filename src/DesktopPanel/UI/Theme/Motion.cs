using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.UI.Theme;

/// <summary>
/// Общие настройки движения: ход (кривые и длительности, одни на всю программу — как у плиток рабочего стола),
/// предел частоты кадров и выключатель анимаций. Все анимации программы идут через FrameClock/Anim и подчиняются
/// этому пределу сразу, без перезапуска. Выключенные анимации — всё встаёт в конечное состояние в первом же кадре.
/// </summary>
public static class Motion
{
    public const int MinFps = 30, MaxFps = 360;

    /// <summary>
    /// Общий ход элементов интерфейса — как у плиток рабочего стола (BackEase, лёгкий перелёт 0,45): поиск, корзина в шапке,
    /// появление строк, кнопки. Длительность чуть короче хода плиток (504 мс), потому что путь у этих элементов короткий.
    /// </summary>
    public const double UiMs = 420;

    /// <summary>Отклик на наведение и нажатие: быстро появляется, медленнее гаснет.</summary>
    public const double HoverInMs = 110, HoverOutMs = 220, PressMs = 90;

    /// <summary>Шаг «лесенки» (строки появляются по очереди, как плитки «паровозиком») и предел всей очереди.</summary>
    public const double TrainStepMs = 26, TrainMaxMs = 240;

    private static readonly BackEase Ui = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });
    private static readonly BackEase Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 });
    private static readonly QuinticEase Damped = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    private static readonly CubicEase Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly CubicEase In = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    private static readonly QuadraticEase Quick = Frozen(new QuadraticEase { EasingMode = EasingMode.EaseOut });

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    public static BackEase UiEase() => Ui;

    /// <summary>Отскок кнопки после нажатия — чуть живее, чем ход плиток.</summary>
    public static BackEase SpringEase() => Spring;

    /// <summary>
    /// То же время, но с полным затуханием, без перелёта — для того, что упирается в соседей (поле поиска: с перелётом оно
    /// на миг становилось шире доступного места и раздвигало шапку, а при сворачивании — уже лупы, «блинчиком»).
    /// </summary>
    public static QuinticEase UiEaseDamped() => Damped;

    public static CubicEase EaseOut() => Out;
    public static CubicEase EaseIn() => In;
    public static QuadraticEase QuickEase() => Quick;

    // ---------- частота кадров и выключатель ----------

    private static int _hz = 60, _limit;
    private static bool _applied;

    /// <summary>Анимации включены (настройка «Выключить анимации» — false).</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>Частота кадров анимаций сейчас: предел из настроек, но не выше монитора.</summary>
    public static int Fps => _limit > 0 ? Math.Min(_limit, _hz) : _hz;

    /// <summary>Сколько миллисекунд между кадрами анимаций (по пределу).</summary>
    public static double FrameMs => 1000.0 / Fps;

    /// <summary>Частота монитора (Гц), запомненная при запуске и после смены режима экрана — без лишних запросов к системе.</summary>
    public static int MonitorFps => _hz;

    /// <summary>Миллисекунд между кадрами монитора.</summary>
    public static double VsyncMs => 1000.0 / _hz;

    /// <summary>Пропускать ли кадры монитора: предел заметно ниже его частоты.</summary>
    public static bool Limited => _limit > 0 && _limit < _hz - 1;

    /// <summary>
    /// Самое долгое время, которое засчитывается анимации за один из первых кадров (первые кадры после перестройки тяжёлые:
    /// без этого предела значки «прыгали» вперёд). Не меньше кадра по пределу — иначе на 30 кадрах/с анимация в начале шла медленно.
    /// </summary>
    public static double FirstFrameCapMs => Math.Max(8, FrameMs * 1.25);

    public static event Action? Changed;

    /// <summary>Самая высокая частота обновления среди режимов экрана по умолчанию (Гц), не меньше 60.</summary>
    public static int MonitorHz()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, -1, ref dm) && dm.dmDisplayFrequency >= 30) return dm.dmDisplayFrequency;
        }
        catch { }
        return 60;
    }

    /// <summary>
    /// Предел кадров (0 — как у монитора) и выключатель. Зовётся при запуске и при смене настроек — действует сразу.
    /// Анимации самого WPF (их почти не осталось: системное затухание подменю) получают предел один раз, при запуске.
    /// </summary>
    public static void Configure(int limit, bool enabled)
    {
        // частоту монитора спрашиваем только при запуске (и после смены режима экрана, RefreshMonitor): EnumDisplaySettings
        // медленный, а ползунок кадров зовёт это на каждый шаг
        if (!_applied) _hz = Math.Clamp(MonitorHz(), MinFps, MaxFps);
        _limit = limit > 0 ? Math.Clamp(limit, MinFps, MaxFps) : 0;
        var changed = Enabled != enabled;
        Enabled = enabled;
        try
        {
            if (!_applied)
            {
                Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata(Fps));
                _applied = true;
            }
        }
        catch (Exception ex) { Log.Warn($"motion: не задать частоту кадров WPF: {ex.Message}"); }
        Log.Info($"motion: анимации {(enabled ? $"{Fps} кадров/с (монитор {_hz} Гц)" : "выключены")}");
        if (changed) Anim.FinishAll();
        Changed?.Invoke();
    }

    /// <summary>Поменялся режим экрана — перечитать частоту монитора.</summary>
    public static void RefreshMonitor() => _hz = Math.Clamp(MonitorHz(), MinFps, MaxFps);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
}
