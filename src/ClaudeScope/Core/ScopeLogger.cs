using System.Text;

namespace ClaudeScope.Core;

/// <summary>
/// 限流 + 轮转日志。
/// 需求里明确要求「不许在后台狂刷错误日志」：
/// 同一类错误在 ThrottleSeconds 内只落一行，被压掉的次数累计后补记。
/// </summary>
public sealed class ScopeLogger : IDisposable
{
    readonly object _gate = new();
    readonly string _file;
    readonly long _maxBytes;
    readonly int _keep;
    readonly TimeSpan _throttle;
    readonly bool _echo;
    readonly Dictionary<string, (DateTime LastAt, int Suppressed)> _throttleState = new();

    StreamWriter? _writer;
    long _size;
    bool _broken;

    public ScopeLogger(LogConfig cfg, bool echo)
    {
        _file = ScopePaths.LogFile;
        _maxBytes = cfg.MaxBytes > 0 ? cfg.MaxBytes : 1024 * 1024;
        _keep = cfg.Keep > 0 ? cfg.Keep : 3;
        _throttle = TimeSpan.FromSeconds(cfg.ThrottleSeconds > 0 ? cfg.ThrottleSeconds : 30);
        _echo = echo;
        Open();
    }

    void Open()
    {
        try
        {
            ScopePaths.EnsureRuntimeDirs();
            _size = File.Exists(_file) ? new FileInfo(_file).Length : 0;
            _writer = new StreamWriter(new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }
        catch
        {
            // 日志写不了不该拖垮程序，降级成只打 stdout
            _broken = true;
            _writer = null;
        }
    }

    void Rotate()
    {
        try
        {
            _writer?.Dispose();
            _writer = null;
            for (var i = _keep - 1; i >= 1; i--)
            {
                var from = $"{_file}.{i}";
                var to = $"{_file}.{i + 1}";
                if (File.Exists(from)) File.Move(from, to, overwrite: true);
            }
            if (File.Exists(_file)) File.Move(_file, $"{_file}.1", overwrite: true);
        }
        catch
        {
            // 轮转失败就继续往原文件写，不做二次伤害
        }
        _size = 0;
        Open();
    }

    void Write(string level, string text)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{level}] {text}";
        lock (_gate)
        {
            if (_echo || _broken) Console.WriteLine(line);
            if (_writer is null) return;
            _writer.WriteLine(line);
            _size += Encoding.UTF8.GetByteCount(line) + 2;
            if (_size >= _maxBytes) Rotate();
        }
    }

    public void Info(string text) => Write("info", text);
    public void Warn(string text) => Write("warn", text);

    /// <summary>同类错误只落一行，期间被压掉的次数在下一次输出时补上。</summary>
    public bool Throttled(string key, string level, string text)
    {
        int suppressed;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_throttleState.TryGetValue(key, out var st) && now - st.LastAt < _throttle)
            {
                _throttleState[key] = (st.LastAt, st.Suppressed + 1);
                return false;
            }
            suppressed = _throttleState.TryGetValue(key, out var prev) ? prev.Suppressed : 0;
            _throttleState[key] = (now, 0);
        }
        Write(level, suppressed > 0 ? $"{text} （期间同类已压制 {suppressed} 次）" : text);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
