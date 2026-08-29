using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClaudeScope.Core;
using ClaudeScope.Server;

namespace ClaudeScope;

static class Program
{
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] pids, uint count);
    const int SW_HIDE = 0;

    /// 只有"这个控制台是为我们自己新建的"时才藏——
    /// 从已有终端里跑的时候藏掉会把用户的终端窗口一起隐藏。
    static bool OwnsConsole()
    {
        var buf = new uint[4];
        return GetConsoleProcessList(buf, 4) <= 1;
    }

    static void HideOwnConsole()
    {
        if (!OwnsConsole()) return;
        var h = GetConsoleWindow();
        if (h != IntPtr.Zero) ShowWindow(h, SW_HIDE);
    }

    [STAThread]
    static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var gui = mode is "" or "--run";
        if (gui) HideOwnConsole();

        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        try
        {
            return mode switch
            {
                "--hook" => RunHook(),
                "--daemon" => RunHost(foreground: true, withStrip: false),
                "--strip" => RunHost(foreground: true, withStrip: true),
                "" or "--run" => RunHost(foreground: false, withStrip: true),
                "--install" => Cli.Install(args),
                "--uninstall" => Cli.Uninstall(),
                "--stop" => Cli.Stop(),
                "--doctor" => Cli.Doctor(),
                "--reset-workarea" => Cli.ResetWorkArea(),
                "--state" => DumpState(),
                "--usage" => DumpUsage(),
                "--version" or "-v" => PrintVersion(),
                "--help" or "-h" or "/?" => PrintHelp(),
                _ => Unknown(mode)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"claude-scope 异常退出：{ex}");
            return 1;
        }
    }

    static int Unknown(string mode)
    {
        Console.Error.WriteLine($"未知参数：{mode}");
        PrintHelp();
        return 2;
    }

    static int PrintVersion()
    {
        Console.WriteLine($"claude-scope {ThisVersion}  (.NET {Environment.Version})");
        return 0;
    }

    public static string ThisVersion =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    static int PrintHelp()
    {
        Console.WriteLine("""
            claude-scope —— 实时显示 Claude 此刻在干什么

            用法:
              claude-scope                无参数：后台常驻（守护进程 + 横幅 + 托盘）
              claude-scope --install      写 hook 到 ~/.claude/settings.json + 注册开机自启
                                          可加 --port 45738 换端口、--no-autostart 只装 hook
              claude-scope --uninstall    撤销上面两件事（只删自己写的条目）
              claude-scope --stop         请运行中的实例正常退出（会归还 AppBar 占位）
              claude-scope --doctor       自检
              claude-scope --reset-workarea  救急：横幅被强杀后屏幕边上空了一条，用这个恢复
              claude-scope --daemon       前台只跑守护进程，日志直接打屏（排错用）
              claude-scope --strip        前台跑守护进程 + 横幅
              claude-scope --state        打印一次状态快照
              claude-scope --usage        打印额度读数和新鲜度
              claude-scope --hook         hook 入口，从 stdin 读 JSON（由 Claude 调用）
              claude-scope --version
            """);
        return 0;
    }

    /* ------------------------------------------------------------ hook 入口 */

    /// <summary>
    /// Claude 的 command 型 hook 会调这个。要求：极快、绝不阻塞、失败也安静退出。
    /// stdin 是一整个 JSON。
    /// </summary>
    static int RunHook()
    {
        string raw;
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var r = new StreamReader(stdin, Encoding.UTF8);
            raw = r.ReadToEnd();
        }
        catch { return 0; }

        if (string.IsNullOrWhiteSpace(raw)) return 0;

        // 有些调用方（比如 PowerShell 的管道）会在前面塞一个 BOM
        raw = raw.TrimStart('﻿');

        var cfg = ScopeConfig.Load();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var obj = new Dictionary<string, object?>();
            foreach (var p in doc.RootElement.EnumerateObject())
                obj[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => JsonSerializer.Deserialize<JsonElement>(p.Value.GetRawText())
                };

            // command 型 hook 比 http 型多知道一件事：入口类型
            var entry = Environment.GetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT");
            if (!string.IsNullOrEmpty(entry)) obj["source"] = entry;

            PostToDaemon(cfg, JsonSerializer.Serialize(obj));
        }
        catch
        {
            // 解析不了就原样转发，让守护进程那边去记这条坏数据
            PostToDaemon(cfg, raw);
        }
        return 0;
    }

    static void PostToDaemon(ScopeConfig cfg, string json)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            http.PostAsync($"http://{cfg.Host}:{cfg.Port}/hook", content).GetAwaiter().GetResult();
        }
        catch
        {
            // 守护进程没起来是常态，不吭声。连接被拒是毫秒级返回，不会拖慢 Claude。
        }
    }

    /* ------------------------------------------------------------ 守护进程 */

    /// <summary>
    /// 后台常驻：守护进程 + 横幅，同一个进程同一条 UI 线程。
    /// 用命名互斥量保证单实例——两个实例会抢同一个端口，
    /// 而且各注册一次 AppBar 占位，关掉一个另一个还占着位。
    /// </summary>
    static int RunHost(bool foreground, bool withStrip)
    {
        using var single = new Mutex(true, @"Local\claude-scope-single", out var isFirst);
        if (!isFirst)
        {
            Console.Error.WriteLine("claude-scope 已经在运行了。要重启先跑 claude-scope --stop");
            return 4;
        }

        ScopePaths.EnsureRuntimeDirs();
        var cfg = ScopeConfig.Load();
        using var log = new ScopeLogger(cfg.Log, echo: foreground);
        using var host = new ScopeHost(cfg, log);

        if (!host.StartBackend()) return 3;

        if (!withStrip)
        {
            var quit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
            if (foreground) Console.WriteLine($"守护进程已启动：http://{cfg.Host}:{cfg.Port}   Ctrl+C 退出");
            quit.Wait();
            log.Info("正在退出");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        // 建好 UI 线程的编组控件，HTTP 线程要靠它把动作切回 UI 线程
        host.InitUiThread();
        host.ShowStrip();
        host.ShowTray();
        log.Info("横幅已就位");
        Application.Run();
        log.Info("正在退出");
        return 0;
    }

    /* ------------------------------------------------------------ 排错命令 */

    static int DumpState()
    {
        var cfg = ScopeConfig.Load();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var s = http.GetStringAsync($"http://{cfg.Host}:{cfg.Port}/api/state").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(s);
            Console.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch
        {
            Console.Error.WriteLine($"连不上守护进程 http://{cfg.Host}:{cfg.Port} —— 先跑 claude-scope --daemon");
            return 1;
        }
    }

    static int DumpUsage()
    {
        var cfg = ScopeConfig.Load();
        using var log = new ScopeLogger(cfg.Log, echo: false);
        var u = new UsageReader(cfg, log).Read();
        if (u is null) { Console.WriteLine("额度显示已在配置里关闭（showUsage=false）"); return 0; }
        if (u.Problem is not null) { Console.WriteLine($"读不到额度：{u.Problem}"); return 1; }

        var at = DateTimeOffset.FromUnixTimeMilliseconds(u.SampledAt).ToLocalTime();
        Console.WriteLine($"5 小时窗口: {u.FiveHourPercent}%");
        Console.WriteLine($"7 天窗口:   {u.WeekPercent}%");
        Console.WriteLine($"采样时间:   {at:yyyy-MM-dd HH:mm:ss}（{u.AgeSeconds / 60:N1} 分钟前）{(u.Stale ? "  ← 已过期，不要当实时值" : "")}");
        Console.WriteLine();
        Console.WriteLine("这两个数来自桌面版写的 plan-usage-history.json，格式未文档化。");
        Console.WriteLine("第一次用请在 Claude Code 里跑 /usage 对一次数，确认对得上再信它。");
        return 0;
    }
}
