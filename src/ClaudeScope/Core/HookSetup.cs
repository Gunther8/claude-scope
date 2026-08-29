using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeScope.Core;

public sealed record HookResult(bool Ok, string Message, string? Backup = null);
public sealed record HookStatus(bool Installed, int Port, bool SettingsReadable, string Summary);

/// <summary>
/// 往 ~/.claude/settings.json 里装/卸 hook。
///
/// 这是整个程序唯一会改你已有配置的地方，所以规矩定死：
///   - 改之前一定备份
///   - 只认自己写的条目（按 url / command 特征），绝不动别人的
///   - settings.json 不是合法 JSON 时**拒绝写入**，而不是覆盖重建
///   - 幂等：装两次不会留下两份
///
/// 另外这里刻意不碰 %APPDATA%\Claude —— 那是 MSIX 重定向目录。
/// hook 走的是 ~/.claude，它在用户根目录下，不在重定向范围内。
/// </summary>
public static class HookSetup
{
    /// http 型 hook：直接 POST，不启动任何子进程，既不吃 PATH 也几乎不给 Claude 增加延迟
    static readonly (string Event, bool NeedsMatcher)[] HttpEvents =
    {
        ("UserPromptSubmit", false),
        // 斜杠命令展开成 prompt 时才发这个。/compact 之类的内置命令不走 UserPromptSubmit，
        // 少了它，你敲完斜杠命令到第一个工具调用之间横幅是死的。
        ("UserPromptExpansion", false),
        ("PreToolUse", true),
        ("PostToolUse", true),
        ("PostToolUseFailure", true),
        ("PermissionRequest", false),
        ("PermissionDenied", false),
        ("Notification", false),
        // 压缩上下文期间一条事件都没有，可能静默好几分钟。
        // 不装这两个，横幅就只能显示"空闲"——纯粹的漏报。
        ("PreCompact", false),
        ("PostCompact", false),
        // MCP 工具要你输入。Notification 的 elicitation_dialog 只是间接兜底，
        // 这两个才是正主。
        ("Elicitation", false),
        ("ElicitationResult", false),
        ("SubagentStart", false),
        ("Stop", false),
        ("StopFailure", false),
        ("SubagentStop", false),
        ("SessionEnd", false)
    };

    /// 我们装了哪些事件（含 SessionStart 这个 command 型的）。自检拿它对账。
    public static IEnumerable<string> InstalledEvents =>
        new[] { "SessionStart" }.Concat(HttpEvents.Select(e => e.Event));

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    static bool IsOurs(JsonNode? entry)
    {
        if (entry?["hooks"] is not JsonArray hooks) return false;
        foreach (var h in hooks)
        {
            if (h?["url"]?.GetValue<string>() is { } url &&
                url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) &&
                url.EndsWith("/hook", StringComparison.Ordinal))
                return true;

            if (h?["command"]?.GetValue<string>() is { } cmd &&
                (cmd.Contains("claude-scope", StringComparison.OrdinalIgnoreCase) ||
                 cmd.Contains("hook-session-start", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    static void StripOurs(JsonObject hooks)
    {
        foreach (var key in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[key] is not JsonArray arr) continue;
            var keep = new JsonArray();
            foreach (var item in arr.ToList())
            {
                arr.Remove(item);
                if (!IsOurs(item)) keep.Add(item);
            }
            if (keep.Count == 0) hooks.Remove(key);
            else hooks[key] = keep;
        }
    }

    static JsonObject? ReadSettings(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(ScopePaths.ClaudeSettings)) return new JsonObject();
            var raw = File.ReadAllText(ScopePaths.ClaudeSettings).TrimStart('﻿');
            if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
            return JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            error = $"~/.claude/settings.json 不是合法 JSON（{ex.Message}）";
            return null;
        }
        catch (Exception ex)
        {
            error = $"读不了 ~/.claude/settings.json：{ex.GetType().Name}";
            return null;
        }
    }

    static string Backup()
    {
        var path = $"{ScopePaths.ClaudeSettings}.claude-scope-backup-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}";
        File.Copy(ScopePaths.ClaudeSettings, path, overwrite: true);
        return path;
    }

    public static HookResult Install(int port)
    {
        var settings = ReadSettings(out var error);
        if (settings is null)
            return new HookResult(false, $"{error}。请先修好它再装——我不敢覆盖你的配置。");

        string? backup = null;
        try
        {
            Directory.CreateDirectory(ScopePaths.ClaudeDir);
            if (File.Exists(ScopePaths.ClaudeSettings)) backup = Backup();

            if (settings["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                settings["hooks"] = hooks;
            }
            StripOurs(hooks);

            var url = $"http://127.0.0.1:{port}/hook";

            // 唯一的 command 型 hook：一个会话只跑一次，延迟无所谓。
            // 它比 http 型多知道一件事——CLAUDE_CODE_ENTRYPOINT（桌面版还是 CLI）。
            var sessionStart = new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = $"\"{ScopePaths.ExePath}\" --hook",
                    ["timeout"] = 8
                })
            };
            if (hooks["SessionStart"] is not JsonArray ss) hooks["SessionStart"] = ss = new JsonArray();
            ss.Add(sessionStart);

            foreach (var (ev, needsMatcher) in HttpEvents)
            {
                var entry = new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "http",
                        ["url"] = url,
                        ["timeout"] = 2
                    })
                };
                if (needsMatcher) entry["matcher"] = ".*";
                if (hooks[ev] is not JsonArray arr) hooks[ev] = arr = new JsonArray();
                arr.Add(entry);
            }

            File.WriteAllText(ScopePaths.ClaudeSettings, settings.ToJsonString(Pretty) + Environment.NewLine);

            var msg = "hook 已写入 ~/.claude/settings.json。" + Environment.NewLine + Environment.NewLine
                    + "用户级 hook 是热加载的，已经开着的会话也会立刻开始发事件。" + Environment.NewLine
                    + "（项目级 .claude/settings.json 不是热加载的，那种要重开会话。）";
            if (backup is not null) msg += Environment.NewLine + Environment.NewLine + "原配置已备份到：" + Environment.NewLine + backup;
            return new HookResult(true, msg, backup);
        }
        catch (Exception ex)
        {
            return new HookResult(false, $"写入失败：{ex.Message}", backup);
        }
    }

    public static HookResult Uninstall()
    {
        var settings = ReadSettings(out var error);
        if (settings is null) return new HookResult(false, $"{error}。没动它。");

        try
        {
            if (settings["hooks"] is not JsonObject hooks)
                return new HookResult(true, "settings.json 里本来就没有 hooks 段。");

            string? backup = null;
            if (File.Exists(ScopePaths.ClaudeSettings)) backup = Backup();

            StripOurs(hooks);
            if (hooks.Count == 0) settings.Remove("hooks");

            File.WriteAllText(ScopePaths.ClaudeSettings, settings.ToJsonString(Pretty) + Environment.NewLine);

            var msg = "hook 已从 ~/.claude/settings.json 移除，你原有的配置一个字没动。";
            if (backup is not null) msg += Environment.NewLine + Environment.NewLine + "改前备份：" + Environment.NewLine + backup;
            return new HookResult(true, msg, backup);
        }
        catch (Exception ex)
        {
            return new HookResult(false, $"移除失败：{ex.Message}");
        }
    }

    public static HookStatus Status(int expectedPort)
    {
        var settings = ReadSettings(out var error);
        if (settings is null)
            return new HookStatus(false, 0, false, error ?? "settings.json 读不了");

        var installed = false;
        var port = 0;
        if (settings["hooks"] is JsonObject hooks)
        {
            foreach (var (_, node) in hooks)
            {
                if (node is not JsonArray arr) continue;
                foreach (var entry in arr)
                {
                    if (!IsOurs(entry)) continue;
                    installed = true;
                    if (entry?["hooks"] is JsonArray hs)
                        foreach (var h in hs)
                            if (h?["url"]?.GetValue<string>() is { } u &&
                                int.TryParse(u.Split(':').LastOrDefault()?.Replace("/hook", ""), out var p))
                                port = p;
                }
            }
        }

        string summary;
        if (!installed)
            summary = "hook 未安装。不装也能用——通道②（会话记录）零配置就在跑，"
                    + Environment.NewLine + "只是拿不到「等你确认」和「会话结束」这两个状态。";
        else if (port != 0 && port != expectedPort)
            summary = $"hook 已安装，但它指向端口 {port}，而本程序在 {expectedPort} 上。"
                    + Environment.NewLine + "点「安装 hook」重写一次即可对齐。";
        else
            summary = $"hook 已安装，指向 127.0.0.1:{expectedPort}。"
                    + Environment.NewLine + "用户级 hook 热加载，已开着的会话也会发事件。";

        return new HookStatus(installed, port, true, summary);
    }
}
