using DesktopPanel.Core.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DesktopPanel.Features.Audio;

/// <summary>
/// Спектр того, что сейчас играет в системе: захват «петлёй» (WASAPI loopback) устройства вывода по умолчанию — то же,
/// что слышно в колонках, без драйверов и без доступа к другим программам. Звук в своём потоке складывается в кольцо
/// последних отсчётов; полосы (Bands) считаются по запросу — раз в кадр отрисовки, и только если с прошлого раза пришёл
/// новый звук (Windows присылает его порциями ~10 мс — чаще считать БПФ незачем).
/// Тишина — это не только «ничего не приходит»: браузеры и плееры на паузе часто шлют поток нулей. Поэтому звуком
/// считается только порция, где есть что-то громче −80 дБ (ниже порога видимых полос): в тишине БПФ не считается вовсе,
/// а визуализация может спать (SoundStarted её будит).
/// Пока захват не нужен (панель скрыта, визуализация выключена) — Stop: устройство отпускается.
/// </summary>
internal sealed class AudioSpectrum : IDisposable
{
    private const int N = 4096; // окно БПФ: ~85 мс при 48 кГц, шаг частот ~12 Гц (басы различаются)
    private const float Quiet = 1e-4f; // −80 дБ: тише этого полос не видно (нулём они становятся уже на −60 дБ)
    private const long SilenceMs = 150;
    private readonly float[] _ring = new float[N];
    private readonly object _lock = new();
    private int _pos;
    private long _lastLoud = long.MinValue / 2; // когда пришёл последний НЕ тихий звук
    private int _version, _seen;                 // порции звука: сколько пришло / по какую посчитано БПФ
    private WasapiLoopbackCapture? _cap;
    private int _channels = 2, _bits = 32, _rate = 48000;
    private bool _float = true;
    private long _retryAt;

    private readonly double[] _re = new double[N], _im = new double[N];
    private static readonly double[] Hann = MakeHann();

    public bool Running => _cap is not null;

    /// <summary>Тихо дольше 150 мс (или захвата нет) — полосы нулевые, кадры считать незачем.</summary>
    public bool Silent
    {
        get { lock (_lock) return _cap is null || Environment.TickCount64 - _lastLoud > SilenceMs; }
    }

    /// <summary>После тишины пришёл звук. Зовётся из потока захвата.</summary>
    // ⚠ оба события — из потока захвата: к интерфейсу только через Dispatcher.BeginInvoke
    public event Action? SoundStarted;

    /// <summary>Захват сам остановился (устройство сменили или отключили). Зовётся из потока захвата.</summary>
    public event Action? Lost;

    public void Start()
    {
        if (_cap is not null || Environment.TickCount64 < _retryAt) return;
        try
        {
            var cap = new WasapiLoopbackCapture();
            var wf = cap.WaveFormat;
            _channels = Math.Max(1, wf.Channels);
            _bits = wf.BitsPerSample;
            _rate = wf.SampleRate;
            _float = wf.Encoding == WaveFormatEncoding.IeeeFloat || (wf.Encoding == WaveFormatEncoding.Extensible && _bits == 32);
            cap.DataAvailable += OnData;
            cap.RecordingStopped += (_, e) =>
            {
                // устройство сменили или отключили — через пару секунд подхватим новое устройство по умолчанию
                if (e.Exception is not null) Log.Warn($"музыка: захват звука остановился: {e.Exception.Message}");
                var lost = false;
                if (ReferenceEquals(_cap, cap))
                {
                    _cap = null;
                    _retryAt = Environment.TickCount64 + 2000;
                    lost = true;
                }
                cap.Dispose();
                if (lost) Lost?.Invoke();
            };
            cap.StartRecording();
            _cap = cap;
        }
        catch (Exception ex)
        {
            Log.Warn($"музыка: захват звука недоступен: {ex.Message}");
            _retryAt = Environment.TickCount64 + 5000;
        }
    }

    public void Stop()
    {
        var cap = _cap;
        _cap = null;
        if (cap is null) return;
        try { cap.StopRecording(); } catch { }
        lock (_lock)
        {
            Array.Clear(_ring);
            _lastLoud = long.MinValue / 2;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var bytesPerSample = _bits / 8;
        var frame = bytesPerSample * _channels;
        if (frame <= 0) return;
        var frames = e.BytesRecorded / frame;
        var woke = false;
        lock (_lock)
        {
            float peak = 0;
            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                var off = i * frame;
                for (var c = 0; c < _channels; c++, off += bytesPerSample)
                {
                    sum += _float ? BitConverter.ToSingle(e.Buffer, off)
                         : _bits == 16 ? BitConverter.ToInt16(e.Buffer, off) / 32768f
                         : _bits == 32 ? BitConverter.ToInt32(e.Buffer, off) / 2147483648f
                         : 0;
                }
                var v = sum / _channels;
                _ring[_pos] = v;
                _pos = (_pos + 1) % N;
                peak = Math.Max(peak, Math.Abs(v));
            }
            if (frames > 0)
            {
                _version++;
                if (peak > Quiet)
                {
                    var now = Environment.TickCount64;
                    woke = now - _lastLoud > SilenceMs;
                    _lastLoud = now;
                }
            }
        }
        if (woke) SoundStarted?.Invoke();
    }

    /// <summary>
    /// Уровни полос 0…1 (низкие — в начале), логарифмически от 50 Гц до 16 кГц. Тишина (громкого звука не было дольше
    /// 150 мс) — все нули. Нового звука с прошлого раза не пришло — полосы не трогает (false).
    /// </summary>
    public bool Bands(float[] bands)
    {
        lock (_lock)
        {
            if (_cap is null || Environment.TickCount64 - _lastLoud > SilenceMs)
            {
                Array.Clear(bands);
                _seen = _version;
                return true;
            }
            if (_version == _seen) return false; // порция звука та же — тот же спектр
            _seen = _version;
            for (var i = 0; i < N; i++)
            {
                _re[i] = _ring[(_pos + i) % N] * Hann[i];
                _im[i] = 0;
            }
        }
        Fft(_re, _im);
        var count = bands.Length;
        double lo = 50, hi = Math.Min(16000, _rate / 2.0 - 1);
        var binHz = (double)_rate / N;
        for (var b = 0; b < count; b++)
        {
            var f0 = lo * Math.Pow(hi / lo, (double)b / count);
            var f1 = lo * Math.Pow(hi / lo, (double)(b + 1) / count);
            double peak;
            if (f1 - f0 < 2 * binHz)
            {
                // полоса уже двух шагов частот (басы) — значение между соседними отсчётами по её середине; раньше
                // несколько полос брали один и тот же отсчёт и прыгали ровной «ступенькой» по 4–6 штук
                var x = Math.Sqrt(f0 * f1) / binHz;
                var i = Math.Clamp((int)x, 1, N / 2 - 2);
                var t = Math.Clamp(x - i, 0, 1);
                peak = Mag(i) * (1 - t) + Mag(i + 1) * t;
            }
            else
            {
                int i0 = Math.Max(1, (int)(f0 / binHz)), i1 = Math.Max(i0 + 1, (int)Math.Ceiling(f1 / binHz));
                peak = 0;
                for (var i = i0; i < i1 && i < N / 2; i++) peak = Math.Max(peak, Mag(i));
            }
            // в децибелы: −60…0 дБ → 0…1 (амплитуда нормирована на размер окна)
            var db = 20 * Math.Log10(peak / (N / 4.0) + 1e-9);
            bands[b] = (float)Math.Clamp((db + 60) / 60, 0, 1);
        }
        return true;
    }

    private double Mag(int i) => Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);

    private static double[] MakeHann()
    {
        var w = new double[N];
        for (var i = 0; i < N; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
        return w;
    }

    /// <summary>Быстрое преобразование Фурье (по основанию 2, на месте).</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
