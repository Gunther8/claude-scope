using System.Runtime.InteropServices;
using ClaudeScope.Core;

namespace ClaudeScope.Ui;

/// <summary>横幅右键菜单要用到的动作，由宿主实现。</summary>
public interface IStripActions
{
    void RebuildStrip();                    // 参数变了，重建窗口
    void OpenStrip();
    void CloseStrip();
    bool IsStripOpen { get; }
    void QuitApp();
    void OpenSettings();
    void SetDemo(string? state);            // null = 退出演示
    void ResetWorkArea();
}

public static class StripMenu
{
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    static readonly (string Wire, string Label)[] DemoStates =
    {
        ("idle", "空闲"), ("thinking", "思考中"), ("writing", "写代码"),
        ("running", "执行命令"), ("done", "完成"), ("error", "出错"),
        ("waiting", "等你确认"), ("stalled", "疑似卡住"),
        ("dead", "会话已终止"), ("disconnected", "状态源断开")
    };

    static readonly int[] Heights = { 40, 52, 64, 80, 100 };

    public static ContextMenuStrip Build(ScopeConfig cfg, IStripActions actions, Func<bool> inDemo)
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        // ---- 演示 ----
        var demo = new ToolStripMenuItem("演示");
        foreach (var (wire, label) in DemoStates)
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) => actions.SetDemo(wire);
            demo.DropDownItems.Add(item);
        }
        demo.DropDownItems.Add(new ToolStripSeparator());
        var live = new ToolStripMenuItem("退出演示，回到实时");
        live.Click += (_, _) => actions.SetDemo(null);
        demo.DropDownItems.Add(live);
        demo.DropDownOpening += (_, _) => live.Enabled = inDemo();
        menu.Items.Add(demo);

        // ---- 显示 ----
        var display = new ToolStripMenuItem("显示");

        var edge = new ToolStripMenuItem("贴边");
        foreach (var (val, label) in new[] { ("top", "顶部"), ("bottom", "底部") })
        {
            var item = new ToolStripMenuItem(label) { Checked = cfg.Strip.Edge == val };
            item.Click += (_, _) =>
            {
                cfg.Strip.Edge = val;
                cfg.Save();
                actions.RebuildStrip();
            };
            edge.DropDownItems.Add(item);
        }
        display.DropDownItems.Add(edge);

        var monitors = new ToolStripMenuItem("显示器");
        monitors.DropDownOpening += (_, _) =>
        {
            // 显示器可能热插拔，每次展开都重新枚举
            monitors.DropDownItems.Clear();
            var auto = new ToolStripMenuItem("自动（第一块非主屏）") { Checked = cfg.Strip.Monitor < 0 };
            auto.Click += (_, _) => { cfg.Strip.Monitor = -1; cfg.Save(); actions.RebuildStrip(); };
            monitors.DropDownItems.Add(auto);
            monitors.DropDownItems.Add(new ToolStripSeparator());

            var all = Screen.AllScreens;
            for (var i = 0; i < all.Length; i++)
            {
                var idx = i;
                var s = all[i];
                var text = $"{i}   {s.Bounds.Width}×{s.Bounds.Height} @ ({s.Bounds.X},{s.Bounds.Y}){(s.Primary ? "   主屏" : "")}";
                var item = new ToolStripMenuItem(text) { Checked = cfg.Strip.Monitor == idx };
                item.Click += (_, _) => { cfg.Strip.Monitor = idx; cfg.Save(); actions.RebuildStrip(); };
                monitors.DropDownItems.Add(item);
            }
        };
        display.DropDownItems.Add(monitors);

        var height = new ToolStripMenuItem("高度");
        foreach (var h in Heights)
        {
            var item = new ToolStripMenuItem($"{h} px") { Checked = cfg.Strip.Height == h };
            item.Click += (_, _) => { cfg.Strip.Height = h; cfg.Save(); actions.RebuildStrip(); };
            height.DropDownItems.Add(item);
        }
        display.DropDownItems.Add(height);

        display.DropDownItems.Add(new ToolStripSeparator());

        var reserve = new ToolStripMenuItem("占位（最大化窗口自动避开）")
        { Checked = cfg.Strip.Reserve, CheckOnClick = true };
        reserve.Click += (_, _) =>
        {
            cfg.Strip.Reserve = reserve.Checked;
            cfg.Save();
            actions.RebuildStrip();
        };
        display.DropDownItems.Add(reserve);

        var reset = new ToolStripMenuItem("恢复工作区（救急）");
        reset.ToolTipText = "横幅进程被强杀导致屏幕边上空出一条却什么都没有时用";
        reset.Click += (_, _) => actions.ResetWorkArea();
        display.DropDownItems.Add(reset);

        // 每次弹出都按当前配置刷勾，否则改完再打开会显示旧值
        display.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripItem it in edge.DropDownItems)
                if (it is ToolStripMenuItem m)
                    m.Checked = (m.Text == "顶部") == (cfg.Strip.Edge == "top");
            foreach (ToolStripItem it in height.DropDownItems)
                if (it is ToolStripMenuItem m)
                    m.Checked = m.Text == $"{cfg.Strip.Height} px";
            reserve.Checked = cfg.Strip.Reserve;
        };
        menu.Items.Add(display);

        // ---- 设置 ----
        var settings = new ToolStripMenuItem("设置…");
        settings.Click += (_, _) => actions.OpenSettings();
        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());

        // 「关闭横幅」和「退出」语义完全不同，必须分开：
        // 前者只关显示，后台继续收数据；后者整个程序退出，hook 就没人接了。
        // 同一个条目在托盘上要能变成「打开横幅」，否则关掉横幅就没有回头路了。
        var toggleStrip = new ToolStripMenuItem("关闭横幅（后台继续收数据）");
        toggleStrip.Click += (_, _) =>
        {
            if (actions.IsStripOpen) actions.CloseStrip();
            else actions.OpenStrip();
        };
        menu.Items.Add(toggleStrip);

        menu.Opening += (_, _) =>
            toggleStrip.Text = actions.IsStripOpen ? "关闭横幅（后台继续收数据）" : "打开横幅";

        var quit = new ToolStripMenuItem("退出 claude-scope");
        quit.Click += (_, _) => actions.QuitApp();
        menu.Items.Add(quit);

        return menu;
    }

    /// <summary>
    /// 横幅带 WS_EX_NOACTIVATE，不会自动成为前台窗口。
    /// 不先 SetForegroundWindow 的话，弹出的菜单点别处不消失——
    /// 因为菜单要靠窗口在前台才能收到"点到外面了"的通知。
    /// 所有托盘程序都在用这个 workaround。
    /// </summary>
    public static void ShowAt(ContextMenuStrip menu, IntPtr owner, Point screenPos)
    {
        SetForegroundWindow(owner);
        menu.Show(screenPos);
    }
}
