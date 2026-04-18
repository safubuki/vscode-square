using System.IO;
using VscodeSquare.Panel.Models;

namespace VscodeSquare.Panel.Services;

public static class SlotUserDataPaths
{
    private static readonly string[] SharedUserFiles =
    [
        "settings.json",
        "keybindings.json",
        "chatLanguageModels.json",
        "mcp.json"
    ];

    private static readonly string[] SharedUserDirectories =
    [
        "globalStorage",
        "snippets",
        "prompts"
    ];

    private static readonly string[] SharedRootFiles =
    [
        "machineid",
        "languagepacks.json",
        "Local State",
        "Preferences",
        "SharedStorage",
        "SharedStorage-wal",
        "DIPS",
        "DIPS-wal"
    ];

    private static readonly string[] SharedRootDirectories =
    [
        "Local Storage",
        "Network",
        "Session Storage",
        "WebStorage",
        "Service Worker",
        "Partitions",
        "shared_proto_db",
        "Shared Dictionary"
    ];

    public static string GetUserDataDirectory(WindowSlot slot, AppConfig config)
    {
        return GetUserDataDirectory(slot.Name, config);
    }

    public static string GetUserDataDirectory(string slotName, AppConfig config)
    {
        var safeSlotName = new string(slotName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        if (string.IsNullOrWhiteSpace(safeSlotName))
        {
            safeSlotName = "slot";
        }

        return Path.Combine(config.StateDirectory, "user-data", safeSlotName);
    }

    public static string GetEffectiveUserDataDirectory(WindowSlot slot, AppConfig config)
    {
        return GetEffectiveUserDataDirectory(slot.Name, config);
    }

    public static string GetEffectiveUserDataDirectory(string slotName, AppConfig config)
    {
        if (config.UseDedicatedUserDataDirs)
        {
            return GetUserDataDirectory(slotName, config);
        }

        return GetInstalledUserDataDirectory(config.CodeCommand) ?? GetUserDataDirectory(slotName, config);
    }

    public static void PrepareDedicatedUserData(WindowSlot slot, AppConfig config, string codeCommand)
    {
        var targetDirectory = GetUserDataDirectory(slot, config);
        Directory.CreateDirectory(targetDirectory);

        if (!config.InheritMainUserState)
        {
            return;
        }

        var sourceDirectory = GetInstalledUserDataDirectory(codeCommand);
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return;
        }

        try
        {
            SyncSharedState(sourceDirectory, targetDirectory);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private static void SyncSharedState(string sourceDirectory, string targetDirectory)
    {
        foreach (var fileName in SharedRootFiles)
        {
            CopyFileIfNeeded(
                Path.Combine(sourceDirectory, fileName),
                Path.Combine(targetDirectory, fileName));
        }

        foreach (var directoryName in SharedRootDirectories)
        {
            CopyDirectoryIfNeeded(
                Path.Combine(sourceDirectory, directoryName),
                Path.Combine(targetDirectory, directoryName));
        }

        var sourceUserDirectory = Path.Combine(sourceDirectory, "User");
        var targetUserDirectory = Path.Combine(targetDirectory, "User");
        Directory.CreateDirectory(targetUserDirectory);

        foreach (var fileName in SharedUserFiles)
        {
            CopyFileIfNeeded(
                Path.Combine(sourceUserDirectory, fileName),
                Path.Combine(targetUserDirectory, fileName));
        }

        foreach (var directoryName in SharedUserDirectories)
        {
            CopyDirectoryIfNeeded(
                Path.Combine(sourceUserDirectory, directoryName),
                Path.Combine(targetUserDirectory, directoryName));
        }
    }

    private static string? GetInstalledUserDataDirectory(string codeCommand)
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData))
        {
            return null;
        }

        var commandName = Path.GetFileNameWithoutExtension(codeCommand);
        if (commandName.Contains("insiders", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(applicationData, "Code - Insiders");
        }

        if (commandName.Contains("code", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(applicationData, "Code");
        }

        return null;
    }

    private static void CopyDirectoryIfNeeded(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        if (DirectorySurfaceMatches(sourceDirectory, targetDirectory))
        {
            return;
        }

        Directory.CreateDirectory(targetDirectory);

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            CopyFileIfNeeded(filePath, Path.Combine(targetDirectory, Path.GetFileName(filePath)));
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectoryIfNeeded(directoryPath, Path.Combine(targetDirectory, Path.GetFileName(directoryPath)));
        }
    }

    private static bool DirectorySurfaceMatches(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(targetDirectory))
        {
            return false;
        }

        try
        {
            return EnumerateDirectorySurface(sourceDirectory)
                .SequenceEqual(EnumerateDirectorySurface(targetDirectory), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            return false;
        }
    }

    private static IEnumerable<string> EnumerateDirectorySurface(string directoryPath)
    {
        return new DirectoryInfo(directoryPath)
            .EnumerateFileSystemInfos()
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry switch
            {
                FileInfo fileInfo => $"F|{fileInfo.Name}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}",
                DirectoryInfo directoryInfo => $"D|{directoryInfo.Name}|{directoryInfo.LastWriteTimeUtc.Ticks}",
                _ => $"X|{entry.Name}|{entry.LastWriteTimeUtc.Ticks}"
            });
    }

    private static void CopyFileIfNeeded(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            var targetInfo = new FileInfo(targetPath);
            if (targetInfo.Exists
                && targetInfo.Length == sourceInfo.Length
                && targetInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }
}
