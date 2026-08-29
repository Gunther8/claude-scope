using System.Text.Json;

namespace ClaudeScope.Core;

public sealed class ScopeSession
{
    public required string Id { get; init; }
    public string Source { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Project { get; set; } = "";
    public int? Pid { get; set; }

    public ScopeState State { get; set; } = ScopeState.Idle;
    public string Label { get; set; } = "空闲";
    public string Detail { get; set; } = "";
    public string Note { get; set; } = "";

    public DateTime Since { get; set; } = DateTime.UtcNow;
    public DateTime LastEventAt { get; set; } = DateTime.UtcNow;
    public DateTime? ToolStartedAt { get; set; }
    public DateTime ErrorUntil { get; set; } = DateTime.MinValue;

    /// 停滞看门狗的宽限期。压缩上下文这类"合法的长时间静默"用它压住误报，
    /// 但只压这么久——真挂了还是要报出来。
    public DateTime StallGraceUntil { get; set; } = DateTime.MinValue;

    /// 被判成"疑似卡住"之前是什么状态。停滞是推断出来的，一旦有事件证明它还活着，
    /// 要能原样退回去，而不是把误判一直挂在屏幕上。
    public ScopeState? StateBeforeStall { get; set; }

    public bool HookSeen { get; set; }
    public bool TranscriptSeen { get; set; }
    public bool Ended { get; set; }

    /// 被「出错硬地板」挡下来的那次转换，到点补上
    public (ScopeState State, string Label, string? Detail, bool Force)? Pending { get; set; }

    /// 模型 / 上下文，来自 transcript 的 assistant 行
    public string? Model { get; set; }
    public long? ContextTokens { get; set; }
}

/// <summary>控制台下给横幅的指令（进/出演示模式），随快照下发。</summary>
public sealed record ScopeCommand(string Type, string? Demo, long Ts);

public sealed class ScopeRegistry
{
    readonly object _gate = new();
    readonly Dictionary<string, ScopeSession> _sessions = new();
    readonly Dictionary<string, ScopeIssue> _issues = new();
    readonly SortedDictionary<string, long> _hookEvents = new(StringComparer.Ordinal);
    readonly SortedSet<string> _hookFields = new(StringComparer.Ordinal);
    readonly ScopeConfig _cfg;
    readonly ScopeLogger _log;

    public ScopeRegistry(ScopeConfig cfg, ScopeLogger log)
    {
        _cfg = cfg;
        _log = log;
        foreach (var i in cfg.LoadIssues) RaiseIssue(i.Code, i.Text);
    }

    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public long HookCount { get; private set; }
    public long TranscriptCount { get; private set; }
    public long Rev { get; private set; }
    public ScopeCommand? Command { get; private set; }

    /// 状态变了就触发一次——横幅在同一个进程里，直接订阅，不用轮询。
    public event Action? Changed;

    void Touch()
    {
        Rev++;
        Changed?.Invoke();
    }

    /* ---------------------------------------------------------------- 指令 */

    public void SetCommand(string type, string? demo = null)
    {
        lock (_gate)
        {
            Command = new ScopeCommand(type, demo, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        Touch();
    }

    /* ---------------------------------------------------------------- 问题 */

    public void RaiseIssue(string code, string text)
    {
        lock (_gate)
        {
            if (_issues.TryGetValue(code, out var prev))
                _issues[code] = prev with { Text = text, Count = prev.Count + 1 };
            else
                _issues[code] = new ScopeIssue(code, text);
        }
        Touch();
    }

    public void ClearIssue(string code)
    {
        bool removed;
        lock (_gate) removed = _issues.Remove(code);
        if (removed) Touch();
    }

    /* ---------------------------------------------------------------- 会话 */

    ScopeSession GetOrCreate(string id, string? cwd, string? source, int? pid)
    {
        if (!_sessions.TryGetValue(id, out var s))
        {
            s = new ScopeSession { Id = id };
            _sessions[id] = s;
        }
        if (!string.IsNullOrEmpty(cwd) && s.Cwd.Length == 0)
        {
            s.Cwd = cwd!;
            s.Project = ToolClassifier.BaseName(cwd);
        }
        if (!string.IsNullOrEmpty(source) && s.Source.Length == 0) s.Source = source!;
        if (pid is > 0 && s.Pid is null) s.Pid = pid;
        return s;
    }

    void Set(ScopeSession s, ScopeState state, string label, string? detail, bool force = false, string note = "")
    {
        var now = DateTime.UtcNow;

        // 出错的硬地板：无论后面来什么事件（哪怕是 Stop），红屏至少停留这么久，
        // 否则一次 200 毫秒的闪红你余光根本抓不到。被挡住的转换会排队，到点补上。
        var floor = TimeSpan.FromSeconds(_cfg.Thresholds.ErrorMinDisplaySeconds);
        if (s.State == ScopeState.Error && state != ScopeState.Error && now - s.Since < floor)
        {
            s.Pending = (state, label, detail, force);
            s.LastEventAt = now;
            Touch();
            return;
        }

        // 软粘滞期：非强制的事件不夺走红屏，但会把"它已经在干别的了"写进副标题
        if (s.State == ScopeState.Error && now < s.ErrorUntil && state != ScopeState.Error && !force)
        {
            s.LastEventAt = now;
            if (!string.IsNullOrEmpty(detail)) s.Detail = detail!;
            Touch();
            return;
        }

        s.Pending = null;

        var changed = s.State != state;
        if (changed) s.Since = now;
        s.State = state;
        s.Label = label;
        if (detail is not null) s.Detail = detail;
        s.LastEventAt = now;
        s.Note = note;

        if (state is ScopeState.Running or ScopeState.Writing)
        {
            if (changed || s.ToolStartedAt is null) s.ToolStartedAt = now;
        }
        else if (state != ScopeState.Stalled)
        {
            s.ToolStartedAt = null;
        }

        if (state == ScopeState.Error)
            s.ErrorUntil = now.AddSeconds(_cfg.Thresholds.ErrorStickySeconds);

        // 来了新事件就说明它还在动，宽限和停滞推断都没必要再留着
        s.StallGraceUntil = DateTime.MinValue;
        s.StateBeforeStall = null;

        Touch();
    }

    /* ------------------------------------------------------------ 通道① hook */

    public void ApplyHook(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;

        string? Get(string name) =>
            payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var id = Get("session_id") ?? Get("sessionId");
        if (string.IsNullOrEmpty(id)) return;

        JsonElement? Obj(string name) =>
            payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

        int? pid = payload.TryGetProperty("pid", out var pv) && pv.ValueKind == JsonValueKind.Number
            ? pv.GetInt32() : null;

        // 子代理事件不该抢显示：它们只更新文案，不改状态
        var isSub = payload.TryGetProperty("agent_id", out _) || payload.TryGetProperty("agent_type", out _);

        lock (_gate)
        {
            var s = GetOrCreate(id!, Get("cwd"), Get("source") ?? Get("entrypoint"), pid);
            s.HookSeen = true;
            HookCount++;

            var ev = Get("hook_event_name") ?? Get("hookEventName") ?? "";
            foreach (var prop in payload.EnumerateObject()) _hookFields.Add(prop.Name);
            var evKey = ev.Length > 0 ? ev : "(无事件名)";
            _hookEvents[evKey] = _hookEvents.TryGetValue(evKey, out var evN) ? evN + 1 : 1;
            var toolName = Get("tool_name");
            var toolInput = Obj("tool_input");

            switch (ev)
            {
                case "SessionStart":
                    s.Ended = false;
                    if (pid is > 0) s.Pid = pid;
                    Set(s, ScopeState.Idle, "空闲", "会话已就绪", force: true);
                    break;

                case "UserPromptSubmit":
                    Set(s, ScopeState.Thinking, "思考中",
                        ToolClassifier.Clip(Get("prompt") ?? "收到指令", 160), force: true);
                    break;

                case "UserPromptExpansion":
                    Set(s, ScopeState.Thinking, "思考中",
                        ToolClassifier.Clip(Get("command") ?? Get("prompt") ?? "收到指令", 160), force: true);
                    break;

                case "PreCompact":
                    Set(s, ScopeState.Thinking, "整理上下文",
                        Get("trigger") == "auto" ? "上下文满了，自动压缩中" : "压缩会话历史", force: true);
                    // 压缩期间一条事件都不会来，实测能静默三分钟。
                    // 不给宽限的话看门狗会稳定误报"疑似卡住"，那个指示灯就废了。
                    // 宽限也有上限——真卡死在压缩上，超时之后照样报。
                    s.StallGraceUntil = DateTime.UtcNow.AddSeconds(_cfg.Thresholds.CompactGraceSeconds);
                    break;

                case "PostCompact":
                    Set(s, ScopeState.Thinking, "思考中", "上下文已压缩，继续", force: true);
                    break;

                case "Elicitation":
                    Set(s, ScopeState.Waiting, "等你填写",
                        ToolClassifier.Clip(Get("message") ?? $"{toolName ?? "工具"} 请求你输入信息", 200), force: true);
                    break;

                case "ElicitationResult":
                    Set(s, ScopeState.Thinking, "思考中", "已收到你的输入", force: true);
                    break;

                case "SubagentStart":
                    // 子代理不夺状态，只把它写进副标题——主会话还是在等它
                    s.Detail = ToolClassifier.Clip(
                        $"子任务 · {Get("agent_type") ?? Get("description") ?? "已派出"}", 200);
                    s.LastEventAt = DateTime.UtcNow;
                    Touch();
                    break;

                case "PreToolUse":
                {
                    var c = ToolClassifier.Classify(toolName, toolInput);
                    if (isSub)
                    {
                        s.Detail = $"子任务 · {(c.Detail.Length > 0 ? c.Detail : toolName)}";
                        s.LastEventAt = DateTime.UtcNow;
                        Touch();
                    }
                    else Set(s, c.State, c.Label, c.Detail);
                    break;
                }

                case "PostToolUse":
                case "PostToolBatch":
                    if (isSub)
                    {
                        s.LastEventAt = DateTime.UtcNow;
                        Touch();
                    }
                    else Set(s, ScopeState.Thinking, "思考中",
                        s.Detail.Length > 0 ? $"{s.Detail} · 已完成" : "处理结果");
                    break;

                case "PostToolUseFailure":
                {
                    var err = Get("error") ?? "无错误详情";
                    var prefix = toolName is not null ? $"{toolName} 失败：" : "工具失败：";
                    Set(s, ScopeState.Error, "出错", ToolClassifier.Clip(prefix + err, 200), force: true);
                    break;
                }

                case "StopFailure":
                    Set(s, ScopeState.Error, "出错",
                        ToolClassifier.Clip(Get("error") ?? "API 请求失败", 200), force: true);
                    break;

                case "PermissionRequest":
                {
                    var c = ToolClassifier.Classify(toolName, toolInput);
                    Set(s, ScopeState.Waiting, "等你确认",
                        ToolClassifier.Clip($"授权 {toolName} · {c.Detail}", 200), force: true);
                    break;
                }

                case "PermissionDenied":
                    Set(s, ScopeState.Thinking, "思考中",
                        ToolClassifier.Clip($"已拒绝 {toolName ?? "该操作"}", 160), force: true);
                    break;

                case "Notification":
                    switch (Get("notification_type"))
                    {
                        case "permission_prompt":
                            Set(s, ScopeState.Waiting, "等你确认",
                                s.Detail.Length > 0 ? $"等待授权 · {s.Detail}" : "等待授权", force: true);
                            break;
                        case "idle_prompt":
                        case "agent_needs_input":
                            Set(s, ScopeState.Waiting, "等你回话", "等待你的输入", force: true);
                            break;
                        case "elicitation_dialog":
                        case "elicitation_url_dialog":
                            Set(s, ScopeState.Waiting, "等你填写", "工具请求你输入信息", force: true);
                            break;
                        case "agent_completed":
                            Set(s, ScopeState.Done, "完成", "任务完成", force: true);
                            break;
                    }
                    break;

                case "Stop":
                    if (!isSub)
                        Set(s, ScopeState.Done, "完成",
                            ToolClassifier.Clip(Get("last_assistant_message") ?? "本轮结束", 200), force: true);
                    break;

                case "SubagentStop":
                    s.LastEventAt = DateTime.UtcNow;
                    Touch();
                    break;

                case "SessionEnd":
                    s.Ended = true;
                    Set(s, ScopeState.Dead, "会话已结束",
                        $"原因：{Get("session_end_reason") ?? "未知"}", force: true);
                    break;

                default:
                    // 未知事件只当心跳，不猜状态
                    s.LastEventAt = DateTime.UtcNow;
                    Touch();
                    break;
            }
        }
    }

    /* ---------------------------------------------------------------- 看门狗 */

    /// <param name="isAlive">进程存活探针，惰性调用——平时零开销。</param>
    public void Tick(Func<int, bool> isAlive)
    {
        var dirty = false;
        var now = DateTime.UtcNow;
        var th = _cfg.Thresholds;

        lock (_gate)
        {
            foreach (var (id, s) in _sessions.ToArray())
            {
                var idle = now - s.LastEventAt;

                // 补上被「出错硬地板」挡下来的那次转换
                if (s.Pending is { } p && now - s.Since >= TimeSpan.FromSeconds(th.ErrorMinDisplaySeconds))
                {
                    s.Pending = null;
                    Set(s, p.State, p.Label, p.Detail, p.Force);
                    dirty = true;
                    continue;
                }

                if (s.State == ScopeState.Done && idle > TimeSpan.FromSeconds(th.DoneDecaySeconds))
                {
                    s.State = ScopeState.Idle;
                    s.Label = "空闲";
                    s.Detail = "";
                    s.Since = now;
                    dirty = true;
                    continue;
                }

                // 已经判成停滞的也要每秒复评一次，否则 note 里的秒数会永远停在第一次判定时的值
                var busy = s.State is ScopeState.Running or ScopeState.Writing or ScopeState.Thinking
                                   or ScopeState.Stalled;
                if (busy && idle > TimeSpan.FromSeconds(th.StallSeconds) && now >= s.StallGraceUntil)
                {
                    // 这里就是「在慢慢干活」和「其实已经死了」的分界线
                    var alive = true;
                    if (s.Pid is { } pid)
                    {
                        try { alive = isAlive(pid); } catch { alive = true; }
                    }

                    var secs = (int)idle.TotalSeconds;
                    if (!alive)
                    {
                        s.State = ScopeState.Dead;
                        s.Label = "会话已终止";
                        s.Note = $"进程 {s.Pid} 已不存在，最后一条事件停在 {secs} 秒前";
                        s.Since = now;
                        s.StateBeforeStall = null;
                    }
                    else if (idle > TimeSpan.FromSeconds(th.StallSeconds + th.DeadAfterStallSeconds))
                    {
                        // 静默这么久还一点动静没有，继续挂着"疑似卡住"就是在占屏幕。
                        // 转成"会话已结束"（优先级最低），它自己会退到后面去。
                        s.State = ScopeState.Dead;
                        s.Label = "会话已结束";
                        s.Note = secs >= 120
                            ? $"已 {secs / 60} 分钟没有任何事件，按已结束处理"
                            : $"已 {secs} 秒没有任何事件，按已结束处理";
                        s.Since = now;
                        s.StateBeforeStall = null;
                    }
                    else
                    {
                        if (s.State != ScopeState.Stalled)
                        {
                            s.StateBeforeStall = s.State;
                            s.Since = now;
                        }
                        s.State = ScopeState.Stalled;
                        s.Label = "疑似卡住";
                        s.Note = s.Pid is not null
                            ? $"进程还活着，但已 {secs} 秒没有任何事件"
                            : $"已 {secs} 秒没有任何事件（拿不到进程号，只能按时间判定）";
                    }
                    dirty = true;
                }

                // 超过 TTL 一律清掉。卡住/终止的会话也要走这条路，
                // 否则一个再也不会有事件的死会话会永远赖在显示上。
                if (idle > TimeSpan.FromMinutes(th.SessionTtlMinutes))
                {
                    _sessions.Remove(id);
                    dirty = true;
                }
            }
        }

        if (dirty) Touch();
    }

    /* ---------------------------------------------------------------- 快照 */

    public ScopeSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var th = _cfg.Thresholds;

            var views = _sessions.Values
                .Select(s => SessionView.From(s, now, th.LongRunSeconds))
                .OrderByDescending(v => ScopeStateInfo.Priority(v.RawState))
                .ThenByDescending(v => v.LastEventAt)
                .ToList();

            return new ScopeSnapshot
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Rev = Rev,
                Daemon = new DaemonView
                {
                    StartedAt = new DateTimeOffset(StartedAt).ToUnixTimeMilliseconds(),
                    Port = _cfg.Port,
                    HookCount = HookCount,
                    TranscriptCount = TranscriptCount,
                    WatchTranscripts = _cfg.WatchTranscripts,
                    HookEvents = new SortedDictionary<string, long>(_hookEvents, StringComparer.Ordinal),
                    HookFields = _hookFields.ToList(),
                    Issues = _issues.Values.OrderByDescending(i => i.At).ToList()
                },
                Sound = _cfg.Sound,
                Thresholds = th,
                Command = Command,
                Primary = views.Count > 0 ? views[0] : null,
                Others = views.Skip(1).Take(6).ToList(),
                Total = views.Count
            };
        }
    }

    /* ------------------------------------------------- 通道② transcript */

    /// <summary>
    /// hookSeen 的会话里，transcript 只用来补文案和抓错误，不夺状态控制权；
    /// 没有 hook 的会话（比如装 hook 之前就开着的会话）则由 transcript 全权驱动。
    /// 唯独「出错」两条通道都认——hook 万一漏了，这里也能把屏幕点红。
    /// </summary>
    public void ApplyTranscript(string id, TranscriptEvent ev)
    {
        if (string.IsNullOrEmpty(id)) return;

        lock (_gate)
        {
            var s = GetOrCreate(id, ev.Cwd, ev.Source, null);
            s.TranscriptSeen = true;
            TranscriptCount++;

            // 模型和上下文是纯粹的补充信息，跟谁有状态话语权无关，来了就更新
            if (ev.Model is { Length: > 0 }) s.Model = ev.Model;
            if (ev.ContextTokens is > 0) s.ContextTokens = ev.ContextTokens;

            // 陈旧闸门。尾随是从"文件末尾往回 64KB"接手的，那段里躺着的全是历史事件；
            // 一个会话冷却超过 transcriptHotMinutes 会被放手，它再次写入时会重新接管，
            // 于是那 64KB 历史被当成新事件重放一遍——十几分钟前的一次工具失败
            // 就能把屏幕重新点红。/compact 特别容易触发：它先静默几分钟做摘要，
            // 再一次性把积压的记录刷进文件。
            // 这里只放行"确实刚发生"的行；旧行的模型/上下文照收（上面已收），状态一律不动。
            if (ev.At is { } at && DateTime.UtcNow - at > TimeSpan.FromSeconds(_cfg.Thresholds.TranscriptStaleSeconds))
                return;

            if (s.Ended && ev.Kind != TranscriptKind.UserPrompt) return;

            var authoritative = !s.HookSeen;

            switch (ev.Kind)
            {
                case TranscriptKind.UserPrompt:
                    if (authoritative)
                        Set(s, ScopeState.Thinking, "思考中", ToolClassifier.Clip(ev.Text ?? "收到指令", 160), force: true);
                    else Beat(s);
                    break;

                case TranscriptKind.Thinking:
                    if (authoritative) Set(s, ScopeState.Thinking, "思考中", "推理中…");
                    else Beat(s);
                    break;

                case TranscriptKind.Text:
                    if (authoritative) Set(s, ScopeState.Thinking, "正在回答", ToolClassifier.Clip(ev.Text, 200));
                    else
                    {
                        if (ev.Text is { Length: > 0 }) s.Detail = ToolClassifier.Clip(ev.Text, 200);
                        Beat(s);
                    }
                    break;

                case TranscriptKind.ToolUse:
                {
                    var c = ToolClassifier.Classify(ev.ToolName, ev.ToolInput);
                    if (authoritative) Set(s, c.State, c.Label, c.Detail);
                    else Beat(s);
                    break;
                }

                case TranscriptKind.ToolError:
                    Set(s, ScopeState.Error, "出错", ToolClassifier.Clip(ev.Text ?? "工具返回错误", 200), force: true);
                    break;

                // 打断没有任何 hook 会报，只有记录里那一行文本。所以它跟"出错"一样，
                // 不管这个会话是不是 hook 驱动的，都必须认。
                case TranscriptKind.Interrupted:
                    Set(s, ScopeState.Idle, "已打断", "你中断了这一轮", force: true);
                    break;

                case TranscriptKind.TurnEnd:
                    if (authoritative)
                        Set(s, ScopeState.Done, "完成", ToolClassifier.Clip(ev.Text ?? "本轮结束", 200), force: true);
                    else Beat(s);
                    break;

                default:
                    Beat(s);
                    break;
            }
        }
    }

    void Beat(ScopeSession s)
    {
        s.LastEventAt = DateTime.UtcNow;

        // 心跳也是活着的证据。"疑似卡住"是我们自己推断出来的，不是谁报上来的，
        // 所以任何一条事件都足以推翻它——否则一次误判会一直占着屏幕，
        // 而且它的优先级比"执行命令"高，会把真正在干活的会话顶下去。
        if (s.State == ScopeState.Stalled && s.StateBeforeStall is { } prev)
        {
            s.State = prev;
            s.Label = ScopeStateInfo.DefaultLabel(prev);
            s.Note = "";
            s.Since = DateTime.UtcNow;
            s.StateBeforeStall = null;
        }
        Touch();
    }
}

public enum TranscriptKind { UserPrompt, Thinking, Text, ToolUse, ToolError, TurnEnd, Interrupted, Heartbeat }

public sealed record TranscriptEvent(TranscriptKind Kind)
{
    /// 这条记录自己带的时间戳（UTC）。用来分辨"刚发生的"和"回放出来的历史"。
    public DateTime? At { get; init; }
    public string? Text { get; init; }
    public string? ToolName { get; init; }
    public JsonElement? ToolInput { get; init; }
    public string? Cwd { get; init; }
    public string? Source { get; init; }
    public string? Model { get; init; }
    public long? ContextTokens { get; init; }
}
