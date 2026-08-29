using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeScope.Core;

public enum ScopeState
{
    Idle, Thinking, Writing, Running, Done, Error, Waiting, Stalled, Dead, Disconnected
}

public static class ScopeStateInfo
{
    /// 多会话仲裁：最需要你介入的那个占据横幅
    public static int Priority(ScopeState s) => s switch
    {
        ScopeState.Waiting => 100,
        ScopeState.Error => 90,
        ScopeState.Stalled => 85,
        ScopeState.Running => 60,
        ScopeState.Writing => 58,
        ScopeState.Thinking => 50,
        ScopeState.Done => 30,
        ScopeState.Idle => 12,
        ScopeState.Dead => 8,
        _ => 0
    };

    /// 状态的默认中文名。撤回一次误判时要把标题一起换回来，
    /// 而 Core 不该反过来依赖 Ui，所以文案在这儿也留一份。
    public static string DefaultLabel(ScopeState s) => s switch
    {
        ScopeState.Idle => "空闲",
        ScopeState.Thinking => "思考中",
        ScopeState.Writing => "写代码",
        ScopeState.Running => "执行命令",
        ScopeState.Done => "完成",
        ScopeState.Error => "出错",
        ScopeState.Waiting => "等你确认",
        ScopeState.Stalled => "疑似卡住",
        ScopeState.Dead => "会话已结束",
        _ => "状态源断开"
    };

    public static string Wire(ScopeState s) => s switch
    {
        ScopeState.Idle => "idle",
        ScopeState.Thinking => "thinking",
        ScopeState.Writing => "writing",
        ScopeState.Running => "running",
        ScopeState.Done => "done",
        ScopeState.Error => "error",
        ScopeState.Waiting => "waiting",
        ScopeState.Stalled => "stalled",
        ScopeState.Dead => "dead",
        _ => "disconnected"
    };

    public static ScopeState? Parse(string? wire) => wire switch
    {
        "idle" => ScopeState.Idle,
        "thinking" => ScopeState.Thinking,
        "writing" => ScopeState.Writing,
        "running" => ScopeState.Running,
        "done" => ScopeState.Done,
        "error" => ScopeState.Error,
        "waiting" => ScopeState.Waiting,
        "stalled" => ScopeState.Stalled,
        "dead" => ScopeState.Dead,
        "disconnected" => ScopeState.Disconnected,
        _ => null
    };
}

public readonly record struct ToolVerdict(ScopeState State, string Label, string Detail);

public static partial class ToolClassifier
{
    static readonly Dictionary<string, string> SourceLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-desktop"] = "桌面版",
        ["desktop"] = "桌面版",
        ["cli"] = "CLI",
        ["vscode"] = "VS Code",
        ["jetbrains"] = "JetBrains",
        ["sdk"] = "SDK"
    };

    public static string SourceLabel(string? source) =>
        string.IsNullOrEmpty(source) ? "本地会话"
        : SourceLabels.TryGetValue(source, out var l) ? l : source;

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    public static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = Whitespace().Replace(text, " ").Trim();
        return s.Length > max ? string.Concat(s.AsSpan(0, max - 1), "…") : s;
    }

    public static string BaseName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var parts = path.Split('\\', '/');
        for (var i = parts.Length - 1; i >= 0; i--)
            if (parts[i].Length > 0) return parts[i];
        return "";
    }

    static string? Str(JsonElement? input, params string[] keys)
    {
        if (input is not { ValueKind: JsonValueKind.Object } obj) return null;
        foreach (var k in keys)
            if (obj.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    /// <summary>
    /// 工具名 -> 状态。要区分的七种状态里，「写代码 / 跑命令 / 在想」都是从这里分出来的。
    /// </summary>
    public static ToolVerdict Classify(string? name, JsonElement? input)
    {
        if (string.IsNullOrEmpty(name))
            return new(ScopeState.Thinking, "思考中", "");

        // 这几个工具的本质就是"停下来等人"——PreToolUse 一响，Claude 就卡在那儿了。
        // 之前它们落到最后的兜底分支，被显示成黄色的"执行命令"，然后一路等到
        // 180 秒被看门狗判成"疑似卡住"。等你确认是优先级最高的状态，不能这么漏。
        if (name is "AskUserQuestion")
            return new(ScopeState.Waiting, "等你选择",
                Clip(Str(input, "question") ?? "在问你一个问题", 160));

        if (name is "ExitPlanMode")
            return new(ScopeState.Waiting, "等你批计划", "方案写好了，等你点同意");

        if (name is "Bash" or "PowerShell")
            return new(ScopeState.Running, "执行命令",
                Clip(Str(input, "command", "description") ?? name, 160));

        if (name is "Edit" or "Write" or "MultiEdit" or "NotebookEdit")
        {
            var f = BaseName(Str(input, "file_path", "notebook_path"));
            return new(ScopeState.Writing, "写代码", Clip(f.Length > 0 ? $"{name} · {f}" : name, 160));
        }

        if (name is "Read" or "Glob" or "Grep")
        {
            var f = BaseName(Str(input, "file_path", "path"));
            if (f.Length == 0) f = Str(input, "pattern") ?? "";
            return new(ScopeState.Thinking, "查阅代码", Clip(f.Length > 0 ? $"{name} · {f}" : name, 160));
        }

        if (name is "WebFetch" or "WebSearch")
            return new(ScopeState.Thinking, "查资料", Clip(Str(input, "url", "query") ?? name, 160));

        if (name is "Task" or "Agent")
            return new(ScopeState.Thinking, "派子任务",
                Clip(Str(input, "description", "subagent_type") ?? name, 160));

        if (name.StartsWith("mcp__", StringComparison.Ordinal))
            return new(ScopeState.Running, "调用工具",
                Clip(name[5..].Replace("__", " · "), 160));

        if (name is "TodoWrite" or "Skill" or "SlashCommand")
            return new(ScopeState.Thinking, "整理任务", Clip(name, 160));

        return new(ScopeState.Running, "调用工具", Clip(name, 160));
    }
}
