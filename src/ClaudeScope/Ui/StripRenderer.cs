using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ClaudeScope.Core;

namespace ClaudeScope.Ui;

/// <summary>横幅要画的一帧内容。渲染器不碰状态机，只认这个结构。</summary>
public sealed class StripFrame
{
    public ScopeState State = ScopeState.Disconnected;
    public string Label = "连接中";
    public string Detail = "正在连上守护进程…";
    public string Project = "";
    public string Source = "";
    public DateTime StateSince = DateTime.UtcNow;
    public DateTime LastEventAt = DateTime.UtcNow;
    public DateTime EnteredAt = DateTime.UtcNow;
    public int? UsageFiveHour;
    public int? UsageWeek;
    public bool UsageStale;
    public string? Model;
    public long? ContextTokens;
}

/// <summary>
/// 右侧那一串附加信息。横幅只有一行，窄屏放不下时要按优先级依次丢，
/// 而不是挤成一团——挤在一起等于全都看不清。
/// </summary>
static class StatFormat
{
    /// claude-opus-5 -> opus-5；claude-haiku-4-5-20251001 -> haiku-4-5
    /// 只做"去前缀"和"去日期后缀"两件事，不猜别的
    public static string ShortModel(string? model)
    {
        if (string.IsNullOrEmpty(model)) return "";
        var m = model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? model[7..] : model;
        var dash = m.LastIndexOf('-');
        if (dash > 0 && m.Length - dash - 1 == 8 && m[(dash + 1)..].All(char.IsDigit))
            m = m[..dash];
        return m;
    }

    public static string Context(long? tokens) => tokens switch
    {
        null or <= 0 => "",
        < 1000 => $"{tokens}",
        < 1_000_000 => $"{tokens / 1000.0:0.#}k",
        _ => $"{tokens / 1_000_000.0:0.##}M"
    };
}

/// <summary>
/// 横幅的 GDI+ 绘制。所有跟状态色相关的画刷画笔都缓存着——
/// 状态只在切换时变，每帧新建再 Dispose 就是每秒上百次 GDI 句柄申请/释放。
/// </summary>
public sealed class StripRenderer : IDisposable
{
    readonly int _h;
    readonly int _pad;
    readonly int _gap;
    readonly int _scopeH;
    readonly int _accentH;
    readonly bool _bottomEdge;

    readonly Font _fWord, _fTimer, _fMain, _fMeta, _fChip;
    readonly SolidBrush _brDim, _brText, _brChipFg, _brScopeBg;
    readonly Pen _penGrid, _penDiv;

    // 状态色相关的对象，只在颜色变了才重建
    int _accentArgb = -1;
    SolidBrush? _brAccent;
    Pen? _penGlow, _penTrace, _penHaz;

    float _marqueeX;
    float[] _wave = Array.Empty<float>();

    public StripRenderer(int height, bool bottomEdge)
    {
        _h = height;
        _bottomEdge = bottomEdge;
        _pad = Math.Max(10, (int)(height * 0.22));
        _gap = Math.Max(8, (int)(height * 0.20));
        _scopeH = (int)(height * 0.62);
        _accentH = Math.Max(2, (int)(height * 0.05));

        _fWord = new Font("Microsoft YaHei UI", height * 0.30f, FontStyle.Bold, GraphicsUnit.Pixel);
        _fTimer = new Font("Consolas", height * 0.26f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fMain = new Font("Consolas", height * 0.27f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fMeta = new Font("Microsoft YaHei UI", height * 0.22f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fChip = new Font("Microsoft YaHei UI", height * 0.21f, FontStyle.Bold, GraphicsUnit.Pixel);

        _brDim = new SolidBrush(Color.FromArgb(255, 125, 138, 152));
        _brText = new SolidBrush(Color.FromArgb(255, 194, 206, 219));
        _brChipFg = new SolidBrush(Color.FromArgb(255, 11, 14, 19));
        _brScopeBg = new SolidBrush(Color.FromArgb(255, 8, 12, 17));
        _penGrid = new Pen(Color.FromArgb(255, 20, 28, 36), 1);
        _penDiv = new Pen(Color.FromArgb(255, 34, 43, 54), 1);
    }

    void EnsureAccent(Color c)
    {
        if (_accentArgb == c.ToArgb()) return;
        _accentArgb = c.ToArgb();

        _brAccent?.Dispose(); _penGlow?.Dispose(); _penTrace?.Dispose(); _penHaz?.Dispose();

        _brAccent = new SolidBrush(c);

        // 余晖薄一点，否则厚笔糊在一起会变成一条模糊的带子
        _penGlow = new Pen(Color.FromArgb(34, c.R, c.G, c.B), Math.Max(4f, _h * 0.11f))
        { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

        // 主迹线细一档，细线 + 抗锯齿才有"精细"的观感
        _penTrace = new Pen(c, Math.Max(1.1f, _h * 0.028f))
        { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

        _penHaz = new Pen(c, 2);
    }

    /// 底色：等你确认呼吸、出错频闪，这是余光最先察觉的那层
    Color BackgroundFor(StripFrame f)
    {
        var inState = (DateTime.UtcNow - f.EnteredAt).TotalSeconds;
        switch (f.State)
        {
            case ScopeState.Waiting:
            {
                var k = (1 + Math.Sin(inState * 2.6)) / 2;
                return Color.FromArgb(255, (int)(24 + 50 * k), (int)(13 + 31 * k), (int)(2 + 4 * k));
            }
            case ScopeState.Error:
                if (inState < 3)
                    return (int)Math.Floor(inState / 0.17) % 2 == 0
                        ? Color.FromArgb(255, 74, 6, 6)
                        : Color.FromArgb(255, 18, 2, 2);
                return Color.FromArgb(255, 38, 7, 7);
            default:
                return Color.FromArgb(255, 6, 8, 11);
        }
    }

    public void AdvanceMarquee(double dt) => _marqueeX -= (float)(dt * 95);
    public void ResetMarquee() => _marqueeX = 0;

    public void Paint(Graphics g, int width, StripFrame f, WaveGenerator wave)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 没有这句，线条端点会被吸附到整像素，细线看着还是有台阶
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var accent = ScopePalette.ColorOf(f.State);
        EnsureAccent(accent);

        g.Clear(BackgroundFor(f));   // 比自建画刷 FillRectangle 少一次句柄申请

        // 状态色描边
        if (_bottomEdge) g.FillRectangle(_brAccent!, 0, 0, width, _accentH);
        else g.FillRectangle(_brAccent!, 0, _h - _accentH, width, _accentH);

        var x = (float)_pad;

        // ---- 状态词 ----
        var word = f.Label.Length > 0 ? f.Label : ScopePalette.LabelOf(f.State);
        var wordSize = g.MeasureString(word, _fWord);
        g.DrawString(word, _fWord, _brAccent!, x, (_h - wordSize.Height) / 2);
        x += wordSize.Width + 8;

        // ---- 计时器：区分「在慢慢干活」和「已经死了」的关键 ----
        if (ScopePalette.ShowsTimer(f.State))
        {
            var clock = FormatClock(DateTime.UtcNow - f.StateSince);
            var ts = g.MeasureString(clock, _fTimer);
            g.DrawString(clock, _fTimer, _brText, x, (_h - ts.Height) / 2);
            x += ts.Width;
        }
        x += _gap;

        g.DrawLine(_penDiv, x, _h * 0.22f, x, _h * 0.78f);
        x += _gap;

        // ---- 右侧先量好，中间的自由空间才好按 2:1 分 ----
        var metaText = BuildMeta(f);
        var metaSize = g.MeasureString(metaText, _fMeta);

        var chipText = f.Source;
        var chipSize = chipText.Length > 0 ? g.MeasureString(chipText, _fChip) : SizeF.Empty;
        var chipW = chipText.Length > 0 ? chipSize.Width + _h * 0.34f : 0;

        // 按优先级排：空间不够时从后往前丢。5h 额度最要紧（它会突然卡住你），
        // 周额度变化最慢、信息量最低，第一个被丢。
        var stats = new List<string>(3);
        if (BuildFiveHour(f) is { Length: > 0 } fh) stats.Add(fh);
        if (StatFormat.Context(f.ContextTokens) is { Length: > 0 } ctx) stats.Add(ctx);
        if (StatFormat.ShortModel(f.Model) is { Length: > 0 } mdl) stats.Add(mdl);
        if (BuildWeek(f) is { Length: > 0 } wk) stats.Add(wk);

        var baseFixed = metaSize.Width + chipW + _gap * 4 + _pad;
        string statText;
        while (true)
        {
            statText = string.Join("  ·  ", stats);
            var w = statText.Length > 0 ? g.MeasureString(statText, _fMeta).Width + _gap : 0;
            // 中间至少要留 210px，否则跑马灯和波形都没法看
            if (width - x - baseFixed - w >= 210 || stats.Count == 0) break;
            stats.RemoveAt(stats.Count - 1);
        }
        var statSize = statText.Length > 0 ? g.MeasureString(statText, _fMeta) : SizeF.Empty;

        var fixedRight = baseFixed + (statText.Length > 0 ? statSize.Width + _gap : 0);
        var free = width - x - fixedRight;
        if (free < 210) free = 210;

        // 中间的自由空间按 示波器 : 文字 = 2 : 1 切分——
        // 波形是余光真正能抓到的那一维，所以给它更长的行程
        var marqueeW = (int)(free / 3);
        var scopeW = (int)(free - marqueeW);

        // ---- 跑马灯 ----
        DrawMarquee(g, x, marqueeW, f.Detail);
        x += marqueeW + _gap;

        // ---- 迷你示波器 ----
        DrawScope(g, (int)x, scopeW, f.State, wave);
        x += scopeW + _gap;

        // ---- 模型 / 上下文 / 额度 ----
        if (statText.Length > 0)
        {
            // 5h 快满的时候整串变成状态色，别的时候一律暗色——
            // 这些是"要看的时候才看"的信息，不该跟状态抢注意力
            var brush = !f.UsageStale && f.UsageFiveHour >= 85 ? _brAccent! : _brDim;
            g.DrawString(statText, _fMeta, brush, x, (_h - statSize.Height) / 2);
            x += statSize.Width + _gap;
        }

        // ---- 来源药丸 ----
        if (chipText.Length > 0)
        {
            var chipH = _h * 0.46f;
            var chipY = (_h - chipH) / 2;
            using var path = new GraphicsPath();
            path.AddArc(x, chipY, chipH, chipH, 90, 180);
            path.AddArc(x + chipW - chipH, chipY, chipH, chipH, 270, 180);
            path.CloseFigure();
            g.FillPath(_brAccent!, path);
            g.DrawString(chipText, _fChip, _brChipFg, x + _h * 0.17f, (_h - chipSize.Height) / 2);
            x += chipW + _gap;
        }

        // ---- 项目 / 最后事件 ----
        if (metaText.Length > 0)
            g.DrawString(metaText, _fMeta, _brDim, x, (_h - metaSize.Height) / 2);
    }

    void DrawMarquee(Graphics g, float x, int width, string text)
    {
        if (text.Length == 0) text = "—";
        var size = g.MeasureString(text, _fMain);
        var clip = g.Clip;
        g.SetClip(new RectangleF(x, 0, width, _h));
        if (size.Width > width)
        {
            if (_marqueeX < -(size.Width + 60)) _marqueeX = width;
            g.DrawString(text, _fMain, _brText, x + _marqueeX, (_h - size.Height) / 2);
        }
        else
        {
            _marqueeX = 0;
            g.DrawString(text, _fMain, _brText, x, (_h - size.Height) / 2);
        }
        g.Clip = clip;
    }

    void DrawScope(Graphics g, int sx, int width, ScopeState state, WaveGenerator wave)
    {
        if (width < 8) return;
        // 采样环是按建窗时的屏宽分配的。换到更宽的屏（或多屏拼接）时
        // 请求宽度可能超过容量，这里夹一下，否则 CopySmoothed 会读越界。
        if (width > wave.Capacity) width = wave.Capacity;
        var sy = (_h - _scopeH) / 2;
        g.FillRectangle(_brScopeBg, sx, sy, width, _scopeH);
        var mid = sy + _scopeH / 2f;
        g.DrawLine(_penGrid, sx, mid, sx + width, mid);

        if (state == ScopeState.Disconnected)
        {
            // 断开时用警示斜纹顶掉波形——一眼就知道"这不是 Claude 的状态，是工具本身出问题了"
            for (var i = -_scopeH; i < width; i += 12)
                g.DrawLine(_penHaz!, sx + i, sy + _scopeH, sx + i + _scopeH, sy);
            return;
        }

        if (_wave.Length != width) _wave = new float[width];
        wave.CopySmoothed(_wave);

        var half = _scopeH * 0.42f;
        var pts = new PointF[width];
        for (var i = 0; i < width; i++)
            pts[i] = new PointF(sx + i, mid - _wave[i] * half);

        g.DrawLines(_penGlow!, pts);
        g.DrawLines(_penTrace!, pts);

        var dot = Math.Max(2.5f, _h * 0.07f);
        g.FillEllipse(_brAccent!, sx + width - dot, mid - wave.Latest * half - dot / 2, dot, dot);
    }

    string BuildMeta(StripFrame f)
    {
        var parts = new List<string>(2);
        if (f.Project.Length > 0) parts.Add(f.Project);
        if (f.State != ScopeState.Disconnected)
            parts.Add(FormatAge(DateTime.UtcNow - f.LastEventAt));
        return string.Join("  ·  ", parts);
    }

    /// 额度不新鲜就不显示数字——绝不外推，也绝不让过期值看起来像实时值。
    /// 这两个数来自桌面版写的采样文件，只有桌面版在跑时才更新。
    static string BuildFiveHour(StripFrame f) =>
        f.UsageFiveHour is not { } p ? "" : f.UsageStale ? "5h —" : $"5h {p}%";

    static string BuildWeek(StripFrame f) =>
        f.UsageWeek is not { } p ? "" : f.UsageStale ? "周 —" : $"周 {p}%";

    static string FormatClock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    static string FormatAge(TimeSpan t)
    {
        var s = (int)Math.Round(t.TotalSeconds);
        if (s < 60) return $"{s} 秒前";
        var m = s / 60;
        return m < 60 ? $"{m} 分前" : $"{m / 60} 小时前";
    }

    public void Dispose()
    {
        _fWord.Dispose(); _fTimer.Dispose(); _fMain.Dispose(); _fMeta.Dispose(); _fChip.Dispose();
        _brDim.Dispose(); _brText.Dispose(); _brChipFg.Dispose(); _brScopeBg.Dispose();
        _penGrid.Dispose(); _penDiv.Dispose();
        _brAccent?.Dispose(); _penGlow?.Dispose(); _penTrace?.Dispose(); _penHaz?.Dispose();
    }
}
