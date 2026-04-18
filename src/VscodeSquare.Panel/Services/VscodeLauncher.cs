using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using VscodeSquare.Panel.Models;

namespace VscodeSquare.Panel.Services;

public sealed class VscodeLauncher
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectNameChange = 0x800C;
    private const int ObjectIdWindow = 0;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
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

            if (config.UseDedicatedUserDataDirs)
            {
                await PrepareDedicatedUserDataAsync(slot, config, resolvedCodeCommand, cancellationToken);
            }

            DiagnosticLog.Write($"Starting VS Code for slot {slot.Name}: {resolvedCodeCommand} {GetLaunchArguments(slot, config)}");
            await Task.Run(() => StartCode(resolvedCodeCommand, slot, config), cancellationToken);

            var window = await WaitForNewWindowAsync(knownHandles, timeout, cancellationToken);
            if (window is null)
            {
                DiagnosticLog.Write($"No new VS Code window detected for slot {slot.Name} within {timeout.TotalSeconds:0} seconds.");
                slot.WindowStatus = SlotWindowStatus.Missing;
                break;
            }

            knownHandles.Add(window.Handle);
            assignments.Add(new WindowAssignment(slot, window));

        }

        foreach (var pendingSlot in launchTargets.Skip(assignments.Count))
        {
            if (pendingSlot.WindowHandle == IntPtr.Zero)
            {
                pendingSlot.WindowStatus = SlotWindowStatus.Missing;
            }
        }

        return assignments;
    }

    private async Task<WindowInfo?> WaitForNewWindowAsync(
        HashSet<IntPtr> knownHandles,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var existingWindow = FindNewWindow(knownHandles);
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
            if (window is not null && !knownHandles.Contains(window.Handle))
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
            existingWindow = FindNewWindow(knownHandles);
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

    private WindowInfo? FindNewWindow(HashSet<IntPtr> knownHandles)
    {
        return _windowEnumerator
            .GetVsCodeWindows()
            .Where(item => !knownHandles.Contains(item.Handle))
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

    private static void StartCode(string codeCommand, WindowSlot slot, AppConfig config)
    {
        var canUseArgumentList = string.Equals(Path.GetExtension(codeCommand), ".exe", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = codeCommand,
            UseShellExecute = !canUseArgumentList
        };

        if (canUseArgumentList)
        {
            AddLaunchArguments(startInfo.ArgumentList, slot, config);
        }
        else
        {
            startInfo.Arguments = GetLaunchArguments(slot, config);
        }

        Process.Start(startInfo);
    }

    private static void AddLaunchArguments(Collection<string> arguments, WindowSlot slot, AppConfig config)
    {
        if (config.UseDedicatedUserDataDirs)
        {
            arguments.Add("--user-data-dir");
            arguments.Add(SlotUserDataPaths.GetUserDataDirectory(slot, config));
        }

        arguments.Add("--new-window");
        foreach (var argument in GetLaunchPathArguments(GetLaunchPath(slot, config)))
        {
            arguments.Add(argument);
        }
    }

    private static string GetLaunchArguments(WindowSlot slot, AppConfig config)
    {
        var arguments = new List<string>();
        if (config.UseDedicatedUserDataDirs)
        {
            arguments.Add("--user-data-dir");
            arguments.Add(Quote(SlotUserDataPaths.GetUserDataDirectory(slot, config)));
        }

        arguments.Add("--new-window");
        foreach (var argument in GetLaunchPathArguments(GetLaunchPath(slot, config)))
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

        return Uri.TryCreate(launchPath, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Scheme)
            && !uri.IsFile;
    }

    private static bool IsWorkspaceFileUri(string launchPath)
    {
        var pathPart = Uri.TryCreate(launchPath, UriKind.Absolute, out var uri)
            ? Uri.UnescapeDataString(uri.AbsolutePath)
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
}

public sealed record WindowAssignment(WindowSlot Slot, WindowInfo Window);
