using System.Runtime.InteropServices;

namespace ClaudeScope.Native;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
public struct APPBARDATA
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public IntPtr lParam;
}

public static class Win32
{
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    public const uint ABE_TOP = 1, ABE_BOTTOM = 3;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOSIZE = 0x0001;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint SPI_SETWORKAREA = 0x002F;
    public const uint SPIF_SENDCHANGE = 0x02;

    // 用 DllImport 而不是 LibraryImport：后者的源生成器要求整个项目开 AllowUnsafeBlocks，
    // 而这里的互操作全是简单类型，不值得为它把 unsafe 打开。
    [DllImport("shell32.dll")]
    public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint param, ref RECT pv, uint winIni);
}

/// <summary>
/// AppBar 占位：让最大化的窗口自动避开横幅。
///
/// 这里有个必须守住的约定——注册方（也就是我们自己的窗口）必须活着，
/// 并且要在退出时 ABM_REMOVE 把工作区还回去。强杀就跑不到那一步，
/// 屏幕边上会留下一条永远收不回来的空白，只能靠 Reset-WorkArea 那种手段救。
/// 所以 Dispose 一定要走到，Form 的 FormClosing 里也兜一层。
/// </summary>
public sealed class AppBarSlot : IDisposable
{
    APPBARDATA _abd;
    bool _registered;

    public RECT Placed { get; private set; }

    public void Register(IntPtr hwnd, bool bottom)
    {
        if (_registered) return;
        _abd = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = hwnd,
            uCallbackMessage = 0x0400 + 42,
            uEdge = bottom ? Win32.ABE_BOTTOM : Win32.ABE_TOP
        };
        Win32.SHAppBarMessage(Win32.ABM_NEW, ref _abd);
        _registered = true;
    }

    /// <summary>
    /// AppBar 协议要走全：QUERYPOS 之后必须**采纳**外壳调整过的矩形，只把厚度改回我们要的值。
    /// 直接拿原始矩形去 SETPOS 会盖住任务栏——贴底边时实测就压在任务栏上了。
    /// </summary>
    public RECT Place(RECT want, bool bottom, int thickness)
    {
        if (!_registered) { Placed = want; return want; }

        _abd.rc = want;
        Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref _abd);

        var r = _abd.rc;
        if (bottom) r.Top = r.Bottom - thickness;
        else r.Bottom = r.Top + thickness;

        _abd.rc = r;
        Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref _abd);
        Placed = _abd.rc;
        return Placed;
    }

    public void Dispose()
    {
        if (!_registered) return;
        Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref _abd);
        _registered = false;
    }
}
