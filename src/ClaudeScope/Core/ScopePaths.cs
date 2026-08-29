namespace ClaudeScope.Core;

/// <summary>
/// 所有路径集中在这里。刻意区分两类：
///   - 程序自己的运行时数据 -> %APPDATA%\claude-scope（我们可写）
///   - Claude 的数据 -> ~/.claude（只读它的会话记录；settings.json 只在装 hook 时改）
/// 绝不碰 %APPDATA%\Claude —— 那是 MSIX 重定向目录，从外面写容易白折腾。
/// </summary>
public static class ScopePaths
{
    public static string Home { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string AppData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// 我们自己的运行时目录。
    /// 可以用 CLAUDE_SCOPE_HOME 覆盖——便携模式（数据放 exe 旁边）和自动化测试都靠它。
    /// 注意不能指望改 APPDATA 环境变量：GetFolderPath 走的是 Shell 已知文件夹，不读环境变量。
    /// </summary>
    public static string RuntimeDir { get; } =
        Environment.GetEnvironmentVariable("CLAUDE_SCOPE_HOME") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(AppData, "claude-scope");
    public static string LogDir { get; } = Path.Combine(RuntimeDir, "logs");
    public static string LogFile { get; } = Path.Combine(LogDir, "daemon.log");
    public static string RuntimeFile { get; } = Path.Combine(RuntimeDir, "runtime.json");
    public static string ConfigFile { get; } = Path.Combine(RuntimeDir, "config.json");

    /// Claude 那边（不在 MSIX 重定向范围内，可以放心读）
    public static string ClaudeDir { get; } = Path.Combine(Home, ".claude");
    public static string ClaudeSettings { get; } = Path.Combine(ClaudeDir, "settings.json");
    public static string ProjectsDir { get; } = Path.Combine(ClaudeDir, "projects");

    /// 桌面版写的额度采样文件。这个确实在 %APPDATA%\Claude 下，但我们只读不写。
    public static string PlanUsageFile { get; } =
        Path.Combine(AppData, "Claude", "plan-usage-history.json");

    /// 当前可执行文件的完整路径（单文件发布下 Assembly.Location 是空的，必须用这个）
    public static string ExePath { get; } = Environment.ProcessPath ?? "claude-scope.exe";

    public static void EnsureRuntimeDirs()
    {
        Directory.CreateDirectory(RuntimeDir);
        Directory.CreateDirectory(LogDir);
    }
}
