using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPanel.Features.Audio;
using DesktopPanel.UI.Theme;

namespace DesktopPanel.UI.Overlay;

/// <summary>
/// Визуализация музыки за «стеклом» панели: полосы цвета акцента от края до края окна, зеркально от середины (низкие
/// частоты — в середине, высокие — к краям), растут снизу. Лежит под затемнением.
/// Дёшево: полосы рисуются в крошечную картинку (144×48 точек — по точке на полосу и на промежуток) прямо в память,
/// а WPF растягивает её на всё окно со сглаживанием — края полос мягкие, как за стеклом. Раньше полосы рисовались
/// векторами во всё окно с эффектом размытия каждый кадр: ~40 % ядра, сборка мусора каждые 2 с, кадры рендера по
/// 30–46 мс — анимации сетки (смыкание после корзины) шли рывками. Картинка обновляется каждый кадр (она крошечная).
/// Работает, только пока её видно (панель открыта, не в уголке) и включена в настройках: тогда же идёт захват звука;
/// спрятали — захват остановлен, кадры не считаются. Тишина — полосы плавно опускаются, замирают, и визуализация
/// засыпает: отписывается от кадров (иначе WPF рисовал бы всё окно с частотой монитора — на 200 Гц это ~25 % ядра
/// при открытой панели без единого звука), а звук после тишины будит её (AudioSpectrum.SoundStarted).
/// </summary>
public sealed class MusicBars : FrameworkElement
{
    private const int PerSide = 36;          // полос в каждой половине
    private const double MaxShare = 0.525;   // самая высокая полоса — доля высоты окна
    private const int W = PerSide * 2 * 2, H = 48; // картинка: полоса и промежуток — по точке

    private readonly AudioSpectrum _audio = new();
    private readonly float[] _raw = new float[PerSide];
    private readonly double[] _soft = new double[PerSide];
    private readonly double[] _level = new double[PerSide];
    private readonly int[] _px = new int[W * H];
    private readonly WriteableBitmap _bmp = new(W, H, 96, 96, PixelFormats.Pbgra32, null);
    private bool _active, _ticking, _enabled = true, _wasAny;
    private int _wakeQueued;
    private double _lastMs;
    // захват отвалился (сменили устройство вывода) или не поднялся — пробуем снова, пока визуализация на экране
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(2) };

    public MusicBars()
    {
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        // края: углы — по скруглению окна, а к левому и правому краю полосы плавно тают (не обрываются ступенькой)
        OpacityMask = new LinearGradientBrush(new GradientStopCollection
        {
            new(Colors.Transparent, 0), new(Colors.Black, 0.08), new(Colors.Black, 0.92), new(Colors.Transparent, 1),
        }, new Point(0, 0), new Point(1, 0));
        SizeChanged += (_, _) => Clip = new RectangleGeometry(new Rect(RenderSize), Corners.WindowDip, Corners.WindowDip);
        IsVisibleChanged += (_, _) => Update();
        Accent.Changed += () => Dispatcher.InvokeAsync(Draw);
        // события захвата приходят из его потока; будим не чаще, чем поток интерфейса успевает разбудить
        _audio.SoundStarted += () =>
        {
            if (Interlocked.Exchange(ref _wakeQueued, 1) == 0) Dispatcher.BeginInvoke(Wake);
        };
        _audio.Lost += () => Dispatcher.BeginInvoke(EnsureCapture);
        _retry.Tick += (_, _) => EnsureCapture();
    }

    /// <summary>Настройка «Визуализация музыки».</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Update();
        }
    }

    private void Update()
    {
        var on = _enabled && IsVisible;
        if (on == _active) return;
        _active = on;
        if (on)
        {
            EnsureCapture();
            Tick(true);
        }
        else
        {
            Tick(false);
            _retry.Stop();
            _audio.Stop();
            Array.Clear(_level);
            _wasAny = false;
            Draw();
        }
    }

    /// <summary>Кадры: подписаться (звук есть или полосы ещё опускаются) или отписаться (тишина, всё на нуле).</summary>
    private void Tick(bool on)
    {
        if (on == _ticking) return;
        _ticking = on;
        Core.Logging.PerfLog.Note(on ? "музыка: кадры идут" : "музыка: тишина — визуализация спит");
        if (on)
        {
            _lastMs = FrameClock.NowMs;
            FrameClock.Rendering += OnFrame;
        }
        else FrameClock.Rendering -= OnFrame;
    }

    /// <summary>После тишины пришёл звук — снова считать кадры.</summary>
    private void Wake()
    {
        Volatile.Write(ref _wakeQueued, 0);
        if (_active) Tick(true);
    }

    /// <summary>Захват должен идти, пока визуализацию видно: не идёт — поднять (или попробовать позже).</summary>
    private void EnsureCapture()
    {
        if (!_active)
        {
            _retry.Stop();
            return;
        }
        if (!_audio.Running) _audio.Start(); // сам не чаще, чем разрешает его пауза после сбоя
        if (_audio.Running) _retry.Stop();
        else _retry.Start();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = FrameClock.NowMs;
        var dt = Math.Clamp(now - _lastMs, 1, 100);
        _lastMs = now;
        _audio.Bands(_raw); // нового звука нет — прежние полосы (БПФ не пересчитывается)
        // лёгкое сглаживание с соседями — без «частокола» и одинаковых ступенек
        for (var i = 0; i < PerSide; i++)
            _soft[i] = (_raw[Math.Max(0, i - 1)] + 2 * _raw[i] + _raw[Math.Min(PerSide - 1, i + 1)]) / 4;
        var any = false;
        for (var i = 0; i < PerSide; i++)
        {
            var target = _soft[i];
            // вверх — быстро, вниз — плавно (не по кадрам, а по времени: одинаково при любой частоте кадров)
            var k = target > _level[i] ? 1 - Math.Exp(-dt / 35.0) : 1 - Math.Exp(-dt / 220.0);
            _level[i] += (target - _level[i]) * k;
            if (_level[i] > 0.002) any = true;
            else _level[i] = 0;
        }
        if (any || _wasAny) Draw();
        // полосы опустились, а звука нет — спать до следующего звука (картинка с нулями уже нарисована)
        if (!any && !_wasAny && _audio.Silent) Tick(false);
        _wasAny = any;
    }

    /// <summary>Полосы — в картинку: снизу ярче (альфа 0xE0), к верху полосы — прозрачнее (0x30).</summary>
    private void Draw()
    {
        Array.Clear(_px);
        var c = Accent.Color;
        for (var i = 0; i < PerSide; i++)
        {
            var lv = _level[i];
            if (lv <= 0) continue;
            var bh = (int)Math.Round(Math.Max(1, lv * lv * H * MaxShare)); // квадрат — тихое ниже, громкое выразительнее
            int xr = (PerSide + i) * 2, xl = (PerSide - 1 - i) * 2;        // зеркально от середины
            for (var r = 0; r < bh; r++)
            {
                var t = bh <= 1 ? 0 : (double)r / (bh - 1);
                var a = (int)(0xE0 - (0xE0 - 0x30) * t);
                var px = (a << 24) | (c.R * a / 255 << 16) | (c.G * a / 255 << 8) | (c.B * a / 255);
                var row = (H - 1 - r) * W;
                _px[row + xr] = px;
                _px[row + xl] = px;
            }
        }
        _bmp.WritePixels(new Int32Rect(0, 0, W, H), _px, W * 4, 0);
    }

    // ⚠ не возвращать векторы во всё окно + BlurEffect: ~40 % ядра, GC каждые 2 с, кадры по 30–46 мс — лента дёргалась
    protected override void OnRender(DrawingContext dc)
    {
        // картинка одна и та же — меняются только её точки (WritePixels): перерисовки окна не нужно
        if (ActualWidth > 0 && ActualHeight > 0) dc.DrawImage(_bmp, new Rect(RenderSize));
    }
}
