using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VscodeSquare.Panel.Services;

public sealed class WindowEnumerator
{
    public IReadOnlyList<WindowInfo> GetVsCodeWindows()
    {
        var windows = new List<WindowInfo>();

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsCandidateWindow(hWnd))
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hWnd, out var processId);
            if (!IsVsCodeProcess(processId))
            {
                return true;
            }

            windows.Add(new WindowInfo(hWnd, GetTitle(hWnd), processId));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public WindowInfo? TryGetWindow(IntPtr hWnd)
    {
        if (!IsLiveWindow(hWnd) || !IsCandidateWindow(hWnd))
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hWnd, out var processId);
        return IsVsCodeProcess(processId)
            ? new WindowInfo(hWnd, GetTitle(hWnd), processId)
            : null;
    }

    public bool IsLiveWindow(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero && IsWindow(hWnd);
    }

    private static bool IsCandidateWindow(IntPtr hWnd)
    {
        return IsWindowVisible(hWnd) && GetWindowTextLength(hWnd) > 0;
    }

    private static bool IsVsCodeProcess(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName.ToLowerInvariant();

            return processName is "code"
                or "code - insiders"
                or "vscodium"
                or "codium";
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string GetTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}

public sealed record WindowInfo(IntPtr Handle, string Title, uint ProcessId);
