using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClaudeScope.Core;

namespace ClaudeScope.Ui;

/// <summary>
/// 托盘图标：右下角一个小圆点，颜色就是当前状态。
/// 它的存在不只是"顺便"——横幅可以被关掉，关掉之后托盘是唯一能把它叫回来的入口。
/// </summary>
public sealed class ScopeTray : IDisposable
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

    readonly NotifyIcon _icon = new();
    readonly Dictionary<ScopeState, Icon> _icons = new();
    readonly List<IntPtr> _handles = new();
    readonly ScopeRegistry _registry;
    readonly UsageReader _usage;
    readonly System.Windows.Forms.Timer _timer = new();

    ScopeState _shown = ScopeState.Disconnected;

    public ScopeTray(ScopeConfig cfg, ScopeRegistry registry, UsageReader usage, IStripActions actions, Func<bool> inDemo)
    {
        _registry = registry;
        _usage = usage;

        foreach (ScopeState st in Enum.GetValues<ScopeState>())
            _icons[st] = MakeIcon(ScopePalette.ColorOf(st), st is ScopeState.Waiting or ScopeState.Error);

        _icon.Icon = _icons[ScopeState.Idle];
        _icon.Text = "claude-scope";
        _icon.Visible = true;
        _icon.ContextMenuStrip = StripMenu.Build(cfg, actions, inDemo);
        // 双击直接开设置，比在菜单里找一次快
        _icon.DoubleClick += (_, _) => actions.OpenSettings();

        _timer.Interval = 1500;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    /// 「等你确认」和「出错」中间多一个白心，托盘那么小也能一眼分出来
    Icon MakeIcon(Color c, bool hollow)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(c);
            g.FillEllipse(brush, 3, 3, 26, 26);
            if (hollow)
            {
                using var inner = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
                g.FillEllipse(inner, 12, 12, 8, 8);
            }
        }
        // GetHicon 出来的句柄要自己销毁，否则每次刷新都漏一个 GDI 对象。
        // 这里 10 个图标一次性建好，进程退出时统一销毁。
        var h = bmp.GetHicon();
        _handles.Add(h);
        return Icon.FromHandle(h);
    }

    void Refresh()
    {
        var snap = _registry.Snapshot();
        var state = ScopeState.Idle;
        string tip;

        if (snap.Primary is { } p)
        {
            state = ScopeStateInfo.Parse(p.State) ?? ScopeState.Idle;
            tip = $"{p.Label} · {p.Project}";
            if (p.Detail.Length > 0) tip += Environment.NewLine + p.Detail;
        }
        else
        {
            tip = "空闲 · 没有活跃会话";
        }

        var u = _usage.Read();
        if (u is { Problem: null, FiveHourPercent: { } fh })
            tip += Environment.NewLine + (u.Stale ? "额度数据已过期" : $"5h {fh}%  周 {u.WeekPercent}%");

        if (state != _shown)
        {
            _shown = state;
            _icon.Icon = _icons[state];
        }

        // NotifyIcon.Text 上限 63 个字符，超了会抛异常
        _icon.Text = tip.Length > 60 ? tip[..60] + "…" : tip;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
        foreach (var h in _handles) DestroyIcon(h);
    }
}
