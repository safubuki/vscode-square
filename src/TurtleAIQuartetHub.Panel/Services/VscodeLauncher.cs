using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class VscodeLauncher
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectNameChange = 0x800C;
    private const int ObjectIdWindow = 0;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private static readonly TimeSpan RemoteWindowProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WindowProbeInterval = TimeSpan.FromMilliseconds(250);

    // lock を握ったままウィンドウが無いプロセスを「ゾンビ」と断ずる前に与える猶予。
    // 起動直後でメインウィンドウ描画前の正常な VS Code を誤って kill しないための保険。
    private static readonly TimeSpan ZombieGracePeriod = TimeSpan.FromSeconds(45);

    // Process names (without extension) that a slot's code.lock may legitimately belong to.
    // Mirrors WindowEnumerator.GetVsCodeWindows so we never terminate a recycled PID owned by an unrelated process.
    private static readonly string[] VsCodeProcessNames = ["code", "code - insiders", "vscodium", "codium"];

    private readonly WindowEnumerator _windowEnumerator;

    public VscodeLauncher(WindowEnumerator windowEnumerator)
    {
        _windowEnumerator = windowEnumerator;
    }

    public bool IsCodeCommandAvailable(string codeCommand)
    {
        return ResolveCodeCommand(codeCommand) is not null;
    }

    public async Task<IReadOnlyList<WindowAssignment>> LaunchMissingAsync(
        IReadOnlyList<WindowSlot> slots,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        var launchTargets = slots
            .Where(slot => slot.WindowHandle == IntPtr.Zero || !_windowEnumerator.IsLiveWindow(slot.WindowHandle))
            .Take(4)
            .ToList();

        if (launchTargets.Count == 0)
        {
            return [];
        }

        foreach (var slot in launchTargets)
        {
            slot.WindowStatus = SlotWindowStatus.Launching;
        }

        var knownHandles = await GetKnownHandlesAsync(cancellationToken);

        var resolvedCodeCommand = ResolveCodeCommand(config.CodeCommand) ?? config.CodeCommand;
        var assignments = new List<WindowAssignment>();
        var timeout = TimeSpan.FromSeconds(config.LaunchTimeoutSeconds);

        foreach (var slot in launchTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            knownHandles.UnionWith(await GetKnownHandlesAsync(cancellationToken));

            if (config.UseDedicatedUserDataDirs)
            {
                await Task.Run(() => KillZombieProcess(slot, config), cancellationToken);
                await PrepareDedicatedUserDataAsync(slot, config, resolvedCodeCommand, cancellationToken);
            }

            var launchPath = GetLaunchPath(slot, config);
            var existingSlotWindow = TryFindExistingSlotWindow(slot, config, launchPath);
            if (existingSlotWindow is not null)
            {
                DiagnosticLog.Write($"Reattached existing VS Code window for slot {slot.Name}: handle=0x{existingSlotWindow.Handle.ToInt64():X}, pid={existingSlotWindow.ProcessId}.");
                knownHandles.Add(existingSlotWindow.Handle);
                assignments.Add(new WindowAssignment(slot, existingSlotWindow));
                continue;
            }

            // 右側AIチャット欄の幅は、ウィンドウが存在しない起動前に準備する。
            // フォーカス直前の storage.json 書換えは、4面状態の Electron 再描画と白飛びを招く。
            var launchLayout = VscodeLayoutState.ExpandAuxiliaryBarForFocus(slot.PreferredLayout, 1920);
            slot.PreferredLayout = launchLayout;
            VscodeLayoutState.TryApplyPreferredLayout(slot, config, launchLayout);

            var assignment = await LaunchWindowAsync(slot, config, resolvedCodeCommand, launchPath, knownHandles, timeout, cancellationToken);
            if (assignment is null)
            {
                DiagnosticLog.Write(LogLevel.Warn, $"No new VS Code window detected for slot {slot.Name} within {timeout.TotalSeconds:0} seconds.");
                slot.WindowStatus = SlotWindowStatus.Missing;
                continue;
            }

            knownHandles.Add(assignment.Window.Handle);
            assignments.Add(assignment);
        }

        return assignments;
    }

    private async Task<WindowAssignment?> LaunchWindowAsync(
        WindowSlot slot,
        AppConfig config,
        string codeCommand,
        string? launchPath,
        HashSet<IntPtr> knownHandles,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var launchCodeCommand = GetCodeCommandForLaunch(config.CodeCommand, codeCommand, launchPath);

        if (ShouldAttemptRemoteFallback(config, launchPath))
        {
            return await LaunchRemoteWindowWithFallbackAsync(
                slot,
                config,
                launchCodeCommand,
                launchPath!,
                knownHandles,
                timeout,
                cancellationToken);
        }

        DiagnosticLog.Write($"Starting VS Code for slot {slot.Name}: {launchCodeCommand} {GetLaunchArguments(slot, config, launchPath)}");
        var launchedProcessId = await Task.Run(() => StartCode(launchCodeCommand, slot, config, launchPath), cancellationToken);
        var window = await WaitForNewWindowAsync(
            slot,
            config,
            knownHandles,
            timeout,
            expectedProcessId: null,
            launchPath,
            fallbackWindowProvider: () => TryFindExistingSlotWindow(slot, config, launchPath),
            cancellationToken);
        return window is null ? null : CreateNewWindowAssignment(slot, window);
    }

    private async Task<WindowAssignment?> LaunchRemoteWindowWithFallbackAsync(
        WindowSlot slot,
        AppConfig config,
        string codeCommand,
        string launchPath,
        HashSet<IntPtr> knownHandles,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var reconnectTimeout = GetRemoteReconnectTimeout(config, totalTimeout);

        DiagnosticLog.Write($"Starting VS Code for slot {slot.Name}: {codeCommand} {GetLaunchArguments(slot, config, launchPath)}");
        var launchedProcessId = await Task.Run(() => StartCode(codeCommand, slot, config, launchPath), cancellationToken);

        var reconnectStopwatch = Stopwatch.StartNew();
        var remoteWindow = await WaitForNewWindowAsync(
            slot,
            config,
            knownHandles,
            reconnectTimeout,
            expectedProcessId: null,
            launchPath,
            fallbackWindowProvider: () => TryFindExistingSlotWindow(slot, config, launchPath),
            cancellationToken);
        if (remoteWindow is not null)
        {
            var remainingReconnectTime = GetRemainingTime(reconnectTimeout, reconnectStopwatch);
            remoteWindow = await WaitForLaunchPathVisibleAsync(remoteWindow, launchPath, remainingReconnectTime, cancellationToken);
        }

        if (remoteWindow is not null)
        {
            return CreateNewWindowAssignment(slot, remoteWindow);
        }

        DiagnosticLog.Write(
            $"Remote workspace reconnect timed out for slot {slot.Name} after {reconnectTimeout.TotalSeconds:0} seconds. Falling back to an empty VS Code window.");

        await Task.Run(() =>
        {
            TryTerminateLaunchProcess(remoteWindow?.ProcessId ?? launchedProcessId, slot.Name);
            KillZombieProcess(slot, config);
        }, cancellationToken);
        knownHandles.UnionWith(await GetKnownHandlesAsync(cancellationToken));

        var fallbackTimeout = GetRemainingTime(totalTimeout, totalStopwatch);
        if (fallbackTimeout <= TimeSpan.Zero)
        {
            DiagnosticLog.Write(LogLevel.Warn, $"No timeout budget remains for slot {slot.Name} fallback launch.");
            return null;
        }

        DiagnosticLog.Write($"Starting fallback VS Code window for slot {slot.Name}: {codeCommand} {GetLaunchArguments(slot, config, null)}");
        var fallbackProcessId = await Task.Run(() => StartCode(codeCommand, slot, config, null), cancellationToken);

        // cmd.exe ラッパー経由の場合、返る PID は cmd のものであって VS Code のものではない。
        // その PID で待つと永久に一致しないため、ウィンドウ列挙による突合に切り替える。
        var expectedProcessId = IsWrapperLaunch(codeCommand) ? null : fallbackProcessId;
        var fallbackWindow = await WaitForNewWindowAsync(
            slot,
            config,
            knownHandles,
            fallbackTimeout,
            expectedProcessId,
            launchPath: null,
            fallbackWindowProvider: () => TryFindExistingSlotWindow(slot, config, null),
            cancellationToken);
        return fallbackWindow is null ? null : CreateNewWindowAssignment(slot, fallbackWindow);
    }

    private static WindowAssignment CreateNewWindowAssignment(WindowSlot slot, WindowInfo window)
    {
        var cloaked = WindowArranger.SetCloaked(window.Handle, true);
        if (cloaked)
        {
            _ = ReleaseLaunchCloakFailsafeAsync(window.Handle);
        }

        return new WindowAssignment(slot, window, cloaked);
    }

    private static async Task ReleaseLaunchCloakFailsafeAsync(IntPtr windowHandle)
    {
        try
        {
            // 配置処理が例外やキャンセルで中断しても、ウィンドウを不可視のまま残さない。
            await Task.Delay(TimeSpan.FromSeconds(12));
            WindowArranger.SetCloaked(windowHandle, false);
        }
        catch
        {
            // Failsafe must never affect launch processing.
        }
    }

    public WindowInfo? TryFindExistingSlotWindow(WindowSlot slot, AppConfig config, string? launchPath = null)
    {
        return TryFindSlotOwnedWindow(slot, config, launchPath);
    }

    private async Task<WindowInfo?> WaitForNewWindowAsync(
        WindowSlot slot,
        AppConfig config,
        HashSet<IntPtr> knownHandles,
        TimeSpan timeout,
        uint? expectedProcessId,
        string? launchPath,
        Func<WindowInfo?>? fallbackWindowProvider,
        CancellationToken cancellationToken)
    {
        var existingWindow = FindExpectedNewWindow(slot, config, knownHandles, expectedProcessId, launchPath);
        if (existingWindow is not null)
        {
            return existingWindow;
        }

        var fallbackWindow = fallbackWindowProvider?.Invoke();
        if (fallbackWindow is not null)
        {
            return fallbackWindow;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var timeoutCts = new CancellationTokenSource(timeout);
        var completionSource = new TaskCompletionSource<WindowInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);

        WinEventDelegate callback = (_, _, windowHandle, objectId, childId, _, _) =>
        {
            if (objectId != ObjectIdWindow || childId != 0 || knownHandles.Contains(windowHandle))
            {
                return;
            }

            var window = _windowEnumerator.TryGetWindow(windowHandle);
            if (window is not null
                && IsExpectedNewWindow(slot, config, window, knownHandles, expectedProcessId, launchPath))
            {
                // Electron の初期白サーフェスを見せない。MainWindow 側で配置と描画猶予を
                // 完了してからクロークを解除する。可視属性は維持されるので HWND 捕捉は継続できる。
                WindowArranger.SetCloaked(window.Handle, true);
                completionSource.TrySetResult(window);
            }
        };

        var hook = SetWinEventHook(
            EventObjectCreate,
            EventObjectNameChange,
            IntPtr.Zero,
            callback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);

        if (hook == IntPtr.Zero)
        {
            DiagnosticLog.Write(LogLevel.Warn, "WinEvent hook could not be registered while waiting for a VS Code window.");
            return FindExpectedNewWindow(slot, config, knownHandles, expectedProcessId, launchPath) ?? fallbackWindowProvider?.Invoke();
        }

        try
        {
            existingWindow = FindExpectedNewWindow(slot, config, knownHandles, expectedProcessId, launchPath);
            if (existingWindow is not null)
            {
                return existingWindow;
            }

            fallbackWindow = fallbackWindowProvider?.Invoke();
            if (fallbackWindow is not null)
            {
                return fallbackWindow;
            }

            using var timeoutRegistration = timeoutCts.Token.Register(() => completionSource.TrySetResult(null));
            using var cancellationRegistration = cancellationToken.Register(() => completionSource.TrySetCanceled(cancellationToken));
            while (!timeoutCts.IsCancellationRequested)
            {
                var completedTask = await Task.WhenAny(
                    completionSource.Task,
                    Task.Delay(WindowProbeInterval, cancellationToken));
                if (completedTask == completionSource.Task)
                {
                    return await completionSource.Task;
                }

                existingWindow = FindExpectedNewWindow(slot, config, knownHandles, expectedProcessId, launchPath);
                if (existingWindow is not null)
                {
                    return existingWindow;
                }

                fallbackWindow = fallbackWindowProvider?.Invoke();
                if (fallbackWindow is not null)
                {
                    return fallbackWindow;
                }
            }

            return null;
        }
        finally
        {
            UnhookWinEvent(hook);
            GC.KeepAlive(callback);
        }
    }

    private WindowInfo? FindExpectedNewWindow(
        WindowSlot slot,
        AppConfig config,
        HashSet<IntPtr> knownHandles,
        uint? expectedProcessId,
        string? launchPath)
    {
        var allVsCodeWindows = _windowEnumerator.GetVsCodeWindows();
        var windows = allVsCodeWindows
            .Where(item => !knownHandles.Contains(item.Handle))
            .Where(item => !expectedProcessId.HasValue || item.ProcessId == expectedProcessId.Value)
            .OrderBy(window => window.ProcessId)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var match = windows.FirstOrDefault(window => IsExpectedNewWindow(slot, config, window, knownHandles, expectedProcessId, launchPath));
        if (match is null && windows.Count > 0)
        {
            // 候補ウィンドウはあるのに 1 つも受理できなかったとき、なぜ弾いたのかを残す。
            // 「No new window」だけだと専用 user-data の lock 待ちなのか別原因か切り分けられないため。
            DiagnosticLog.Write(
                $"No VS Code window accepted for slot {slot.Name}: "
                + $"{windows.Count} unknown candidate(s) rejected by IsExpectedNewWindow "
                + $"(dedicated={config.UseDedicatedUserDataDirs}, launchPath={launchPath ?? "<none>"}). "
                + $"Candidates: {string.Join(", ", windows.Select(w => $"pid={w.ProcessId},title='{w.Title}'"))}");
        }

        return match;
    }

    private static bool IsExpectedNewWindow(
        WindowSlot slot,
        AppConfig config,
        WindowInfo window,
        HashSet<IntPtr> knownHandles,
        uint? expectedProcessId,
        string? launchPath)
    {
        if (knownHandles.Contains(window.Handle)
            || expectedProcessId.HasValue && window.ProcessId != expectedProcessId.Value)
        {
            return false;
        }

        if (config.UseDedicatedUserDataDirs
            && TryReadSlotLockProcessId(slot, config, out var slotProcessId)
            && IsProcessAlive(slotProcessId))
        {
            return window.ProcessId == slotProcessId;
        }

        if (config.UseDedicatedUserDataDirs && !string.IsNullOrWhiteSpace(launchPath))
        {
            // 専用 user-data モードでは、slot の code.lock がまだ書かれていない（=起動直後）か、
            // lock のプロセスが死んでいる間は、ワークスペース付き起動のウィンドウを一切受理しない。
            // ここで何度も弾かれ続けるとタイムアウトに至るので、その状況を残す。
            DiagnosticLog.Write(
                $"Rejecting VS Code window for slot {slot.Name} (pid={window.ProcessId}, title='{window.Title}'): "
                + "dedicated user-data with launch path but no live code.lock owner yet.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(launchPath)
            && VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(window.Title, launchPath))
        {
            return true;
        }

        return !config.UseDedicatedUserDataDirs || string.IsNullOrWhiteSpace(launchPath);
    }

    private WindowInfo? TryFindSlotOwnedWindow(WindowSlot slot, AppConfig config, string? launchPath)
    {
        if (!config.UseDedicatedUserDataDirs
            || !TryReadSlotLockProcessId(slot, config, out var processId)
            || !IsProcessAlive(processId))
        {
            return null;
        }

        var windows = _windowEnumerator
            .GetVsCodeWindows()
            .Where(window => window.ProcessId == processId)
            .ToList();
        if (windows.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(launchPath))
        {
            var matchingWindow = windows.FirstOrDefault(window =>
                VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(window.Title, launchPath));
            if (matchingWindow is not null)
            {
                return matchingWindow;
            }

            DiagnosticLog.Write(
                $"Ignoring slot-owned VS Code window for slot {slot.Name} because it does not match launch path: {launchPath}");
            return null;
        }

        return windows
            .OrderBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool TryReadSlotLockProcessId(WindowSlot slot, AppConfig config, out uint processId)
    {
        processId = 0;
        var lockFile = Path.Combine(SlotUserDataPaths.GetUserDataDirectory(slot, config), "code.lock");
        if (!File.Exists(lockFile))
        {
            return false;
        }

        try
        {
            var lockContent = File.ReadAllText(lockFile).Trim();
            return uint.TryParse(lockContent, out processId) && processId > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessAlive(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return !process.HasExited;
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

    private async Task<HashSet<IntPtr>> GetKnownHandlesAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() => _windowEnumerator
                .GetVsCodeWindows()
                .Select(window => window.Handle)
                .ToHashSet(),
            cancellationToken);
    }

    private async Task<WindowInfo?> WaitForLaunchPathVisibleAsync(
        WindowInfo window,
        string launchPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(window.Title, launchPath))
        {
            return window;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var refreshedWindow = _windowEnumerator.TryGetWindow(window.Handle);
            if (refreshedWindow is null)
            {
                return null;
            }

            if (VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(refreshedWindow.Title, launchPath))
            {
                return refreshedWindow;
            }

            var remainingTime = GetRemainingTime(timeout, stopwatch);
            if (remainingTime <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remainingTime < RemoteWindowProbeInterval ? remainingTime : RemoteWindowProbeInterval, cancellationToken);
        }

        return null;
    }

    private static async Task PrepareDedicatedUserDataAsync(
        WindowSlot slot,
        AppConfig config,
        string resolvedCodeCommand,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalPriority = Thread.CurrentThread.Priority;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                SlotUserDataPaths.PrepareDedicatedUserData(slot, config, resolvedCodeCommand);
            }
            finally
            {
                Thread.CurrentThread.Priority = originalPriority;
            }
        }, cancellationToken);
    }

    // .exe 以外（code.cmd 等）は cmd.exe 経由で起動するため、Process.Start が返す PID は
    // VS Code 本体ではなく cmd のものになる。PID 依存の待受を避ける判定に使う。
    private static bool IsWrapperLaunch(string codeCommand)
    {
        return !string.Equals(Path.GetExtension(codeCommand), ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static uint? StartCode(string codeCommand, WindowSlot slot, AppConfig config, string? launchPath)
    {
        var canUseArgumentList = string.Equals(Path.GetExtension(codeCommand), ".exe", StringComparison.OrdinalIgnoreCase);
        var startInfo = canUseArgumentList
            ? CreateExecutableStartInfo(codeCommand, slot, config, launchPath)
            : CreateWrapperStartInfo(codeCommand, slot, config, launchPath);

        using var process = Process.Start(startInfo);
        return process is null ? null : (uint)process.Id;
    }

    private static ProcessStartInfo CreateExecutableStartInfo(
        string codeCommand,
        WindowSlot slot,
        AppConfig config,
        string? launchPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = codeCommand,
            UseShellExecute = false
        };

        AddLaunchArguments(startInfo.ArgumentList, slot, config, launchPath);
        VscodeUserSettings.ApplyManagedProxyEnvironment(startInfo, config);
        return startInfo;
    }

    private static ProcessStartInfo CreateWrapperStartInfo(
        string codeCommand,
        WindowSlot slot,
        AppConfig config,
        string? launchPath)
    {
        var wrapperArguments = GetLaunchArguments(slot, config, launchPath);
        var wrappedCommand = string.IsNullOrWhiteSpace(wrapperArguments)
            ? QuoteForCommandShell(codeCommand)
            : $"{QuoteForCommandShell(codeCommand)} {wrapperArguments}";

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /s /c \"{wrappedCommand}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        VscodeUserSettings.ApplyManagedProxyEnvironment(startInfo, config);
        return startInfo;
    }

    private void KillZombieProcess(WindowSlot slot, AppConfig config)
    {
        var userDataDir = SlotUserDataPaths.GetUserDataDirectory(slot, config);
        var lockFile = Path.Combine(userDataDir, "code.lock");

        if (!File.Exists(lockFile))
        {
            return;
        }

        string lockContent;
        try
        {
            lockContent = File.ReadAllText(lockFile).Trim();
        }
        catch
        {
            return;
        }

        if (!int.TryParse(lockContent, out var pid))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                // The lock outlived its owner; clear it so the next launch is not steered toward a dead PID.
                TryDeleteStaleLock(lockFile, slot, pid, "process already exited");
                return;
            }

            // Guard against PID recycling: only ever terminate a lock holder that is genuinely a VS Code process.
            // If the original VS Code died and Windows reused its PID for something else, the lock is stale and
            // the unrelated process must not be killed.
            if (!IsVsCodeProcess(process))
            {
                TryDeleteStaleLock(lockFile, slot, pid, $"lock PID reused by unrelated process '{SafeProcessName(process) ?? "unknown"}'");
                return;
            }

            var liveWindows = _windowEnumerator.GetVsCodeWindows();
            var hasWindow = liveWindows.Any(w => w.ProcessId == pid);
            if (hasWindow)
            {
                return;
            }

            // 「lock は握っているがウィンドウが無い」状態は、本物のゾンビだけでなく、
            // 起動直後でまだメインウィンドウを描画していない正常なプロセスでも起こる。
            // 重い環境（例: Antigravity と同時起動）では初期化に時間がかかり、その隙に
            // 起動したばかりの VS Code を誤って kill すると、lock だけが残って次回以降の
            // 起動が連鎖的に壊れる。起動から十分時間が経ったプロセスだけを真のゾンビと見なす。
            var processAge = GetProcessAge(process);
            if (processAge is { } age && age < ZombieGracePeriod)
            {
                DiagnosticLog.Write(
                    $"Skipping zombie kill for VS Code process {pid} (slot {slot.Name}): "
                    + $"started {age.TotalSeconds:0.0}s ago, still within {ZombieGracePeriod.TotalSeconds:0}s grace period (likely still starting up).");
                return;
            }

            DiagnosticLog.Write(
                $"Killing zombie VS Code process {pid} for slot {slot.Name} "
                + $"(lock held but no window; age={(processAge is { } a ? $"{a.TotalSeconds:0.0}s" : "unknown")}).");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // Process no longer exists; the lock is stale.
            TryDeleteStaleLock(lockFile, slot, pid, "process no longer exists");
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            DiagnosticLog.Write(LogLevel.Warn, $"Failed to kill zombie process {pid}: {ex.Message}");
        }
    }

    // プロセスの起動からの経過時間。StartTime が取得できない（権限不足等）場合は null。
    private static TimeSpan? GetProcessAge(Process process)
    {
        try
        {
            return DateTime.Now - process.StartTime;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool IsVsCodeProcess(Process process)
    {
        var name = SafeProcessName(process);
        return name is not null
            && VsCodeProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static string? SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void TryDeleteStaleLock(string lockFile, WindowSlot slot, int pid, string reason)
    {
        try
        {
            File.Delete(lockFile);
            DiagnosticLog.Write($"Removed stale code.lock for slot {slot.Name} (pid {pid}: {reason}).");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(LogLevel.Warn, $"Failed to remove stale code.lock for slot {slot.Name}: {ex.Message}");
        }
    }

    private static void AddLaunchArguments(Collection<string> arguments, WindowSlot slot, AppConfig config, string? launchPath)
    {
        if (config.UseDedicatedUserDataDirs)
        {
            arguments.Add("--user-data-dir");
            arguments.Add(SlotUserDataPaths.GetUserDataDirectory(slot, config));
        }

        arguments.Add("--new-window");
        foreach (var argument in GetLaunchPathArguments(launchPath))
        {
            arguments.Add(argument);
        }
    }

    private static string GetLaunchArguments(WindowSlot slot, AppConfig config, string? launchPath)
    {
        var arguments = new List<string>();
        if (config.UseDedicatedUserDataDirs)
        {
            arguments.Add("--user-data-dir");
            arguments.Add(Quote(SlotUserDataPaths.GetUserDataDirectory(slot, config)));
        }

        arguments.Add("--new-window");
        foreach (var argument in GetLaunchPathArguments(launchPath))
        {
            arguments.Add(argument.StartsWith("--", StringComparison.Ordinal) ? argument : Quote(argument));
        }

        return string.Join(" ", arguments);
    }

    private static string? GetLaunchPath(WindowSlot slot, AppConfig config)
    {
        if (config.ReopenLastWorkspace
            && slot.SavedWorkspaceConfirmed
            && !string.IsNullOrWhiteSpace(slot.SavedWorkspacePath))
        {
            return slot.SavedWorkspacePath;
        }

        return slot.Path;
    }

    private static IEnumerable<string> GetLaunchPathArguments(string? launchPath)
    {
        if (string.IsNullOrWhiteSpace(launchPath))
        {
            yield break;
        }

        if (IsRemoteOrVirtualUri(launchPath))
        {
            yield return IsWorkspaceFileUri(launchPath) ? "--file-uri" : "--folder-uri";
            yield return launchPath;
            yield break;
        }

        yield return launchPath;
    }

    private static bool IsRemoteOrVirtualUri(string launchPath)
    {
        if (IsWindowsPath(launchPath) || launchPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        return TryParseNonFileUri(launchPath, out _);
    }

    private static bool IsWorkspaceFileUri(string launchPath)
    {
        var pathPart = Uri.TryCreate(launchPath, UriKind.Absolute, out var uri)
            ? Uri.UnescapeDataString(uri.AbsolutePath)
            : TryParseUriParts(launchPath, out var uriParts)
                ? Uri.UnescapeDataString(uriParts.AbsolutePath)
                : launchPath;

        return pathPart.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsPath(string value)
    {
        return value.Length >= 3
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/');
    }

    private static bool TryParseNonFileUri(string value, out UriParts uriParts)
    {
        uriParts = default;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri is not null
            && !string.IsNullOrWhiteSpace(uri.Scheme)
            && !uri.IsFile)
        {
            uriParts = new UriParts(uri.Scheme, uri.Authority, uri.AbsolutePath, uri.AbsoluteUri);
            return true;
        }

        return TryParseUriParts(value, out uriParts)
            && !string.Equals(uriParts.Scheme, "file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseUriParts(string value, out UriParts uriParts)
    {
        uriParts = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var schemeSeparatorIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparatorIndex <= 0)
        {
            return false;
        }

        var scheme = value[..schemeSeparatorIndex];
        if (!IsValidUriScheme(scheme))
        {
            return false;
        }

        var remainder = value[(schemeSeparatorIndex + 3)..];
        var pathIndex = remainder.IndexOf('/');
        var authority = pathIndex >= 0 ? remainder[..pathIndex] : remainder;
        var absolutePath = pathIndex >= 0 ? remainder[pathIndex..] : "/";
        uriParts = new UriParts(scheme, authority, absolutePath, value);
        return true;
    }

    private static bool IsValidUriScheme(string scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme) || !char.IsLetter(scheme[0]))
        {
            return false;
        }

        for (var index = 1; index < scheme.Length; index++)
        {
            var character = scheme[index];
            if (!char.IsLetterOrDigit(character)
                && character != '+'
                && character != '-'
                && character != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static string GetCodeCommandForLaunch(string configuredCodeCommand, string resolvedCodeCommand, string? launchPath)
    {
        if (string.IsNullOrWhiteSpace(launchPath) || !IsRemoteOrVirtualUri(launchPath))
        {
            return resolvedCodeCommand;
        }

        return ResolveVsCodeCliCommand(configuredCodeCommand, resolvedCodeCommand) ?? resolvedCodeCommand;
    }

    private static string? ResolveVsCodeCliCommand(string configuredCodeCommand, string resolvedCodeCommand)
    {
        var normalizedConfigured = string.IsNullOrWhiteSpace(configuredCodeCommand)
            ? "code"
            : configuredCodeCommand.Trim().Trim('"');

        if (File.Exists(normalizedConfigured) && IsVsCodeWrapperScript(normalizedConfigured))
        {
            return normalizedConfigured;
        }

        if (IsVsCodeCliAlias(normalizedConfigured))
        {
            foreach (var pathCandidate in GetPathCandidates(normalizedConfigured))
            {
                if (File.Exists(pathCandidate) && IsVsCodeWrapperScript(pathCandidate))
                {
                    return pathCandidate;
                }
            }

            foreach (var wellKnownPath in GetWellKnownCodePaths(normalizedConfigured))
            {
                if (File.Exists(wellKnownPath) && IsVsCodeWrapperScript(wellKnownPath))
                {
                    return wellKnownPath;
                }
            }
        }

        return TryResolveVsCodeCliWrapper(resolvedCodeCommand);
    }

    private static string? TryResolveVsCodeCliWrapper(string commandPath)
    {
        if (string.IsNullOrWhiteSpace(commandPath))
        {
            return null;
        }

        if (IsVsCodeWrapperScript(commandPath))
        {
            return commandPath;
        }

        var wrapperName = GetPreferredWrapperName(commandPath);
        if (string.IsNullOrWhiteSpace(wrapperName))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(commandPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var wrapperPath = Path.Combine(directory, "bin", wrapperName);
        return File.Exists(wrapperPath) ? wrapperPath : null;
    }

    private static bool ShouldAttemptRemoteFallback(AppConfig config, string? launchPath)
    {
        return config.UseDedicatedUserDataDirs
            && !string.IsNullOrWhiteSpace(launchPath)
            && IsRemoteOrVirtualUri(launchPath);
    }

    private static TimeSpan GetRemoteReconnectTimeout(AppConfig config, TimeSpan totalTimeout)
    {
        var reconnectTimeout = TimeSpan.FromSeconds(config.RemoteReconnectTimeoutSeconds);
        if (reconnectTimeout >= totalTimeout)
        {
            return totalTimeout > TimeSpan.FromSeconds(1)
                ? totalTimeout - TimeSpan.FromSeconds(1)
                : totalTimeout;
        }

        return reconnectTimeout;
    }

    private static TimeSpan GetRemainingTime(TimeSpan budget, Stopwatch stopwatch)
    {
        var remaining = budget - stopwatch.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static void TryTerminateLaunchProcess(uint? processId, string slotName)
    {
        if (!processId.HasValue)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId.Value);
            if (process.HasExited)
            {
                return;
            }

            DiagnosticLog.Write($"Killing failed VS Code launch {processId.Value} for slot {slotName} before fallback.");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // Process no longer exists
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            DiagnosticLog.Write(LogLevel.Warn, $"Failed to kill launch process {processId.Value} for slot {slotName}: {ex.Message}");
        }
    }

    private static string? ResolveCodeCommand(string codeCommand)
    {
        var normalized = string.IsNullOrWhiteSpace(codeCommand)
            ? "code"
            : codeCommand.Trim().Trim('"');
        if (File.Exists(normalized))
        {
            return ResolveVsCodeExecutable(normalized);
        }

        if (IsVsCodeCliAlias(normalized))
        {
            foreach (var wellKnownPath in GetWellKnownCodePaths(normalized))
            {
                if (File.Exists(wellKnownPath))
                {
                    return wellKnownPath;
                }
            }
        }

        foreach (var pathCandidate in GetPathCandidates(normalized))
        {
            if (File.Exists(pathCandidate))
            {
                return ResolveVsCodeExecutable(pathCandidate);
            }
        }

        foreach (var wellKnownPath in GetWellKnownCodePaths(normalized))
        {
            if (File.Exists(wellKnownPath))
            {
                return wellKnownPath;
            }
        }

        return null;
    }

    private static string ResolveVsCodeExecutable(string commandPath)
    {
        if (!IsVsCodeWrapperScript(commandPath))
        {
            return commandPath;
        }

        var executableName = GetPreferredExecutableName(commandPath);
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return commandPath;
        }

        foreach (var directory in GetWrapperParentDirectories(commandPath))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return commandPath;
    }

    private static bool IsVsCodeWrapperScript(string commandPath)
    {
        var extension = Path.GetExtension(commandPath);
        if (!string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(commandPath);
        return fileName.Equals("code.cmd", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("code.bat", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("code-insiders.cmd", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("code-insiders.bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetPreferredExecutableName(string commandPath)
    {
        var fileName = Path.GetFileName(commandPath);
        if (fileName.Equals("code.cmd", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("code.bat", StringComparison.OrdinalIgnoreCase))
        {
            return "Code.exe";
        }

        if (fileName.Equals("code-insiders.cmd", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("code-insiders.bat", StringComparison.OrdinalIgnoreCase))
        {
            return "Code - Insiders.exe";
        }

        return null;
    }

    private static string? GetPreferredWrapperName(string commandPath)
    {
        var fileName = Path.GetFileName(commandPath);
        if (fileName.Equals("Code.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "code.cmd";
        }

        if (fileName.Equals("Code - Insiders.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "code-insiders.cmd";
        }

        return null;
    }

    private static IEnumerable<string> GetWrapperParentDirectories(string commandPath)
    {
        var currentDirectory = Path.GetDirectoryName(commandPath);
        if (string.IsNullOrWhiteSpace(currentDirectory))
        {
            yield break;
        }

        yield return currentDirectory;

        var parentDirectory = Directory.GetParent(currentDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            yield return parentDirectory;
        }
    }

    private static bool IsVsCodeCliAlias(string command)
    {
        var commandName = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
        return commandName is "code" or "code-insiders" or "code - insiders";
    }

    private static IEnumerable<string> GetPathCandidates(string command)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IEnumerable<string> names = Path.HasExtension(command)
            ? new[] { command }
            : new[] { command }.Concat(extensions.Select(extension => command + extension));

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                yield return Path.Combine(directory, name);
            }
        }
    }

    private static IEnumerable<string> GetWellKnownCodePaths(string command)
    {
        var commandName = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
        var wantsStable = commandName is "code";
        var wantsInsiders = commandName is "code-insiders" or "code - insiders";

        if (!wantsStable && !wantsInsiders)
        {
            yield break;
        }

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            if (wantsStable)
            {
                yield return Path.Combine(root, "Programs", "Microsoft VS Code", "Code.exe");
                yield return Path.Combine(root, "Programs", "Microsoft VS Code", "bin", "code.cmd");
                yield return Path.Combine(root, "Microsoft VS Code", "Code.exe");
                yield return Path.Combine(root, "Microsoft VS Code", "bin", "code.cmd");
            }

            if (wantsInsiders)
            {
                yield return Path.Combine(root, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe");
                yield return Path.Combine(root, "Programs", "Microsoft VS Code Insiders", "bin", "code-insiders.cmd");
                yield return Path.Combine(root, "Microsoft VS Code Insiders", "Code - Insiders.exe");
                yield return Path.Combine(root, "Microsoft VS Code Insiders", "bin", "code-insiders.cmd");
            }
        }
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string QuoteForCommandShell(string value)
    {
        return $"\"{value}\"";
    }

    private delegate void WinEventDelegate(
        IntPtr winEventHook,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookAssembly,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr winEventHook);

    private readonly record struct UriParts(string Scheme, string Authority, string AbsolutePath, string AbsoluteUri);
}

public sealed record WindowAssignment(WindowSlot Slot, WindowInfo Window, bool WasCloakedForLaunch = false);
