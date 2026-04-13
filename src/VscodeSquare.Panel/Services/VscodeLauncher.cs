using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using VscodeSquare.Panel.Models;

namespace VscodeSquare.Panel.Services;

public sealed class VscodeLauncher
{
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

        var beforeHandles = _windowEnumerator
            .GetVsCodeWindows()
            .Select(window => window.Handle)
            .ToHashSet();

        var resolvedCodeCommand = ResolveCodeCommand(config.CodeCommand) ?? config.CodeCommand;

        foreach (var slot in launchTargets)
        {
            DiagnosticLog.Write($"Starting VS Code for slot {slot.Name}: {resolvedCodeCommand} {GetLaunchArguments(slot, config)}");
            StartCode(resolvedCodeCommand, slot, config);
            await Task.Delay(350, cancellationToken);
        }

        var windows = await WaitForNewWindowsAsync(
            beforeHandles,
            launchTargets.Count,
            TimeSpan.FromSeconds(config.LaunchTimeoutSeconds),
            cancellationToken);

        return launchTargets
            .Zip(windows, (slot, window) => new WindowAssignment(slot, window))
            .ToList();
    }

    private async Task<IReadOnlyList<WindowInfo>> WaitForNewWindowsAsync(
        HashSet<IntPtr> beforeHandles,
        int expectedCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var windows = new List<WindowInfo>();

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            windows = _windowEnumerator
                .GetVsCodeWindows()
                .Where(window => !beforeHandles.Contains(window.Handle))
                .OrderBy(window => window.ProcessId)
                .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (windows.Count >= expectedCount)
            {
                break;
            }

            await Task.Delay(500, cancellationToken);
        }

        return windows.Take(expectedCount).ToList();
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
            arguments.Add(GetUserDataDirectory(slot, config));
        }

        arguments.Add("--new-window");
        var launchPath = GetLaunchPath(slot, config);
        if (!string.IsNullOrWhiteSpace(launchPath))
        {
            arguments.Add(launchPath);
        }
    }

    private static string GetLaunchArguments(WindowSlot slot, AppConfig config)
    {
        var arguments = new List<string>();
        if (config.UseDedicatedUserDataDirs)
        {
            arguments.Add("--user-data-dir");
            arguments.Add(Quote(GetUserDataDirectory(slot, config)));
        }

        arguments.Add("--new-window");
        var launchPath = GetLaunchPath(slot, config);
        if (!string.IsNullOrWhiteSpace(launchPath))
        {
            arguments.Add(Quote(launchPath));
        }

        return string.Join(" ", arguments);
    }

    private static string GetUserDataDirectory(WindowSlot slot, AppConfig config)
    {
        var safeSlotName = new string(slot.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        if (string.IsNullOrWhiteSpace(safeSlotName))
        {
            safeSlotName = "slot";
        }

        return Path.Combine(config.StateDirectory, "user-data", safeSlotName);
    }

    private static string? GetLaunchPath(WindowSlot slot, AppConfig config)
    {
        if (!config.ReopenLastWorkspace)
        {
            return slot.Path;
        }

        var userDataDirectory = GetUserDataDirectory(slot, config);
        if (Directory.Exists(userDataDirectory) && Directory.EnumerateFileSystemEntries(userDataDirectory).Any())
        {
            return null;
        }

        return slot.Path;
    }

    private static string? ResolveCodeCommand(string codeCommand)
    {
        var normalized = string.IsNullOrWhiteSpace(codeCommand)
            ? "code"
            : codeCommand.Trim().Trim('"');
        if (File.Exists(normalized))
        {
            return normalized;
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
                return pathCandidate;
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
                yield return Path.Combine(root, "Programs", "Microsoft VS Code", "bin", "code.cmd");
                yield return Path.Combine(root, "Programs", "Microsoft VS Code", "Code.exe");
                yield return Path.Combine(root, "Microsoft VS Code", "bin", "code.cmd");
                yield return Path.Combine(root, "Microsoft VS Code", "Code.exe");
            }

            if (wantsInsiders)
            {
                yield return Path.Combine(root, "Programs", "Microsoft VS Code Insiders", "bin", "code-insiders.cmd");
                yield return Path.Combine(root, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe");
                yield return Path.Combine(root, "Microsoft VS Code Insiders", "bin", "code-insiders.cmd");
                yield return Path.Combine(root, "Microsoft VS Code Insiders", "Code - Insiders.exe");
            }
        }
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}

public sealed record WindowAssignment(WindowSlot Slot, WindowInfo Window);
