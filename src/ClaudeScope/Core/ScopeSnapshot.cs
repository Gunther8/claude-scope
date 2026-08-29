using System.Text.Json.Serialization;

namespace ClaudeScope.Core;

/// <summary>
/// 发给控制台的快照。字段名和旧的 JS 版保持一致——
/// 这样现有控制台页面不用改就能直接对拍验证。
/// </summary>
public sealed class ScopeSnapshot
{
    public long Ts { get; init; }
    public long Rev { get; init; }
    public required DaemonView Daemon { get; init; }
    public required SoundConfig Sound { get; init; }
    public required ThresholdConfig Thresholds { get; init; }
    public ScopeCommand? Command { get; init; }
    public SessionView? Primary { get; init; }
    public required List<SessionView> Others { get; init; }
    public int Total { get; init; }
    public UsageView? Usage { get; set; }
}

public sealed class DaemonView
{
    public long StartedAt { get; init; }
    public int Port { get; init; }
    public long HookCount { get; init; }
    public long TranscriptCount { get; init; }
    public bool WatchTranscripts { get; init; }
    /// 各类 hook 事件各收到过多少条。用来回答"某个状态到底有没有事件源在供"——
    /// 一个始终为 0 的事件，就意味着它对应的状态永远不会出现。
    public required SortedDictionary<string, long> HookEvents { get; init; }
    /// hook 负载里实际出现过的字段名。用来回答"某个信息到底拿不拿得到"，
    /// 比如 pid——拿不到就意味着"已终止"和"在慢慢干活"根本分不开，这一点不能靠猜。
    public required List<string> HookFields { get; init; }
    public required List<ScopeIssue> Issues { get; init; }
}

public sealed class ChannelView
{
    public bool Hook { get; init; }
    public bool Transcript { get; init; }
}

public sealed class SessionView
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Project { get; init; }
    public required string Cwd { get; init; }
    public required string State { get; init; }
    public required string Label { get; init; }
    public required string Detail { get; init; }
    public required string Note { get; init; }
    public long Since { get; init; }
    public long LastEventAt { get; init; }
    public bool LongRunning { get; init; }
    public required ChannelView Channels { get; init; }
    public int? Pid { get; init; }
    public string? Model { get; init; }
    public long? ContextTokens { get; init; }

    /// 排序用，不序列化出去
    [JsonIgnore] public ScopeState RawState { get; init; }

    public static SessionView From(ScopeSession s, DateTime now, double longRunSeconds) => new()
    {
        Id = s.Id,
        Source = ToolClassifier.SourceLabel(s.Source),
        Project = s.Project.Length > 0 ? s.Project : "(未知项目)",
        Cwd = s.Cwd,
        State = ScopeStateInfo.Wire(s.State),
        RawState = s.State,
        Label = s.Label,
        Detail = s.Detail,
        Note = s.Note,
        Since = new DateTimeOffset(s.Since).ToUnixTimeMilliseconds(),
        LastEventAt = new DateTimeOffset(s.LastEventAt).ToUnixTimeMilliseconds(),
        LongRunning = s.ToolStartedAt is { } t && (now - t).TotalSeconds > longRunSeconds,
        Channels = new ChannelView { Hook = s.HookSeen, Transcript = s.TranscriptSeen },
        Pid = s.Pid,
        Model = s.Model,
        ContextTokens = s.ContextTokens
    };
}

/// <summary>
/// 额度。这几个数来自桌面版写的 plan-usage-history.json，格式未文档化，
/// 所以一定要把采样时间和"信不信得过"一起交出去，让界面自己决定怎么显示。
/// </summary>
public sealed class UsageView
{
    /// 5 小时窗口用量百分比
    public int? FiveHourPercent { get; init; }
    /// 7 天窗口用量百分比
    public int? WeekPercent { get; init; }
    /// 采样时刻
    public long SampledAt { get; init; }
    /// 距今多少秒
    public double AgeSeconds { get; init; }
    /// 超过阈值就是不新鲜——界面该变灰，绝不能外推
    public bool Stale { get; init; }
    /// 读不到 / 格式不认识时说明原因，而不是显示 0
    public string? Problem { get; init; }
}
