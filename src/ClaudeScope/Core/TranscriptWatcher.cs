using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeScope.Core;

/// <summary>
/// 通道②：尾随 ~/.claude/projects/**/&lt;sessionId&gt;.jsonl
///
/// 桌面版会话和 CLI 会话都往这里写，行里带 entrypoint 字段可以区分来源。
/// 这条通道零配置、对装 hook 之前就开着的会话也立即生效，
/// 并且文件 mtime 本身就是一条免费的心跳信号。
/// 模型名和上下文 token 也只有这里有——hook 负载里没有。
/// </summary>
public sealed partial class TranscriptWatcher : IDisposable
{
    /// 首次挂载时回看这么多，用来"接管"一个已经在跑的会话
    const int ReadTailBytes = 64 * 1024;

    sealed class Tracked
    {
        public long Offset;
        public byte[] Rest = Array.Empty<byte>();
        public bool Seeding = true;
    }

    readonly string _dir;
    readonly ScopeRegistry _registry;
    readonly ScopeLogger _log;
    readonly TimeSpan _hot;
    readonly Dictionary<string, Tracked> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly List<System.Threading.Timer> _timers = new();  // 开了 WinForms，Timer 会撞名，写全

    FileSystemWatcher? _fsw;
    int _parseErrors;
    DateTime _lastParseError = DateTime.MinValue;
    readonly object _gate = new();

    public TranscriptWatcher(ScopeConfig cfg, ScopeRegistry registry, ScopeLogger log)
    {
        _dir = cfg.ResolvedProjectsDir;
        _registry = registry;
        _log = log;
        _hot = TimeSpan.FromMinutes(cfg.Thresholds.TranscriptHotMinutes);
    }

    public void Start()
    {
        if (!Directory.Exists(_dir))
        {
            _registry.RaiseIssue("transcript-dir-missing",
                $"找不到会话记录目录：{_dir}（通道②已停用，仅靠 hook 供状态）");
            _log.Warn($"transcript 目录不存在：{_dir}");
            return;
        }
        _registry.ClearIssue("transcript-dir-missing");

        Rescan(initial: true);

        // FileSystemWatcher 会漏事件、也会重复报，所以它只当"发现新文件"的加速器，
        // 真正取数还是靠下面的轮询。
        try
        {
            _fsw = new FileSystemWatcher(_dir, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            _fsw.Created += (_, e) => Consider(e.FullPath);
            _fsw.Changed += (_, e) => Consider(e.FullPath);
            _fsw.Error += (_, e) =>
            {
                _log.Throttled("fsw", "warn", $"FileSystemWatcher 出错，已退回纯轮询：{e.GetException().Message}");
                try { _fsw!.EnableRaisingEvents = false; } catch { }
            };
            _fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _log.Warn($"FileSystemWatcher 不可用（{ex.GetType().Name}），使用纯轮询模式");
        }

        _timers.Add(new System.Threading.Timer(_ => SafePoll(), null, 700, 700));
        _timers.Add(new System.Threading.Timer(_ => SafeRescan(), null, 5000, 5000));
    }

    void SafePoll()
    {
        try { Poll(); }
        catch (Exception ex) { _log.Throttled("poll", "warn", $"轮询会话记录出错：{ex.Message}"); }
    }

    void SafeRescan()
    {
        try { Rescan(false); }
        catch (Exception ex) { _log.Throttled("rescan", "warn", $"扫描会话目录出错：{ex.Message}"); }
    }

    /* ------------------------------------------------------------ 文件发现 */

    void Rescan(bool initial)
    {
        string[] projects;
        try { projects = Directory.GetDirectories(_dir); }
        catch (Exception ex)
        {
            _log.Throttled("rescan-dir", "warn", $"扫描会话目录失败：{ex.GetType().Name}");
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var proj in projects)
        {
            string[] files;
            try { files = Directory.GetFiles(proj, "*.jsonl"); }
            catch { continue; }

            foreach (var f in files)
            {
                lock (_gate) { if (_files.ContainsKey(f)) continue; }
                try
                {
                    var fi = new FileInfo(f);
                    // 只接手最近还在动的会话，否则每次扫描都要 stat 几百个历史文件
                    if (now - fi.LastWriteTimeUtc > _hot) continue;
                    Attach(f, fi.Length, quiet: initial);
                }
                catch { /* 文件可能刚被删，忽略 */ }
            }
        }
    }

    void Consider(string path)
    {
        lock (_gate) { if (_files.ContainsKey(path)) return; }
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return;
            Attach(path, fi.Length, quiet: false);
        }
        catch { }
    }

    void Attach(string path, long size, bool quiet)
    {
        lock (_gate)
        {
            if (_files.ContainsKey(path)) return;
            _files[path] = new Tracked { Offset = Math.Max(0, size - ReadTailBytes) };
        }
        if (!quiet) _log.Info($"接管会话记录：{Path.GetFileName(path)}");
        ReadDelta(path);
    }

    /* ------------------------------------------------------------ 轮询取数 */

    void Poll()
    {
        KeyValuePair<string, Tracked>[] snapshot;
        lock (_gate) snapshot = _files.ToArray();

        var now = DateTime.UtcNow;
        foreach (var (path, rec) in snapshot)
        {
            FileInfo fi;
            try
            {
                fi = new FileInfo(path);
                if (!fi.Exists) { lock (_gate) _files.Remove(path); continue; }
            }
            catch { lock (_gate) _files.Remove(path); continue; }

            // 凉了就放手，省得白扫
            if (now - fi.LastWriteTimeUtc > _hot && fi.Length == rec.Offset)
            {
                lock (_gate) _files.Remove(path);
                continue;
            }

            if (fi.Length == rec.Offset) continue;
            if (fi.Length < rec.Offset)
            {
                // 文件被截断/重建
                rec.Offset = 0;
                rec.Rest = Array.Empty<byte>();
            }
            ReadDelta(path);
        }

        // 解析错误连续 60 秒没再出现就撤掉降级提示
        if (_parseErrors > 0 && (now - _lastParseError).TotalSeconds > 60)
        {
            _parseErrors = 0;
            _registry.ClearIssue("transcript-parse");
        }
    }

    void ReadDelta(string path)
    {
        Tracked rec;
        lock (_gate) { if (!_files.TryGetValue(path, out rec!)) return; }

        try
        {
            // Claude 正在往里写，必须用 ReadWrite|Delete 共享打开，绝不能锁住它
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (fs.Length <= rec.Offset) return;
            var len = (int)Math.Min(fs.Length - rec.Offset, 4 * 1024 * 1024);
            var buf = new byte[len];
            fs.Seek(rec.Offset, SeekOrigin.Begin);
            var read = fs.Read(buf, 0, len);
            rec.Offset += read;

            // 上次剩下的半行拼上来。用字节而不是字符串，
            // 否则 UTF-8 多字节字符正好被切断时会解出乱码。
            var data = rec.Rest.Length > 0
                ? rec.Rest.Concat(buf.Take(read)).ToArray()
                : buf[..read];

            var lastNl = Array.LastIndexOf(data, (byte)'\n');
            if (lastNl < 0) { rec.Rest = data; return; }

            rec.Rest = data[(lastNl + 1)..];
            var text = Encoding.UTF8.GetString(data, 0, lastNl);

            var lines = text.Split('\n');
            // 首次挂载时可能截在半行上，丢掉第一段残行
            var start = rec.Seeding ? 1 : 0;
            rec.Seeding = false;
            for (var i = start; i < lines.Length; i++) HandleLine(lines[i], path);
        }
        catch (IOException ex)
        {
            _log.Throttled("read-io", "warn", $"读取会话记录失败：{ex.GetType().Name}");
        }
        catch (UnauthorizedAccessException)
        {
            lock (_gate) _files.Remove(path);
        }
    }

    /* ------------------------------------------------------------ 行解析 */

    // 会话记录里混着一堆系统注入的 XML 块（任务通知、系统提醒、命令回显……）。
    // 它们是"喂给 Claude 的东西"，不是"Claude 在做的事"，
    // 直接当跑马灯文案会显示成一坨 <task-notification><task-id>byk84k96g</task-id>…
    [GeneratedRegex(@"^<(task-notification|system-reminder|command-name|command-message|command-args|local-command-stdout|local-command-stderr|user-prompt-submit-hook|bash-input|bash-stdout|bash-stderr|function_results|channel|ide_[a-z_]+|nudge)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex InternalTag();

    static bool IsInternalMessage(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith('<') && InternalTag().IsMatch(t);
    }

    void HandleLine(string line, string path)
    {
        var t = line.Trim();
        if (t.Length == 0 || t[0] != '{') return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(t); }
        catch (JsonException)
        {
            _parseErrors++;
            _lastParseError = DateTime.UtcNow;
            _log.Throttled("parse", "warn", $"会话记录里有解析不了的行：{Path.GetFileName(path)}");
            if (_parseErrors >= 5)
                _registry.RaiseIssue("transcript-parse",
                    $"会话记录有损坏行（已跳过 {_parseErrors} 行），通道②降级运行");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            string? Str(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var id = Str(root, "sessionId") ?? Str(root, "session_id");
            if (string.IsNullOrEmpty(id)) return;

            var cwd = Str(root, "cwd");
            var source = Str(root, "entrypoint");
            var type = Str(root, "type");

            // 每条记录自己带的时间戳。接管一个会话时我们会回看文件末尾 64KB，
            // 那一段全是历史；registry 靠这个时间戳把历史挡在状态机外面。
            DateTime? ts = null;
            if (Str(root, "timestamp") is { Length: > 0 } tsRaw &&
                DateTime.TryParse(tsRaw, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                              | System.Globalization.DateTimeStyles.AssumeUniversal, out var tsVal))
                ts = tsVal;

            void Apply(string sid, TranscriptEvent e) => _registry.ApplyTranscript(sid, e with { At = ts });

            // 子代理（sidechain）不夺主状态，只当心跳
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
            {
                Apply(id!, new TranscriptEvent(TranscriptKind.Heartbeat) { Cwd = cwd, Source = source });
                return;
            }

            if (type == "user")
            {
                if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content))
                    return;

                if (content.ValueKind == JsonValueKind.String)
                {
                    if (Str(root, "promptSource") == "hook" || Str(root, "userType") == "internal") return;
                    var text = content.GetString() ?? "";
                    var kind = IsInternalMessage(text) ? TranscriptKind.Heartbeat : TranscriptKind.UserPrompt;
                    Apply(id!, new TranscriptEvent(kind)
                    {
                        Text = kind == TranscriptKind.UserPrompt ? text : null,
                        Cwd = cwd,
                        Source = source
                    });
                    return;
                }

                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        // 你按 ESC 打断的时候，Claude Code 不发任何 hook——连 Stop 都不发。
                        // 于是会话被永远钉在"思考中"，几分钟后被看门狗误判成"疑似卡住"，
                        // 而"疑似卡住"优先级比"执行命令"还高，会把真正在干活的窗口挤下屏幕。
                        // 记录里唯一的痕迹就是这行文本，所以只能从这儿认。
                        if (Str(block, "type") == "text" &&
                            Str(block, "text") is { } bt &&
                            bt.TrimStart().StartsWith("[Request interrupted by user", StringComparison.Ordinal))
                        {
                            Apply(id!, new TranscriptEvent(TranscriptKind.Interrupted)
                            { Cwd = cwd, Source = source });
                            return;
                        }

                        if (Str(block, "type") != "tool_result") continue;
                        if (!block.TryGetProperty("is_error", out var isErr) || isErr.ValueKind != JsonValueKind.True) continue;

                        var errText = block.TryGetProperty("content", out var c)
                            ? c.ValueKind switch
                            {
                                JsonValueKind.String => c.GetString() ?? "",
                                JsonValueKind.Array => string.Join(" ", c.EnumerateArray()
                                    .Select(b => Str(b, "text") ?? "").Where(x => x.Length > 0)),
                                _ => "工具返回错误"
                            }
                            : "工具返回错误";

                        Apply(id!, new TranscriptEvent(TranscriptKind.ToolError)
                        { Text = errText, Cwd = cwd, Source = source });
                        return;
                    }
                    Apply(id!, new TranscriptEvent(TranscriptKind.Heartbeat) { Cwd = cwd, Source = source });
                }
                return;
            }

            if (type == "assistant")
            {
                if (!root.TryGetProperty("message", out var msg)) return;

                var model = Str(msg, "model");
                var ctx = ContextTokensOf(msg);

                if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in content.EnumerateArray())
                    {
                        switch (Str(b, "type"))
                        {
                            case "thinking":
                                Apply(id!, new TranscriptEvent(TranscriptKind.Thinking)
                                { Cwd = cwd, Source = source, Model = model, ContextTokens = ctx });
                                break;
                            case "text":
                                var txt = Str(b, "text");
                                if (!string.IsNullOrWhiteSpace(txt))
                                    Apply(id!, new TranscriptEvent(TranscriptKind.Text)
                                    { Text = txt, Cwd = cwd, Source = source, Model = model, ContextTokens = ctx });
                                break;
                            case "tool_use":
                                Apply(id!, new TranscriptEvent(TranscriptKind.ToolUse)
                                {
                                    ToolName = Str(b, "name"),
                                    ToolInput = b.TryGetProperty("input", out var inp) ? inp.Clone() : null,
                                    Cwd = cwd,
                                    Source = source,
                                    Model = model,
                                    ContextTokens = ctx
                                });
                                break;
                        }
                    }

                    if (Str(msg, "stop_reason") == "end_turn")
                    {
                        var lastText = content.EnumerateArray()
                            .Where(b => Str(b, "type") == "text")
                            .Select(b => Str(b, "text"))
                            .LastOrDefault(x => !string.IsNullOrWhiteSpace(x));
                        Apply(id!, new TranscriptEvent(TranscriptKind.TurnEnd)
                        { Text = lastText ?? "本轮结束", Cwd = cwd, Source = source, Model = model, ContextTokens = ctx });
                    }
                }
                return;
            }

            Apply(id!, new TranscriptEvent(TranscriptKind.Heartbeat) { Cwd = cwd, Source = source });
        }
    }

    /// <summary>
    /// 上下文占用 = 这次请求真正送进模型的 token 总量。
    /// 缓存命中的部分同样占上下文窗口，所以三项都要算进去——
    /// 只看 input_tokens 会得到一个小到荒谬的数（缓存命中时它可能只有个位数）。
    /// </summary>
    static long? ContextTokensOf(JsonElement msg)
    {
        if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return null;
        long Get(string name) =>
            u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        var total = Get("input_tokens") + Get("cache_read_input_tokens") + Get("cache_creation_input_tokens");
        return total > 0 ? total : null;
    }

    public void Dispose()
    {
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
        _fsw?.Dispose();
    }
}
