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
    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_MOVE = 0x0001;
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
        KeepSystemAwake(true);
        if (hwnd != IntPtr.Zero)
            SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)(-1));
        WakeInputPulse();
    }

    static void WakeInputPulse()
    {
        try
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT { dx = 0, dy = 0, dwFlags = MOUSEEVENTF_MOVE }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }
        catch { }
    }

    public static void KeepTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        SetWindowPos(hwnd, Topmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static void MarkFullscreen(IntPtr hwnd, bool fullscreen)
    {
        if (hwnd == IntPtr.Zero)
            return;
        try
        {
            var taskbar = (ITaskbarList2)new TaskbarList();
            taskbar.HrInit();
            taskbar.MarkFullscreenWindow(hwnd, fullscreen);
        }
        catch { }
    }

    [ComImport, Guid("602D4995-B13A-429b-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList2
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    class TaskbarList { }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint esFlags);
}
