using ClaudeScope.Core;
using ClaudeScope.Server;
using ClaudeScope.Ui;

namespace ClaudeScope;

/// <summary>
/// 一个进程装下全部：守护进程 + 横幅。
///
/// 旧版是三个进程（node 守护 + PowerShell 横幅 + PowerShell 托盘）靠 HTTP 互相通信。
/// 合成一个之后：
///   - 横幅直接读状态机对象，不再 HTTP 轮询，也不用管数据新鲜度
///   - 换显示器/贴边只是重建窗口，不用「杀旧进程 → 等它收 AppBar → 起新进程」那套舞蹈
///   - 内存从三份降到一份
/// 控制台仍然走 HTTP（它是网页），hook 也仍然 POST 到本机端口。
/// </summary>
public sealed class ScopeHost : IDisposable, Ui.IStripActions
{
    readonly ScopeConfig _cfg;
    readonly ScopeLogger _log;
    readonly ScopeRegistry _registry;
    readonly UsageReader _usage;
    readonly ScopeHttpServer _server;

    TranscriptWatcher? _watcher;
    System.Threading.Timer? _ticker;
    StripForm? _strip;
    ScopeTray? _tray;
    SettingsForm? _settings;

    public ScopeHost(ScopeConfig cfg, ScopeLogger log)
    {
        _cfg = cfg;
        _log = log;
        _registry = new ScopeRegistry(cfg, log);
        _usage = new UsageReader(cfg, log);
        _server = new ScopeHttpServer(cfg, _registry, log) { UsageProvider = _usage.Read };
        _server.ControlHandler = HandleControlAsync;
    }

    public ScopeRegistry Registry => _registry;
    public string Token => _server.Token;

    public bool StartBackend()
    {
        if (!_server.Start()) return false;

        WriteRuntimeFile();

        if (_cfg.WatchTranscripts)
        {
            _watcher = new TranscriptWatcher(_cfg, _registry, _log);
            _watcher.Start();
        }
        else
        {
            _registry.RaiseIssue("transcript-off", "通道②（会话记录尾随）已在配置里关闭");
        }

        // 停滞看门狗
        _ticker = new System.Threading.Timer(_ =>
        {
            try { _registry.Tick(IsProcessAlive); }
            catch (Exception ex) { _log.Throttled("tick", "warn", $"看门狗出错：{ex.Message}"); }
        }, null, 1000, 1000);

        return true;
    }

    /// <summary>把横幅挂上来。必须在 UI 线程调用。</summary>
    public void ShowStrip()
    {
        // 先关旧的：它的 FormClosing 里会 ABM_REMOVE 把 AppBar 占位还回去。
        // 同进程串行执行，不会出现两个窗口叠着注册占位的情况。
        CloseStrip();
        _strip = new StripForm(_cfg, _registry, _usage, _log, this);
        _strip.FormClosed += (_, _) => _strip = null;
        _strip.Show();
    }

    /* -------------------------------------------------- 右键菜单要的动作 */

    public void RebuildStrip() => ShowStrip();

    public void OpenStrip() => ShowStrip();

    public bool IsStripOpen => _strip is not null;

    /// <summary>托盘必须跟横幅一起常驻：横幅可以被关掉，关掉后托盘是唯一能把它叫回来的入口。</summary>
    public void ShowTray()
    {
        _tray ??= new ScopeTray(_cfg, _registry, _usage, this, () => false);
    }

    public void QuitApp()
    {
        CloseStrip();
        Application.Exit();
    }

    public void OpenSettings()
    {
        if (_settings is null || _settings.IsDisposed)
            _settings = new SettingsForm(_cfg, _registry, _usage, this);
        _settings.Show();
        _settings.WindowState = FormWindowState.Normal;
        _settings.BringToFront();
        _settings.Activate();
    }

    public void SetDemo(string? state)
    {
        if (state is null) _registry.SetCommand("live");
        else _registry.SetCommand("demo", state);
    }

    void Ui.IStripActions.ResetWorkArea() => ResetWorkArea();

    public void CloseStrip()
    {
        if (_strip is null) return;
        var s = _strip;
        _strip = null;
        try { s.Close(); s.Dispose(); } catch { }
    }

    /* ---------------------------------------------------------- 控制台动作 */

    Task<object> HandleControlAsync(string action, System.Text.Json.JsonElement body)
    {
        object result = action switch
        {
            // 演示：把指令写进快照，横幅下次轮询（≤250ms）就会切过去。
            // 不需要知道横幅在不在——不在的话这条指令就只是躺在快照里没人执行。
            "demo" => Demo(body),
            "live" => Live(),
            "strip-open" => StripOpen(),
            "strip-close" => StripClose(),
            "reset-workarea" => ResetWorkArea(),
            "quit" => Quit(),
            _ => new { ok = false, message = $"未知动作：{action}" }
        };
        return Task.FromResult(result);
    }

    object Demo(System.Text.Json.JsonElement body)
    {
        var demo = body.TryGetProperty("demo", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String
            ? d.GetString() : "running";
        if (ScopeStateInfo.Parse(demo) is null)
            return new { ok = false, message = $"没有这种状态：{demo}" };
        _registry.SetCommand("demo", demo);
        return new { ok = true, message = $"横幅已切到演示：{demo}。回到实时点「退出演示」" };
    }

    object Live()
    {
        _registry.SetCommand("live");
        return new { ok = true, message = "横幅已回到实时状态" };
    }

    object StripOpen()
    {
        // 横幅在 UI 线程上，从 HTTP 线程过来要 marshal
        if (!HasUi) return new { ok = false, message = "当前是 --daemon 模式，没有界面" };
        RunOnUi(ShowStrip);
        return new { ok = true, message = "横幅已打开" };
    }

    /// --stop 走这条路而不是杀进程：杀进程会跳过 ABM_REMOVE，
    /// 屏幕边上会留下一条收不回来的空白。
    object Quit()
    {
        if (!HasUi)
        {
            // 只有 --daemon 模式才允许直接结束进程——那时候没有窗口，也就没有 AppBar 要收
            _ = Task.Run(async () => { await Task.Delay(200); Environment.Exit(0); });
            return new { ok = true, message = "守护进程正在退出" };
        }
        RunOnUi(QuitApp);
        return new { ok = true, message = "正在退出，AppBar 占位会归还" };
    }

    object StripClose()
    {
        if (!HasUi) return new { ok = false, message = "当前是 --daemon 模式，没有界面" };
        RunOnUi(CloseStrip);
        return new { ok = true, message = "横幅已关闭，AppBar 占位已收回" };
    }

    /// <summary>
    /// 救急：把每块屏的工作区恢复成整屏。
    /// 用于横幅进程被强杀、AppBar 占位没收回、屏幕边上留下一条空白的情况。
    /// 任务栏会自己重新占回它那份，不用担心把任务栏挤掉。
    /// </summary>
    static object ResetWorkArea()
    {
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
            if (Native.Win32.SystemParametersInfo(Native.Win32.SPI_SETWORKAREA, 0, ref r, Native.Win32.SPIF_SENDCHANGE))
                done++;
        }
        return new { ok = done > 0, message = $"已把 {done} 块屏的工作区恢复成整屏" };
    }

    /// <summary>
    /// 用一个隐藏控件做线程编组，而不是 SynchronizationContext.Current。
    ///
    /// 原因是个时序陷阱：WinForms 的同步上下文要等第一个窗口句柄创建时才装上，
    /// 在 Application.Run 之前抓 SynchronizationContext.Current 会拿到 null。
    /// 那样 --stop 就会走进"没有 UI 线程"的兜底分支直接 Environment.Exit，
    /// **跳过 AppBar 的 ABM_REMOVE**，屏幕边上留下一条收不回来的空白。
    /// 隐藏控件在这里强制建句柄，时序上没有含糊。
    /// </summary>
    Control? _marshal;

    public void InitUiThread()
    {
        _marshal = new Control();
        _ = _marshal.Handle;   // 强制建句柄，之后 BeginInvoke 才可用

        // 分辨率变化、插拔显示器、远程桌面接入都会触发这个。
        // 横幅的位置和 AppBar 占位都是按当时的屏幕算的，不重建会贴错地方。
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (!IsStripOpen) return;
        _log.Info("显示配置变了，重建横幅");
        // 稍等一下再重建：切换过程中 Screen.AllScreens 拿到的可能还是中间态
        RunOnUi(() =>
        {
            var t = new System.Windows.Forms.Timer { Interval = 1200 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); if (IsStripOpen) ShowStrip(); };
            t.Start();
        });
    }

    bool HasUi => _marshal is { IsDisposed: false, IsHandleCreated: true };

    void RunOnUi(Action action)
    {
        if (_marshal is { IsDisposed: false, IsHandleCreated: true } c) c.BeginInvoke(action);
        else action();
    }

    /* ---------------------------------------------------------------- 杂项 */

    void WriteRuntimeFile()
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                port = _cfg.Port,
                host = _cfg.Host,
                token = _server.Token,
                exe = ScopePaths.ExePath,
                startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, ScopeHttpServer.Json);
            File.WriteAllText(ScopePaths.RuntimeFile, payload);
        }
        catch (Exception ex)
        {
            _log.Warn($"写 runtime.json 失败：{ex.GetType().Name}");
        }
    }

    /// 惰性调用：只有会话越过停滞阈值时才会问一次，平时零开销。
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch { return true; }   // 拿不准就当活着，别误报"已终止"
    }

    public void Dispose()
    {
        _ticker?.Dispose();
        CloseStrip();
        _tray?.Dispose();
        _settings?.Dispose();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _marshal?.Dispose();
        _watcher?.Dispose();
        _server.Dispose();
        try { File.Delete(ScopePaths.RuntimeFile); } catch { }
    }
}
