namespace VscodeSquare.Panel.Services;

internal enum WindowSlotRefreshState
{
    NoWindow,
    Missing,
    Ready
}

internal sealed record WindowSlotStatusSnapshot(
    string Name,
    IntPtr WindowHandle,
    string WindowTitle,
    string CurrentWorkspacePath,
    DateTimeOffset? WorkspaceRefreshedAt);

internal sealed record WindowSlotStatusRefreshResult(
    string SlotName,
    IntPtr WindowHandle,
    WindowSlotRefreshState State,
    WindowInfo? Window,
    AiStatusSnapshot? AiStatus,
    string? CurrentWorkspacePath,
    DateTimeOffset? WorkspaceRefreshedAt,
    long ElapsedMilliseconds);
