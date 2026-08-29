using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeScope.Core;

public sealed class SoundConfig
{
    public bool Waiting { get; set; } = true;
    public bool Error { get; set; } = true;
    public double MinIntervalSeconds { get; set; } = 15;
}

public sealed class StripConfig
{
    /// -1 = 自动选一块非主显示器
    public int Monitor { get; set; } = -1;
    public string Edge { get; set; } = "top";
    public int Height { get; set; } = 52;
    public bool Reserve { get; set; } = true;
}

public sealed class ThresholdConfig
{
    public double LongRunSeconds { get; set; } = 30;
    /// 多久没有任何事件才怀疑它卡住了。
    /// 180 秒太紧：一条跑五分钟的构建、一次超长的推理，中间本来就没有事件。
    public double StallSeconds { get; set; } = 300;
    /// 判成"疑似卡住"之后再静默这么久，就转成"会话已结束"，退出对横幅的争夺。
    public double DeadAfterStallSeconds { get; set; } = 300;
    public double DoneDecaySeconds { get; set; } = 20;
    public double ErrorStickySeconds { get; set; } = 10;
    public double ErrorMinDisplaySeconds { get; set; } = 5;
    public double SessionTtlMinutes { get; set; } = 30;
    public double TranscriptHotMinutes { get; set; } = 10;
    /// 会话记录里比这个更旧的行只当补充信息，不许再改状态。
    /// 尾随是从"文件末尾往回 64KB"接手的，那段里全是历史事件——
    /// 没有这道闸门，重新接管一个冷却过的会话就会把十几分钟前的错误重新点红。
    public double TranscriptStaleSeconds { get; set; } = 90;
    /// 压缩上下文期间允许静默多久不报"疑似卡住"。实测一次手动 compact 用了 164 秒。
    public double CompactGraceSeconds { get; set; } = 480;
    /// 额度采样超过这个时长就认为不新鲜，显示成灰的而不是当真
    public double UsageStaleMinutes { get; set; } = 30;
}

public sealed class LogConfig
{
    public long MaxBytes { get; set; } = 1024 * 1024;
    public int Keep { get; set; } = 3;
    public double ThrottleSeconds { get; set; } = 30;
}

public sealed class ScopeConfig
{
    public int Port { get; set; } = 45737;
    public string Host { get; set; } = "127.0.0.1";
    public bool WatchTranscripts { get; set; } = true;
    public string? ProjectsDir { get; set; }
    public bool ShowUsage { get; set; } = true;
    public SoundConfig Sound { get; set; } = new();
    public StripConfig Strip { get; set; } = new();
    public ThresholdConfig Thresholds { get; set; } = new();
    public LogConfig Log { get; set; } = new();

    /// 加载时发现的问题，会随快照一起报给界面——坏配置不该让程序起不来，
    /// 但也不该悄悄退回默认值让你以为设置生效了。
    [JsonIgnore] public List<ScopeIssue> LoadIssues { get; } = new();

    [JsonIgnore]
    public string ResolvedProjectsDir =>
        string.IsNullOrWhiteSpace(ProjectsDir) ? ScopePaths.ProjectsDir : ProjectsDir!;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ScopeConfig Load()
    {
        var cfg = new ScopeConfig();

        string? raw = null;
        try
        {
            if (File.Exists(ScopePaths.ConfigFile))
                raw = File.ReadAllText(ScopePaths.ConfigFile);
        }
        catch (Exception ex)
        {
            cfg.LoadIssues.Add(new ScopeIssue("config-unreadable", $"读不到 config.json（{ex.GetType().Name}），已使用默认配置"));
        }

        if (raw is not null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ScopeConfig>(raw.TrimStart('﻿'), JsonOpts);
                if (parsed is not null) cfg = parsed;
            }
            catch (JsonException ex)
            {
                cfg.LoadIssues.Add(new ScopeIssue("config-invalid", $"config.json 不是合法 JSON（{ex.Message}），已使用默认配置"));
            }
        }

        cfg.Validate();
        return cfg;
    }

    /// 明显不合理的值拽回来，并且说清楚被拽了——不静默修正。
    void Validate()
    {
        if (Port is < 1024 or > 65535)
        {
            LoadIssues.Add(new ScopeIssue("config-port", $"port={Port} 不合法，已退回 45737"));
            Port = 45737;
        }

        var t = Thresholds;
        var def = new ThresholdConfig();

        // 用属性访问器成对传，避免为每个字段抄一遍 if
        (string Name, Func<double> Get, Action<double> Set, double Default)[] checks =
        {
            ("longRunSeconds",         () => t.LongRunSeconds,         v => t.LongRunSeconds = v,         def.LongRunSeconds),
            ("stallSeconds",           () => t.StallSeconds,           v => t.StallSeconds = v,           def.StallSeconds),
            ("deadAfterStallSeconds",  () => t.DeadAfterStallSeconds,  v => t.DeadAfterStallSeconds = v,  def.DeadAfterStallSeconds),
            ("doneDecaySeconds",       () => t.DoneDecaySeconds,       v => t.DoneDecaySeconds = v,       def.DoneDecaySeconds),
            ("errorStickySeconds",     () => t.ErrorStickySeconds,     v => t.ErrorStickySeconds = v,     def.ErrorStickySeconds),
            ("errorMinDisplaySeconds", () => t.ErrorMinDisplaySeconds, v => t.ErrorMinDisplaySeconds = v, def.ErrorMinDisplaySeconds),
            ("sessionTtlMinutes",      () => t.SessionTtlMinutes,      v => t.SessionTtlMinutes = v,      def.SessionTtlMinutes),
            ("transcriptHotMinutes",   () => t.TranscriptHotMinutes,   v => t.TranscriptHotMinutes = v,   def.TranscriptHotMinutes),
            ("transcriptStaleSeconds", () => t.TranscriptStaleSeconds, v => t.TranscriptStaleSeconds = v, def.TranscriptStaleSeconds),
            ("compactGraceSeconds",    () => t.CompactGraceSeconds,    v => t.CompactGraceSeconds = v,    def.CompactGraceSeconds),
            ("usageStaleMinutes",      () => t.UsageStaleMinutes,      v => t.UsageStaleMinutes = v,      def.UsageStaleMinutes),
        };

        foreach (var (name, get, set, fallback) in checks)
        {
            var v = get();
            if (double.IsFinite(v) && v > 0) continue;
            LoadIssues.Add(new ScopeIssue("config-threshold", $"thresholds.{name} 不合法，已退回 {fallback}"));
            set(fallback);
        }

        Strip.Height = Math.Clamp(Strip.Height, 28, 200);
        if (Strip.Edge != "top" && Strip.Edge != "bottom") Strip.Edge = "top";
    }

    public void Save()
    {
        ScopePaths.EnsureRuntimeDirs();
        var tmp = ScopePaths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts) + Environment.NewLine);
        File.Move(tmp, ScopePaths.ConfigFile, overwrite: true);
    }
}

public sealed record ScopeIssue(string Code, string Text)
{
    public long At { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public int Count { get; set; } = 1;
}
