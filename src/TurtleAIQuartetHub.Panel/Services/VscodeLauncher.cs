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

            VscodeLayoutState.TryApplyPreferredLayout(slot, config, slot.PreferredLayout);

            var launchPath = GetLaunchPath(slot, config);
            var assignment = await LaunchWindowAsync(slot, config, resolvedCodeCommand, launchPath, knownHandles, timeout, cancellationToken);
            if (assignment is null)
            {
                DiagnosticLog.Write($"No new VS Code window detected for slot {slot.Name} within {timeout.TotalSeconds:0} seconds.");
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
            knownHandles,
            timeout,
            CanTrackLaunchedProcess(launchCodeCommand) ? launchedProcessId : null,
            cancellationToken);
        return window is null ? null : new WindowAssignment(slot, window);
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
        var trackedProcessId = CanTrackLaunchedProcess(codeCommand) ? launchedProcessId : null;

        var reconnectStopwatch = Stopwatch.StartNew();
        var remoteWindow = await WaitForNewWindowAsync(knownHandles, reconnectTimeout, trackedProcessId, cancellationToken);
        if (remoteWindow is not null)
        {
            var remainingReconnectTime = GetRemainingTime(reconnectTimeout, reconnectStopwatch);
            remoteWindow = await WaitForLaunchPathVisibleAsync(remoteWindow, launchPath, remainingReconnectTime, cancellationToken);
        }

        if (remoteWindow is not null)
        {
            return new WindowAssignment(slot, remoteWindow);
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
            DiagnosticLog.Write($"No timeout budget remains for slot {slot.Name} fallback launch.");
            return null;
        }

        DiagnosticLog.Write($"Starting fallback VS Code window for slot {slot.Name}: {codeCommand} {GetLaunchArguments(slot, config, null)}");
        var fallbackProcessId = await Task.Run(() => StartCode(codeCommand, slot, config, null), cancellationToken);
        var fallbackWindow = await WaitForNewWindowAsync(knownHandles, fallbackTimeout, fallbackProcessId, cancellationToken);
        return fallbackWindow is null ? null : new WindowAssignment(slot, fallbackWindow);
    }

    private async Task<WindowInfo?> WaitForNewWindowAsync(
        HashSet<IntPtr> knownHandles,
        TimeSpan timeout,
        uint? expectedProcessId,
        CancellationToken cancellationToken)
    {
        var existingWindow = FindNewWindow(knownHandles, expectedProcessId);
        if (existingWindow is not null)
        {
            return existingWindow;
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
                && !knownHandles.Contains(window.Handle)
                && (!expectedProcessId.HasValue || window.ProcessId == expectedProcessId.Value))
            {
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
            DiagnosticLog.Write("WinEvent hook could not be registered while waiting for a VS Code window.");
            return FindNewWindow(knownHandles);
        }

        try
        {
            existingWindow = FindNewWindow(knownHandles, expectedProcessId);
            if (existingWindow is not null)
            {
                return existingWindow;
            }

            using var timeoutRegistration = timeoutCts.Token.Register(() => completionSource.TrySetResult(null));
            using var cancellationRegistration = cancellationToken.Register(() => completionSource.TrySetCanceled(cancellationToken));
            return await completionSource.Task;
        }
        finally
        {
            UnhookWinEvent(hook);
            GC.KeepAlive(callback);
        }
    }

    private WindowInfo? FindNewWindow(HashSet<IntPtr> knownHandles, uint? expectedProcessId = null)
    {
        return _windowEnumerator
            .GetVsCodeWindows()
            .Where(item => !knownHandles.Contains(item.Handle))
            .Where(item => !expectedProcessId.HasValue || item.ProcessId == expectedProcessId.Value)
            .OrderBy(window => window.ProcessId)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
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

        return new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /s /c \"{wrappedCommand}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
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
                return;
            }

            var liveWindows = _windowEnumerator.GetVsCodeWindows();
            var hasWindow = liveWindows.Any(w => w.ProcessId == pid);
            if (hasWindow)
            {
                return;
            }

            DiagnosticLog.Write($"Killing zombie VS Code process {pid} for slot {slot.Name} (lock held but no window).");
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
            DiagnosticLog.Write($"Failed to kill zombie process {pid}: {ex.Message}");
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

    private static bool CanTrackLaunchedProcess(string codeCommand)
    {
        return string.Equals(Path.GetExtension(codeCommand), ".exe", StringComparison.OrdinalIgnoreCase)
            && !IsVsCodeWrapperScript(codeCommand);
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
            DiagnosticLog.Write($"Failed to kill launch process {processId.Value} for slot {slotName}: {ex.Message}");
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

public sealed record WindowAssignment(WindowSlot Slot, WindowInfo Window);
