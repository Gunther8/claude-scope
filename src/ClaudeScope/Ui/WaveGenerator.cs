namespace ClaudeScope.Ui;

/// <summary>
/// 示波器的采样生成。两个坑在这里解决掉了，都是靠放大截图才发现的：
///
/// 1. 逐采样的白噪声画出来是像素哈希，看着像马赛克。改成时间相关的平滑噪声，
///    每 NoiseStep 个采样换一个目标值、中间 smoothstep 过渡，抖动才像示波器而不是雪花。
///
/// 2. 一帧要补好几个像素的采样（执行命令那档 33fps 下一帧要补约 10 个）。
///    如果这些采样共用同一个相位，画出来是"平台 + 跳变"的台阶——
///    那不是波形，是帧率的痕迹。所以按每像素的相位增量在帧内插值。
/// </summary>
public sealed class WaveGenerator
{
    const int NoiseStep = 6;

    readonly float[] _samples;
    readonly Random _rng = new();

    int _head;
    double _phase;
    double _acc;
    double _curAmp;
    double _curScroll;

    double _noiseFrom, _noiseTo;
    int _noiseI = NoiseStep;

    public WaveGenerator(int capacity)
    {
        _samples = new float[Math.Max(64, capacity)];
    }

    public int Capacity => _samples.Length;

    double SmoothNoise()
    {
        if (_noiseI >= NoiseStep)
        {
            _noiseFrom = _noiseTo;
            _noiseTo = _rng.NextDouble() * 2 - 1;
            _noiseI = 0;
        }
        var t = (double)_noiseI / NoiseStep;
        _noiseI++;
        t = t * t * (3 - 2 * t);   // smoothstep，别在换目标的那一点出现折角
        return _noiseFrom + (_noiseTo - _noiseFrom) * t;
    }

    static double Shape(WaveShape shape, double p) => shape switch
    {
        WaveShape.Sine => Math.Sin(p * Math.PI * 2),
        WaveShape.Burst => BurstAt(p),
        WaveShape.Saw => 2 * (p - Math.Floor(p + 0.5)),
        WaveShape.Clipped => Math.Clamp(Math.Sin(p * Math.PI * 2) * 1.8, -1, 1),
        WaveShape.Pulse => PulseAt(p),
        WaveShape.Ping => PingAt(p),
        _ => 0
    };

    /// 打字节奏：一阵一阵的方波，中间留白
    static double BurstAt(double p)
    {
        var seg = (int)Math.Floor(p * 2) % 3;
        if (seg == 2) return 0;
        var sign = Math.Sin(p * Math.PI * 2) > 0 ? 1 : -1;
        return sign * (0.6 + 0.4 * Math.Abs(Math.Sin(p * 7)));
    }

    static double PulseAt(double p)
    {
        var t = p - Math.Floor(p);
        var sign = t < 0.5 ? 1 : -1;
        return Math.Exp(-Math.Pow((t - 0.5) * 5, 2)) * sign * 2;
    }

    static double PingAt(double p)
    {
        var t = p - Math.Floor(p);
        return t < 0.06 ? Math.Sin(t / 0.06 * Math.PI) : 0;
    }

    public void Advance(double dt, WaveSpec target)
    {
        // 参数缓动，让"状态变了"本身成为一次可见的运动变化
        var k = 1 - Math.Pow(0.004, dt);
        _curAmp += (target.Amp - _curAmp) * k;
        _curScroll += (target.Scroll - _curScroll) * k;

        _phase += dt * target.Freq;
        _acc += dt * _curScroll;

        var steps = (int)Math.Floor(_acc);
        if (steps <= 0) return;
        if (steps > _samples.Length) steps = _samples.Length;
        _acc -= steps;

        // 帧内相位插值：每像素推进 freq/scroll 个周期
        var dp = _curScroll > 0.001 ? target.Freq / _curScroll : 0;

        for (var i = 0; i < steps; i++)
        {
            var ph = _phase - (steps - 1 - i) * dp;
            var v = Shape(target.Shape, ph) * _curAmp + SmoothNoise() * target.Noise;
            _samples[_head] = (float)v;
            _head = (_head + 1) % _samples.Length;
        }
    }

    /// <summary>取最近 count 个采样，并做三点平滑——把相邻像素之间的硬台阶抹掉，抗锯齿才吃得上劲。</summary>
    public void CopySmoothed(Span<float> dest)
    {
        var n = dest.Length;
        if (n == 0) return;
        var cap = _samples.Length;
        var start = ((_head - n) % cap + cap) % cap;

        var prev = _samples[start];
        for (var i = 0; i < n; i++)
        {
            var cur = _samples[(start + i) % cap];
            var next = _samples[(start + i + 1) % cap];
            dest[i] = (prev + cur * 2 + next) / 4;
            prev = cur;
        }
    }

    public float Latest => _samples[(_head - 1 + _samples.Length) % _samples.Length];
}
