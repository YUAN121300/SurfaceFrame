using System;
using System.Runtime.InteropServices;

namespace SurfaceFrame;

// 自适应亮度仍交给 Windows。这里只维持进程在线，并在离家后关闭显示器电源。
static class DisplayPower
{
    const int WM_SYSCOMMAND = 0x0112;
    const int SC_MONITORPOWER = 0xF170;
    const uint ES_CONTINUOUS = 0x80000000;
    const uint ES_SYSTEM_REQUIRED = 0x00000001;
    const uint ES_DISPLAY_REQUIRED = 0x00000002;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr Topmost = new(-1);

    public static void KeepSystemAwake(bool displayRequired)
    {
        var flags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED;
        if (displayRequired)
            flags |= ES_DISPLAY_REQUIRED;
        SetThreadExecutionState(flags);
    }

    public static void AllowSleep()
        => SetThreadExecutionState(ES_CONTINUOUS);

    public static void MonitorOff(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2);
    }

    public static void MonitorOn(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)(-1));
    }

    public static void KeepTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        SetWindowPos(hwnd, Topmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint esFlags);
}
