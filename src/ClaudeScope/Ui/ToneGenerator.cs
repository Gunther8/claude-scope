using System.Media;

namespace ClaudeScope.Ui;

/// <summary>
/// 提示音。只有「等你确认」和「出错」响——这两个是需要你放下手头事的状态。
/// 现场合成 WAV 到内存里播，不依赖任何音频文件，也不受系统声音方案影响。
/// </summary>
public sealed class ToneGenerator : IDisposable
{
    readonly SoundPlayer? _waiting;
    readonly SoundPlayer? _error;
    readonly Dictionary<string, DateTime> _lastPlayed = new()
    {
        ["waiting"] = DateTime.MinValue,
        ["error"] = DateTime.MinValue
    };

    public ToneGenerator()
    {
        // 等你确认：两声上行小铃；出错：两声下行低音
        _waiting = Build(new double[] { 880, 1174 }, new[] { 0.16, 0.26 });
        _error = Build(new double[] { 233, 175 }, new[] { 0.20, 0.34 });
    }

    static SoundPlayer? Build(double[] freqs, double[] durs, double vol = 0.22)
    {
        try
        {
            var ms = new MemoryStream();
            const int rate = 22050;
            var total = durs.Sum(d => (int)(rate * d));

            using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                var dataBytes = total * 2;
                bw.Write("RIFF"u8.ToArray());
                bw.Write(36 + dataBytes);
                bw.Write("WAVE"u8.ToArray());
                bw.Write("fmt "u8.ToArray());
                bw.Write(16);
                bw.Write((short)1);          // PCM
                bw.Write((short)1);          // 单声道
                bw.Write(rate);
                bw.Write(rate * 2);          // byteRate
                bw.Write((short)2);          // blockAlign
                bw.Write((short)16);         // bits
                bw.Write("data"u8.ToArray());
                bw.Write(dataBytes);

                for (var seg = 0; seg < freqs.Length; seg++)
                {
                    var n = (int)(rate * durs[seg]);
                    for (var i = 0; i < n; i++)
                    {
                        var t = (double)i / rate;
                        // 淡入淡出，不然起止会有咔哒声
                        var env = Math.Min(1.0, Math.Min(i / (rate * 0.01), (n - i) / (rate * 0.05)));
                        var v = Math.Sin(2 * Math.PI * freqs[seg] * t) * env * vol;
                        bw.Write((short)Math.Round(v * short.MaxValue));
                    }
                }
            }

            ms.Position = 0;
            var player = new SoundPlayer(ms);
            player.Load();
            return player;
        }
        catch
        {
            return null;   // 没有声卡之类的情况，静音降级，不该让横幅起不来
        }
    }

    /// <param name="minInterval">同类提示音的最小间隔，防止反复打扰</param>
    public void Play(string kind, bool enabled, TimeSpan minInterval)
    {
        if (!enabled) return;
        if (!_lastPlayed.TryGetValue(kind, out var last)) return;
        var now = DateTime.UtcNow;
        if (now - last < minInterval) return;
        _lastPlayed[kind] = now;

        var p = kind == "waiting" ? _waiting : _error;
        // Play() 是异步的，不会卡住渲染线程
        try { p?.Play(); } catch { }
    }

    public void Dispose()
    {
        _waiting?.Dispose();
        _error?.Dispose();
    }
}
