namespace TurtleAIQuartetHub.Panel.Services;

internal enum ManagedWindowCloseStatus
{
    AlreadyClosed,
    Closed,
    RequestFailed,
    TimedOut
}

internal readonly record struct ManagedWindowCloseResult(ManagedWindowCloseStatus Status)
{
    public bool Succeeded => Status is ManagedWindowCloseStatus.AlreadyClosed or ManagedWindowCloseStatus.Closed;
}

/// <summary>
/// 外部アプリの WM_CLOSE は「要求をキューへ送れた」ことしか保証しないため、
/// HWND が実際に破棄されるまで非同期で確認する。
/// </summary>
internal sealed class ManagedWindowCloseService
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<IntPtr, bool> _isLiveWindow;
    private readonly Func<IntPtr, bool> _requestClose;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;

    public ManagedWindowCloseService(WindowEnumerator windowEnumerator, WindowArranger windowArranger)
        : this(
            windowEnumerator.IsLiveWindow,
            windowArranger.Close,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            DefaultTimeout,
            DefaultPollInterval)
    {
    }

    internal ManagedWindowCloseService(
        Func<IntPtr, bool> isLiveWindow,
        Func<IntPtr, bool> requestClose,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan timeout,
        TimeSpan pollInterval)
    {
        ArgumentNullException.ThrowIfNull(isLiveWindow);
        ArgumentNullException.ThrowIfNull(requestClose);
        ArgumentNullException.ThrowIfNull(delay);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        _isLiveWindow = isLiveWindow;
        _requestClose = requestClose;
        _delay = delay;
        _timeout = timeout;
        _pollInterval = pollInterval;
    }

    public async Task<ManagedWindowCloseResult> CloseAndWaitAsync(
        IntPtr windowHandle,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle == IntPtr.Zero || !_isLiveWindow(windowHandle))
        {
            return new ManagedWindowCloseResult(ManagedWindowCloseStatus.AlreadyClosed);
        }

        if (!_requestClose(windowHandle))
        {
            return new ManagedWindowCloseResult(ManagedWindowCloseStatus.RequestFailed);
        }

        // 経過時間ではなく最大試行回数を先に確定し、テスト用の即時 delay でも
        // タイムアウト経路を決定的に検証できるようにする。
        var pollCount = Math.Max(1, (int)Math.Ceiling(_timeout.TotalMilliseconds / _pollInterval.TotalMilliseconds));
        for (var attempt = 0; attempt < pollCount; attempt++)
        {
            if (!_isLiveWindow(windowHandle))
            {
                return new ManagedWindowCloseResult(ManagedWindowCloseStatus.Closed);
            }

            await _delay(_pollInterval, cancellationToken);
        }

        return new ManagedWindowCloseResult(
            _isLiveWindow(windowHandle)
                ? ManagedWindowCloseStatus.TimedOut
                : ManagedWindowCloseStatus.Closed);
    }
}
