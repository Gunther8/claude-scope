using System.Diagnostics;
using ClaudeScope.Core;

namespace ClaudeScope.Ui;

/// <summary>
/// 原生设置/详情窗。三个页签：状态、设置、诊断。
/// 用原生而不是网页，是为了不给一个单 exe 工具再拖一个浏览器进来。
/// </summary>
public sealed class SettingsForm : Form
{
    readonly ScopeConfig _cfg;
    readonly ScopeRegistry _registry;
    readonly UsageReader _usage;
    readonly IStripActions _actions;

    readonly ListView _sessions = new();
    readonly Label _usageLabel = new();
    readonly Label _hookLabel = new();
    readonly TextBox _logBox = new();
    readonly System.Windows.Forms.Timer _refresh = new();

    public SettingsForm(ScopeConfig cfg, ScopeRegistry registry, UsageReader usage, IStripActions actions)
    {
        _cfg = cfg;
        _registry = registry;
        _usage = usage;
        _actions = actions;

        Text = "claude-scope";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 560);
        MinimumSize = new Size(560, 420);
        Font = new Font("Microsoft YaHei UI", 9f);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildStatusTab());
        tabs.TabPages.Add(BuildSettingsTab());
        tabs.TabPages.Add(BuildDiagnosticsTab());
        Controls.Add(tabs);

        _refresh.Interval = 1000;
        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Start();
        RefreshStatus();
    }

    /* ---------------------------------------------------------------- 状态 */

    TabPage BuildStatusTab()
    {
        var page = new TabPage("状态") { Padding = new Padding(10) };

        _sessions.View = View.Details;
        _sessions.FullRowSelect = true;
        _sessions.GridLines = false;
        _sessions.Dock = DockStyle.Fill;
        _sessions.Columns.Add("状态", 90);
        _sessions.Columns.Add("项目", 170);
        _sessions.Columns.Add("来源", 70);
        _sessions.Columns.Add("模型", 90);
        _sessions.Columns.Add("上下文", 75);
        _sessions.Columns.Add("最后事件", 90);

        _usageLabel.Dock = DockStyle.Bottom;
        _usageLabel.Height = 96;
        _usageLabel.Padding = new Padding(4, 8, 4, 4);
        _usageLabel.ForeColor = Color.FromArgb(70, 70, 70);

        page.Controls.Add(_sessions);
        page.Controls.Add(_usageLabel);
        return page;
    }

    void RefreshStatus()
    {
        if (!Visible) return;

        var snap = _registry.Snapshot();
        var all = new List<SessionView>();
        if (snap.Primary is not null) all.Add(snap.Primary);
        all.AddRange(snap.Others);

        _sessions.BeginUpdate();
        _sessions.Items.Clear();
        foreach (var s in all)
        {
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.LastEventAt;
            var item = new ListViewItem(new[]
            {
                s.Label,
                s.Project,
                s.Source,
                StatFormat.ShortModel(s.Model),
                StatFormat.Context(s.ContextTokens),
                FormatAge(age)
            });
            _sessions.Items.Add(item);
        }
        if (all.Count == 0)
            _sessions.Items.Add(new ListViewItem(new[] { "—", "没有活跃会话", "", "", "", "" }));
        _sessions.EndUpdate();

        _usageLabel.Text = BuildUsageText();
    }

    string BuildUsageText()
    {
        var u = _usage.Read();
        if (u is null) return "额度显示已在配置里关闭（showUsage = false）";
        if (u.Problem is not null) return $"额度：{u.Problem}";

        var at = DateTimeOffset.FromUnixTimeMilliseconds(u.SampledAt).ToLocalTime();
        var lines = new List<string>
        {
            $"5 小时窗口 {u.FiveHourPercent}%     7 天窗口 {u.WeekPercent}%",
            $"采样时间 {at:HH:mm:ss}（{u.AgeSeconds / 60:N1} 分钟前）{(u.Stale ? "  ← 已过期，别当实时值" : "")}",
            "",
            // 这段话必须留着：这两个数的含义是从行为推断的，不是官方文档写的
            "数据来自桌面版写的 plan-usage-history.json，格式未文档化，且只有桌面版在跑时才更新。",
            "第一次用请在 Claude Code 里跑 /usage 对一次数，确认对得上再信它。"
        };
        return string.Join(Environment.NewLine, lines);
    }

    static string FormatAge(long ms)
    {
        var s = (int)(ms / 1000);
        if (s < 60) return $"{s} 秒前";
        var m = s / 60;
        return m < 60 ? $"{m} 分前" : $"{m / 60} 小时前";
    }

    /* ---------------------------------------------------------------- 设置 */

    TabPage BuildSettingsTab()
    {
        var page = new TabPage("设置") { Padding = new Padding(12), AutoScroll = true };
        var y = 10;

        Label Head(string text)
        {
            var l = new Label
            {
                Text = text,
                Location = new Point(0, y),
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold)
            };
            y += 26;
            page.Controls.Add(l);
            return l;
        }

        NumericUpDown Num(string label, double value, double min, double max, Action<double> set)
        {
            page.Controls.Add(new Label { Text = label, Location = new Point(8, y + 4), AutoSize = true });
            var n = new NumericUpDown
            {
                Location = new Point(230, y),
                Width = 90,
                Minimum = (decimal)min,
                Maximum = (decimal)max,
                Value = (decimal)Math.Clamp(value, min, max)
            };
            n.ValueChanged += (_, _) => { set((double)n.Value); _cfg.Save(); };
            page.Controls.Add(n);
            y += 30;
            return n;
        }

        CheckBox Check(string label, bool value, Action<bool> set)
        {
            var c = new CheckBox { Text = label, Location = new Point(8, y), AutoSize = true, Checked = value };
            c.CheckedChanged += (_, _) => { set(c.Checked); _cfg.Save(); };
            page.Controls.Add(c);
            y += 26;
            return c;
        }

        Head("阈值");
        Num("长任务标记（秒）", _cfg.Thresholds.LongRunSeconds, 1, 3600, v => _cfg.Thresholds.LongRunSeconds = v);
        Num("判定卡住 / 终止（秒）", _cfg.Thresholds.StallSeconds, 5, 7200, v => _cfg.Thresholds.StallSeconds = v);
        Num("「完成」多久落回空闲（秒）", _cfg.Thresholds.DoneDecaySeconds, 1, 600, v => _cfg.Thresholds.DoneDecaySeconds = v);
        Num("红屏最短停留（秒）", _cfg.Thresholds.ErrorMinDisplaySeconds, 1, 60, v => _cfg.Thresholds.ErrorMinDisplaySeconds = v);
        Num("会话保留时长（分钟）", _cfg.Thresholds.SessionTtlMinutes, 1, 1440, v => _cfg.Thresholds.SessionTtlMinutes = v);
        Num("额度多久算过期（分钟）", _cfg.Thresholds.UsageStaleMinutes, 1, 1440, v => _cfg.Thresholds.UsageStaleMinutes = v);

        y += 10;
        Head("提示音");
        Check("「等你确认」响", _cfg.Sound.Waiting, v => _cfg.Sound.Waiting = v);
        Check("「出错」响", _cfg.Sound.Error, v => _cfg.Sound.Error = v);
        Num("同类最小间隔（秒）", _cfg.Sound.MinIntervalSeconds, 0, 600, v => _cfg.Sound.MinIntervalSeconds = v);

        y += 10;
        Head("通道");
        Check("尾随会话记录（通道②）", _cfg.WatchTranscripts, v => _cfg.WatchTranscripts = v);
        Check("显示额度", _cfg.ShowUsage, v => _cfg.ShowUsage = v);

        y += 6;
        var note = new Label
        {
            Text = "阈值和提示音改完立即生效；通道开关要重启程序。横幅的贴边/显示器/高度在右键菜单里改。",
            Location = new Point(8, y),
            Size = new Size(600, 40),
            ForeColor = Color.FromArgb(110, 110, 110)
        };
        page.Controls.Add(note);
        return page;
    }

    /* ---------------------------------------------------------------- 诊断 */

    TabPage BuildDiagnosticsTab()
    {
        var page = new TabPage("诊断") { Padding = new Padding(12) };

        _hookLabel.Location = new Point(8, 10);
        _hookLabel.Size = new Size(640, 60);
        page.Controls.Add(_hookLabel);

        var install = new Button { Text = "安装 hook", Location = new Point(8, 76), Width = 110 };
        install.Click += (_, _) => DoHook(true);
        page.Controls.Add(install);

        var uninstall = new Button { Text = "移除 hook", Location = new Point(126, 76), Width = 110 };
        uninstall.Click += (_, _) => DoHook(false);
        page.Controls.Add(uninstall);

        var openCfg = new Button { Text = "打开配置目录", Location = new Point(244, 76), Width = 120 };
        openCfg.Click += (_, _) => OpenFolder(ScopePaths.RuntimeDir);
        page.Controls.Add(openCfg);

        var openLog = new Button { Text = "打开日志目录", Location = new Point(372, 76), Width = 120 };
        openLog.Click += (_, _) => OpenFolder(ScopePaths.LogDir);
        page.Controls.Add(openLog);

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Font = new Font("Consolas", 8.5f);
        _logBox.Location = new Point(8, 114);
        _logBox.Size = new Size(640, 380);
        _logBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        page.Controls.Add(_logBox);

        page.Enter += (_, _) => { RefreshHookStatus(); LoadLogTail(); };
        return page;
    }

    void RefreshHookStatus()
    {
        var st = HookSetup.Status(_cfg.Port);
        _hookLabel.Text = st.Summary;
    }

    void DoHook(bool install)
    {
        var r = install ? HookSetup.Install(_cfg.Port) : HookSetup.Uninstall();
        MessageBox.Show(r.Message, "claude-scope", MessageBoxButtons.OK,
            r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        RefreshHookStatus();
    }

    void LoadLogTail()
    {
        try
        {
            using var fs = new FileStream(ScopePaths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            var lines = r.ReadToEnd().Split('\n');
            _logBox.Lines = lines.TakeLast(200).Select(l => l.TrimEnd('\r')).ToArray();
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
        catch (FileNotFoundException) { _logBox.Text = "（日志还没生成）"; }
        catch (Exception ex) { _logBox.Text = $"读日志失败：{ex.Message}"; }
    }

    static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 关掉只是藏起来：设置窗反复开销毁没必要，而且能保住滚动位置
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refresh.Dispose();
        base.Dispose(disposing);
    }
}
