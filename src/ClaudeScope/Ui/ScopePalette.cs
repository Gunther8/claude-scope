using System.Drawing;
using ClaudeScope.Core;

namespace ClaudeScope.Ui;

public enum WaveShape { Flat, Sine, Burst, Saw, Clipped, Pulse, Ping }

public readonly record struct WaveSpec(WaveShape Shape, double Amp, double Freq, double Noise, double Scroll);

/// <summary>
/// 每种状态的视觉编码。四个维度同时用上，因为 1 米余光看色相基本没用：
/// 色相只是粗分类，真正能被余光抓到的是波形的运动特征和整条横幅的明暗。
/// </summary>
public static class ScopePalette
{
    public static Color ColorOf(ScopeState s) => s switch
    {
        ScopeState.Idle => ColorTranslator.FromHtml("#4A5560"),
        ScopeState.Thinking => ColorTranslator.FromHtml("#22D3EE"),
        ScopeState.Writing => ColorTranslator.FromHtml("#A78BFA"),
        ScopeState.Running => ColorTranslator.FromHtml("#FBBF24"),
        ScopeState.Done => ColorTranslator.FromHtml("#34D399"),
        ScopeState.Error => ColorTranslator.FromHtml("#F87171"),
        ScopeState.Waiting => ColorTranslator.FromHtml("#FB923C"),
        ScopeState.Stalled => ColorTranslator.FromHtml("#7DD3FC"),
        ScopeState.Dead => ColorTranslator.FromHtml("#8B96A3"),
        _ => ColorTranslator.FromHtml("#FACC15")
    };

    /// 波形形状：不同状态的运动特征要一眼分得开
    public static WaveSpec WaveOf(ScopeState s) => s switch
    {
        ScopeState.Idle => new(WaveShape.Flat, 0.05, 0.5, 0.012, 45),
        ScopeState.Thinking => new(WaveShape.Sine, 0.30, 1.15, 0.02, 115),
        ScopeState.Writing => new(WaveShape.Burst, 0.38, 2.4, 0.03, 190),
        ScopeState.Running => new(WaveShape.Saw, 0.48, 4.6, 0.13, 340),
        ScopeState.Done => new(WaveShape.Sine, 0.42, 0.9, 0.01, 220),
        ScopeState.Error => new(WaveShape.Clipped, 0.78, 3.1, 0.34, 300),
        ScopeState.Waiting => new(WaveShape.Pulse, 0.62, 0.42, 0.015, 95),
        ScopeState.Stalled => new(WaveShape.Ping, 0.50, 0.66, 0.008, 70),
        ScopeState.Dead => new(WaveShape.Flat, 0.0, 0.2, 0.004, 30),
        _ => new(WaveShape.Flat, 0.0, 0.0, 0.0, 0)
    };

    public static string LabelOf(ScopeState s) => s switch
    {
        ScopeState.Idle => "空闲",
        ScopeState.Thinking => "思考中",
        ScopeState.Writing => "写代码",
        ScopeState.Running => "执行命令",
        ScopeState.Done => "完成",
        ScopeState.Error => "出错",
        ScopeState.Waiting => "等你确认",
        ScopeState.Stalled => "疑似卡住",
        ScopeState.Dead => "会话已终止",
        _ => "状态源断开"
    };

    /// 演示模式下的示例文案，让每种状态看起来像真事
    public static string DemoTextOf(ScopeState s) => s switch
    {
        ScopeState.Idle => "当前没有正在运行的会话",
        ScopeState.Thinking => "在读你的需求，规划要动哪几个文件",
        ScopeState.Writing => "Edit · server.js",
        ScopeState.Running => "Bash · npm test -- --runInBand",
        ScopeState.Done => "改完了，3 个文件，测试全绿",
        ScopeState.Error => "Bash 失败：exit 1 · ELIFECYCLE npm test",
        ScopeState.Waiting => "等待授权 · Bash · rm -rf ./dist",
        ScopeState.Stalled => "Bash · 等一个迟迟不返回的远程命令",
        ScopeState.Dead => "Bash · npm run build",
        _ => "连不上守护进程"
    };

    /// 需要显示计时器的状态：这是区分「在慢慢干活」和「已经死了」的关键
    public static bool ShowsTimer(ScopeState s) =>
        s is ScopeState.Thinking or ScopeState.Writing or ScopeState.Running
          or ScopeState.Waiting or ScopeState.Stalled;
}
