using System.Reflection;
using ClaudeScope.Core;
using ClaudeScope.Native;

namespace ClaudeScope.Ui;

/// <summary>
/// 横幅窗口。无边框、置顶、不进任务栏、不抢焦点。
///
/// 为什么不用浏览器画：Edge/Chrome 的 --app 窗口会在客户区里自己画一条带
/// 最小化/最大化/关闭的标题栏，约 30px。52px 的横幅被它一吃只剩十几像素，
/// 而且那条是 Chromium 自绘的，扒 WS_CAPTION 之类的窗口样式没用（实测过）。
/// </summary>
public sealed class StripForm : Form
{
    readonly ScopeConfig _cfg;
    readonly ScopeRegistry _registry;
    readonly UsageReader _usage;
    readonly ScopeLogger _log;

    readonly StripRenderer _renderer;
    readonly WaveGenerator _wave;
    readonly ToneGenerator _tones = new();
    readonly AppBarSlot _appBar = new();

    readonly System.Windows.Forms.Timer _render = new();
    readonly System.Windows.Forms.Timer _poll = new();
    readonly System.Windows.Forms.Timer _guard = new();

    readonly StripFrame _frame = new();
    readonly bool _bottom;
    readonly int _height;
    readonly Screen _screen;

    ScopeState _prevState = ScopeState.Disconnected;
    DateTime _lastTick = DateTime.UtcNow;
    long _lastCommandTs;
    bool _commandBaselineSet;
    ScopeState? _demoState;
    readonly IStripActions _actions;
    ContextMenuStrip? _menu;

    public StripForm(ScopeConfig cfg, ScopeRegistry registry, UsageReader usage, ScopeLogger log, IStripActions actions)
    {
        _actions = actions;
        _cfg = cfg;
        _registry = registry;
        _usage = usage;
        _log = log;

        _bottom = cfg.Strip.Edge == "bottom";
        _height = cfg.Strip.Height;
        _screen = ResolveScreen(cfg.Strip.Monitor);

        _renderer = new StripRenderer(_height, _bottom);
        _wave = new WaveGenerator(_screen.Bounds.Width);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "claude-scope-strip";
        BackColor = System.Drawing.Color.FromArgb(6, 8, 11);

        // WinForms 的 DoubleBuffered 是 protected，反射打开，否则 33fps 重绘会闪
        typeof(Control)
            .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(this, true);

        var want = WantRect();
        Location = new System.Drawing.Point(want.Left, want.Top);
        ClientSize = new System.Drawing.Size(want.Width, _height);

        _render.Interval = 30;   // 33fps：再高 GDI+ 的开销就不划算了
        _render.Tick += OnRenderTick;
        _poll.Interval = 250;    // 同进程读状态机，250ms 绰绰有余
        _poll.Tick += (_, _) => PollState();
        _guard.Interval = 3000;
        _guard.Tick += (_, _) => ReassertPosition();
    }

    /// <summary>横幅要贴哪块屏。-1 或找不到就自动选第一块非主显示器。</summary>
    static Screen ResolveScreen(int index)
    {
        var all = Screen.AllScreens;
        if (index >= 0 && index < all.Length) return all[index];
        var nonPrimary = all.Where(s => !s.Primary).OrderBy(s => s.Bounds.X).FirstOrDefault();
        return nonPrimary ?? Screen.PrimaryScreen ?? all[0];
    }

    RECT WantRect()
    {
        var b = _screen.Bounds;
        return _bottom
            ? new RECT { Left = b.Left, Right = b.Right, Top = b.Bottom - _height, Bottom = b.Bottom }
            : new RECT { Left = b.Left, Right = b.Right, Top = b.Top, Bottom = b.Top + _height };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // 不进任务栏、不抢焦点
        var ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);

        if (_cfg.Strip.Reserve)
        {
            _appBar.Register(Handle, _bottom);
            _log.Info("已注册 AppBar 占位，最大化窗口会自动避开这条横幅");
        }
        ReassertPosition();

        _menu = StripMenu.Build(_cfg, _actions, () => _demoState is not null);
        MouseUp += OnMouseUp;

        PollState();
        _render.Start();
        _poll.Start();
        _guard.Start();
    }

    /// 分辨率变化、任务栏移动、别的程序抢置顶，都靠这个纠回来
    void ReassertPosition()
    {
        var r = _cfg.Strip.Reserve
            ? _appBar.Place(WantRect(), _bottom, _height)
            : WantRect();

        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST,
            r.Left, r.Top, r.Width, r.Height, Win32.SWP_NOACTIVATE);
    }

    /* ---------------------------------------------------------------- 取状态 */

    void PollState()
    {
        var snap = _registry.Snapshot();
        var usage = _usage.Read();

        // 控制台发来的演示指令。按 ts 去重——快照会被反复读到，
        // 不去重的话一条指令会被执行无数遍。
        //
        // 基线必须在第一次轮询就记下，**哪怕这时候还没有任何指令**。
        // 写成「只在 Command 非空时记基线」会吞掉第一条真指令：
        // 首次轮询 Command 为 null 不进分支，等真指令来了反而被当成基线。
        if (!_commandBaselineSet)
        {
            _commandBaselineSet = true;
            _lastCommandTs = snap.Command?.Ts ?? 0;
        }
        else if (snap.Command is { } cmd && cmd.Ts > _lastCommandTs)
        {
            _lastCommandTs = cmd.Ts;
            _demoState = cmd.Type == "demo" ? ScopeStateInfo.Parse(cmd.Demo) : null;
        }

        if (_demoState is { } demo)
        {
            _frame.State = demo;
            _frame.Label = ScopePalette.LabelOf(demo);
            _frame.Detail = ScopePalette.DemoTextOf(demo);
            _frame.Project = "demo";
            _frame.Source = "演示";
            _frame.StateSince = DateTime.UtcNow.AddSeconds(-34);
            _frame.LastEventAt = DateTime.UtcNow.AddSeconds(-3);
            _frame.Model = null;
            _frame.ContextTokens = null;
        }
        else if (snap.Primary is { } p)
        {
            _frame.State = ScopeStateInfo.Parse(p.State) ?? ScopeState.Idle;
            _frame.Label = p.Label;
            _frame.Detail = p.Detail;
            _frame.Project = p.Project;
            _frame.Source = p.Source;
            _frame.StateSince = DateTimeOffset.FromUnixTimeMilliseconds(p.Since).UtcDateTime;
            _frame.LastEventAt = DateTimeOffset.FromUnixTimeMilliseconds(p.LastEventAt).UtcDateTime;
            _frame.Model = p.Model;
            _frame.ContextTokens = p.ContextTokens;
        }
        else
        {
            _frame.State = ScopeState.Idle;
            _frame.Label = "空闲";
            _frame.Detail = "当前没有活跃会话";
            _frame.Project = "";
            _frame.Source = "";
            _frame.LastEventAt = DateTime.UtcNow;
        }

        _frame.UsageFiveHour = usage?.Problem is null ? usage?.FiveHourPercent : null;
        _frame.UsageWeek = usage?.Problem is null ? usage?.WeekPercent : null;
        _frame.UsageStale = usage?.Stale ?? false;
    }

    void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || _menu is null) return;
        StripMenu.ShowAt(_menu, Handle, PointToScreen(e.Location));
    }

    /* ---------------------------------------------------------------- 渲染 */

    void OnRenderTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min(0.2, (now - _lastTick).TotalSeconds);
        _lastTick = now;

        if (_frame.State != _prevState)
        {
            _prevState = _frame.State;
            _frame.EnteredAt = now;
            _renderer.ResetMarquee();

            // 只有「等你确认」和「出错」响，而且只在刚切进那个状态时响
            var minGap = TimeSpan.FromSeconds(_cfg.Sound.MinIntervalSeconds);
            if (_frame.State == ScopeState.Waiting) _tones.Play("waiting", _cfg.Sound.Waiting, minGap);
            else if (_frame.State == ScopeState.Error) _tones.Play("error", _cfg.Sound.Error, minGap);
        }

        _wave.Advance(dt, ScopePalette.WaveOf(_frame.State));
        _renderer.AdvanceMarquee(dt);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _renderer.Paint(e.Graphics, ClientSize.Width, _frame, _wave);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 全靠 Paint 里的 Clear，这里什么都不做，省一次全屏填充
    }

    /* ---------------------------------------------------------------- 收尾 */

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // AppBar 占位一定要还回去，否则屏幕边上会留下一条收不回来的空白
        _appBar.Dispose();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _render.Dispose();
            _poll.Dispose();
            _guard.Dispose();
            _renderer.Dispose();
            _tones.Dispose();
            _appBar.Dispose();
            _menu?.Dispose();
        }
        base.Dispose(disposing);
    }
}
