using Microsoft.Win32;

namespace ClaudeScope.Core;

/// <summary>
/// 开机自启，用 HKCU 的 Run 键。
///
/// 本来用的是计划任务（能设 20 秒延迟，登录时更稳），但实测在受管控的机器上
/// schtasks /SC ONLOGON 直接 "Access is denied"——即使加了 /RU 指定当前用户也一样，
/// 而同一台机器上 Task Scheduler 的 COM API 和 Run 键都正常。
/// 目标用户多半就是这种企业管控环境，所以选永远不需要管理员的 Run 键。
///
/// 丢掉的延迟由横幅自己的守位定时器补偿：它每 3 秒重新按一次位置和 AppBar 占位，
/// 所以就算登录瞬间桌面还没就绪，几秒内也会自己纠正。
/// </summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "claude-scope";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>已登记的路径，用来发现"exe 挪过位置但自启还指向旧路径"。</summary>
    public static string? RegisteredPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) as string;
        }
        catch { return null; }
    }

    public static (bool Ok, string Message) Enable()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            k.SetValue(ValueName, $"\"{ScopePaths.ExePath}\"");
            return (true, "已设为开机自启（登录时启动）。");
        }
        catch (Exception ex)
        {
            return (false, $"设置失败：{ex.Message}");
        }
    }

    public static (bool Ok, string Message) Disable()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(ValueName) is null) return (true, "本来就没设开机自启。");
            k.DeleteValue(ValueName, throwOnMissingValue: false);
            return (true, "开机自启已取消。");
        }
        catch (Exception ex)
        {
            return (false, $"取消失败：{ex.Message}");
        }
    }
}
