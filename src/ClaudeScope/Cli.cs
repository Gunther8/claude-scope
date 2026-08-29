using System.Text.Json;
using ClaudeScope.Core;

namespace ClaudeScope;

/// <summary>命令行入口：安装、卸载、停止、自检。</summary>
public static class Cli
{
    static int PortFrom(string[] args, ScopeConfig cfg)
    {
        var i = Array.FindIndex(args, a => a.Equals("--port", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p is >= 1024 and <= 65535)
            return p;
        return cfg.Port;
    }

    public static int Install(string[] args)
    {
        var cfg = ScopeConfig.Load();
        var port = PortFrom(args, cfg);
        if (port != cfg.Port)
        {
            cfg.Port = port;
            cfg.Save();
            Console.WriteLine($"端口已改为 {port} 并写入 config.json");
        }

        Console.WriteLine("写入 hook ...");
        var r = HookSetup.Install(port);
        Console.WriteLine(r.Ok ? "  " + r.Message.Replace(Environment.NewLine, Environment.NewLine + "  ")
                               : "  失败：" + r.Message);
        if (!r.Ok) return 1;

        if (!args.Contains("--no-autostart", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine("注册开机自启 ...");
            var (ok, msg) = Autostart.Enable();
            Console.WriteLine("  " + msg);
        }

        Console.WriteLine();
        Console.WriteLine("装完了。现在直接双击 claude-scope.exe 就能用。");
        Console.WriteLine($"自检：\"{ScopePaths.ExePath}\" --doctor");
        return 0;
    }

    public static int Uninstall()
    {
        Console.WriteLine("移除 hook ...");
        var r = HookSetup.Uninstall();
        Console.WriteLine("  " + r.Message.Replace(Environment.NewLine, Environment.NewLine + "  "));

        Console.WriteLine();
        Console.WriteLine("取消开机自启 ...");
        var (_, msg) = Autostart.Disable();
        Console.WriteLine("  " + msg);

        Console.WriteLine();
        Console.WriteLine($"运行时数据还留在 {ScopePaths.RuntimeDir}，想彻底清掉可以手动删。");
        return r.Ok ? 0 : 1;
    }

    /// <summary>
    /// 停止运行中的实例。**必须走正常退出路径**——直接杀进程会跳过 ABM_REMOVE，
    /// 屏幕边上会留下一条收不回来的空白。所以这里是请它自己退。
    /// </summary>
    public static int Stop()
    {
        var cfg = ScopeConfig.Load();
        var token = ReadToken();
        if (token is null)
        {
            Console.WriteLine("没找到运行时信息，程序可能没在跑。");
            return 0;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Add("x-scope-token", token);
            using var content = new StringContent("{\"action\":\"quit\"}", System.Text.Encoding.UTF8, "application/json");
            var res = http.PostAsync($"http://{cfg.Host}:{cfg.Port}/api/control", content).GetAwaiter().GetResult();
            Console.WriteLine(res.IsSuccessStatusCode
                ? "已请求退出，AppBar 占位会在退出时归还。"
                : $"请求返回 {(int)res.StatusCode}");
            return res.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            Console.WriteLine("连不上运行中的实例，它可能已经退了。");
            return 0;
        }
    }

    static string? ReadToken()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ScopePaths.RuntimeFile));
            return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 救急：把每块屏的工作区恢复成整屏。
    ///
    /// 这个必须是独立 CLI 命令，不能只放在菜单里——需要它的时候程序往往已经死了
    /// （被任务管理器结束、蓝屏、断电），AppBar 占位没来得及 ABM_REMOVE，
    /// 屏幕边上留下一条永远收不回来的空白。
    /// 任务栏会自己重新占回它那份，不用担心把任务栏挤掉。
    /// </summary>
    public static int ResetWorkArea()
    {
        Console.WriteLine();
        Console.WriteLine("恢复工作区 ...");
        var i = 0;
        var done = 0;
        foreach (var s in Screen.AllScreens)
        {
            var r = new Native.RECT
            {
                Left = s.Bounds.Left,
                Top = s.Bounds.Top,
                Right = s.Bounds.Right,
                Bottom = s.Bounds.Bottom
            };
            var ok = Native.Win32.SystemParametersInfo(
                Native.Win32.SPI_SETWORKAREA, 0, ref r, Native.Win32.SPIF_SENDCHANGE);
            Console.WriteLine($"  Index {i}  {s.DeviceName}  ->  {(ok ? "已恢复" : "失败")}");
            if (ok) done++;
            i++;
        }
        Console.WriteLine();
        Console.WriteLine("任务栏会在几秒内重新占回它那一份。");
        Console.WriteLine("还是不对的话，重启资源管理器：Stop-Process -Name explorer -Force");
        Console.WriteLine();
        return done > 0 ? 0 : 1;
    }

    /* ---------------------------------------------------------------- 自检 */

    public static int Doctor()
    {
        var cfg = ScopeConfig.Load();
        var problems = 0;

        void Ok(string name, string detail) =>
            Console.WriteLine($"  [ OK ] {name,-14} {detail}");
        void Bad(string name, string detail, string fix)
        {
            Console.WriteLine($"  [FAIL] {name,-14} {detail}");
            if (fix.Length > 0) Console.WriteLine($"         → {fix}");
            problems++;
        }
        void Info(string name, string detail) =>
            Console.WriteLine($"  [ 信息 ] {name,-12} {detail}");

        Console.WriteLine();
        Console.WriteLine("=== claude-scope 自检 ===");
        Console.WriteLine();

        Ok("程序", $"{ScopePaths.ExePath}");
        Ok("配置", ScopePaths.ConfigFile + (File.Exists(ScopePaths.ConfigFile) ? "" : "（还没生成，用的是默认值）"));

        foreach (var issue in cfg.LoadIssues)
            Bad("配置内容", issue.Text, "");

        // 运行中的实例
        var alive = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var s = http.GetStringAsync($"http://{cfg.Host}:{cfg.Port}/health").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(s);
            var pid = doc.RootElement.GetProperty("pid").GetInt32();
            Ok("运行中", $"http://{cfg.Host}:{cfg.Port}  pid={pid}");
            alive = true;
        }
        catch
        {
            Bad("运行中", $"{cfg.Host}:{cfg.Port} 没有响应", $"双击 claude-scope.exe，或 \"{ScopePaths.ExePath}\" --daemon");
        }

        // hook
        var hook = HookSetup.Status(cfg.Port);
        if (!hook.SettingsReadable)
            Bad("hook", hook.Summary, "先修好 ~/.claude/settings.json，否则 Claude 自己也读不了它");
        else if (hook.Installed)
            Ok("hook", hook.Summary.Split(Environment.NewLine)[0]);
        else
            Bad("hook", "未安装", $"\"{ScopePaths.ExePath}\" --install");

        // 会话记录
        var pdir = cfg.ResolvedProjectsDir;
        if (Directory.Exists(pdir))
        {
            var recent = 0;
            try
            {
                recent = Directory.EnumerateFiles(pdir, "*.jsonl", SearchOption.AllDirectories)
                    .Count(f => DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromHours(24));
            }
            catch { }
            Ok("会话记录", $"{pdir}（近 24 小时 {recent} 个活跃文件）");
        }
        else
        {
            Bad("会话记录", $"找不到 {pdir}", "在 Claude 里开一个会话，这个目录会自动出现");
        }

        // 额度
        using (var log = new ScopeLogger(cfg.Log, echo: false))
        {
            var u = new UsageReader(cfg, log).Read();
            if (u is null) Info("额度", "已在配置里关闭");
            else if (u.Problem is not null) Info("额度", u.Problem);
            else Info("额度", $"5h {u.FiveHourPercent}%  周 {u.WeekPercent}%  （采样 {u.AgeSeconds / 60:N1} 分钟前{(u.Stale ? "，已过期" : "")}）");
        }

        Info("开机自启", Autostart.IsEnabled() ? "已注册" : $"未注册（\"{ScopePaths.ExePath}\" --install 可注册）");
        Info("MSIX 提示", "%APPDATA%\\Claude 是重定向目录，本程序不碰它；hook 走没被重定向的 ~/.claude");

        if (alive)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var s = http.GetStringAsync($"http://{cfg.Host}:{cfg.Port}/api/state").GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                var d = root.GetProperty("daemon");
                Console.WriteLine();
                Console.WriteLine("当前状态：");
                if (root.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    Console.WriteLine($"  {p.GetProperty("label").GetString()} · {p.GetProperty("project").GetString()} · {p.GetProperty("source").GetString()}");
                    Console.WriteLine($"  在做：{p.GetProperty("detail").GetString()}");
                }
                else Console.WriteLine("  没有活跃会话");
                Console.WriteLine($"  hook 事件 {d.GetProperty("hookCount").GetInt64()}   记录事件 {d.GetProperty("transcriptCount").GetInt64()}");

                // 逐类列出来，是为了能一眼看出"某个状态到底有没有事件源在供"。
                // 装了却始终为 0 的事件，说明这个客户端根本不发它——
                // 它对应的状态就永远不会出现，这一点必须让人看见，不能默默漏报。
                if (d.TryGetProperty("hookEvents", out var he) && he.ValueKind == JsonValueKind.Object)
                {
                    var seen = new Dictionary<string, long>(StringComparer.Ordinal);
                    foreach (var kv in he.EnumerateObject()) seen[kv.Name] = kv.Value.GetInt64();
                    Console.WriteLine();
                    Console.WriteLine("  已安装的 hook 各收到多少条（本次运行以来）：");
                    foreach (var name in HookSetup.InstalledEvents)
                    {
                        var n = seen.TryGetValue(name, out var c) ? c : 0;
                        Console.WriteLine($"    {(n > 0 ? "有" : "零")}  {name,-20} {n}");
                        seen.Remove(name);
                    }
                    foreach (var (name, n) in seen)
                        Console.WriteLine($"    ?   {name,-20} {n}  （没装却收到了）");
                    Console.WriteLine();
                    Console.WriteLine("  「零」不一定是坏事：程序刚起来、或者这段时间就是没发生过该事件。");
                    Console.WriteLine("  但长期为零，就说明这个客户端不发它，对应状态不会出现。");
                }

                if (d.TryGetProperty("hookFields", out var hf) && hf.ValueKind == JsonValueKind.Array)
                {
                    var fields = hf.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                    Console.WriteLine();
                    Console.WriteLine("  hook 负载里出现过的字段：" + (fields.Count == 0 ? "（还没收到过）" : string.Join(", ", fields)));
                    if (fields.Count > 0 && !fields.Contains("pid"))
                    {
                        Console.WriteLine("  注意：负载里没有 pid。也就是说「进程已经死了」和「它只是在慢慢干活」");
                        Console.WriteLine("        无法区分，「疑似卡住」纯粹是按静默时长推断的，可能误报。");
                    }
                }

                if (d.GetProperty("hookCount").GetInt64() == 0 && hook.Installed)
                {
                    Console.WriteLine();
                    Console.WriteLine("  一条 hook 都没收到。用户级 hook 是热加载的，多半只是这会儿没有会话在动——");
                    Console.WriteLine("  让 Claude 随便跑一步，再看一次。");
                }
            }
            catch { }
        }

        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "全部通过。" : $"{problems} 项不通过，按上面的 → 提示处理。");
        Console.WriteLine();
        return problems == 0 ? 0 : 1;
    }
}
