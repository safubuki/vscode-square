using System.Runtime.InteropServices;
using VscodeSquare.Panel.Models;

namespace VscodeSquare.Panel.Services;

public sealed class WindowArranger
{
    private const int SW_RESTORE = 9;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    public int Arrange(IReadOnlyList<WindowSlot> slots, int gap)
    {
        var workArea = GetPrimaryWorkArea();
        var normalizedGap = Math.Clamp(gap, 0, 64);
        var cellWidth = Math.Max(320, (workArea.Width - normalizedGap * 3) / 2);
        var cellHeight = Math.Max(240, (workArea.Height - normalizedGap * 3) / 2);
        var arranged = 0;

        for (var index = 0; index < Math.Min(4, slots.Count); index++)
        {
            var slot = slots[index];
            if (slot.WindowHandle == IntPtr.Zero || !IsWindow(slot.WindowHandle))
            {
                continue;
            }

            var column = index % 2;
            var row = index / 2;
            var x = workArea.Left + normalizedGap + column * (cellWidth + normalizedGap);
            var y = workArea.Top + normalizedGap + row * (cellHeight + normalizedGap);

            ShowWindow(slot.WindowHandle, SW_RESTORE);
            if (SetWindowPos(slot.WindowHandle, IntPtr.Zero, x, y, cellWidth, cellHeight, SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_SHOWWINDOW))
            {
                arranged++;
            }
        }

        return arranged;
    }

    public bool Focus(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        ShowWindow(windowHandle, SW_RESTORE);
        return SetForegroundWindow(windowHandle);
    }

    private static WorkArea GetPrimaryWorkArea()
    {
        var monitor = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };

        if (!GetMonitorInfo(monitor, ref info))
        {
            return new WorkArea(0, 0, 1280, 720);
        }

        return new WorkArea(
            info.rcWork.Left,
            info.rcWork.Top,
            info.rcWork.Right - info.rcWork.Left,
            info.rcWork.Bottom - info.rcWork.Top);
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private readonly record struct WorkArea(int Left, int Top, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
