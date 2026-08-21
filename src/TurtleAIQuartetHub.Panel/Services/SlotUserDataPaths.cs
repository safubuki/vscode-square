using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public static class SlotUserDataPaths
{
    private static readonly JsonDocumentOptions SettingsParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions SettingsWriteOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string[] SharedUserFiles =
    [
        "settings.json",
        "keybindings.json",
        "chatLanguageModels.json",
        "mcp.json"
    ];

    private static readonly string[] SharedUserDirectories =
    [
        "snippets",
        "prompts"
    ];

    // 消してよいのは再生成できるキャッシュだけ。
    // 残すもの: User（設定・キーバインド・globalStorage・workspaceStorage）、
    // WebStorage / Partitions / Local Storage / Session Storage（サインインとチャット履歴）。
    private static readonly string[] RegenerableCacheDirectoryNames =
    [
        "Cache",
        "CachedData",
        "CachedExtensions",
        "CachedExtensionVSIXs",
        "CachedProfilesData",
        "Code Cache",
        "GPUCache",
        "DawnGraphiteCache",
        "DawnWebGPUCache",
        "Crashpad",
        "logs",
        "VideoDecodeStats",
        "blob_storage",
        "clp",
        "agent-host",
        "copilot-terminal-output"
    ];

    private static readonly string[] VsCodeProcessNames = ["Code", "Code - Insiders", "VSCodium", "Codium"];

    // クラッシュダンプは VS Code 実行中でも消してよい。
    private static readonly string[] AlwaysSafeCacheDirectoryNames = ["Crashpad"];

    private static readonly string[] SharedRootFiles =
    [
        "machineid",
        "languagepacks.json",
        "Local State",
        "Preferences"
    ];

    // Chromium/Electron storage trees are intentionally excluded to avoid heavy startup copies on lower-spec PCs.
    private static readonly string[] SharedRootDirectories = [];

    public static string GetUserDataDirectory(WindowSlot slot, AppConfig config)
    {
        return GetUserDataDirectory(slot.RuntimeSlotName, config);
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
        return GetEffectiveUserDataDirectory(slot.RuntimeSlotName, config);
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

        if (config.InheritMainUserState)
        {
            var sourceDirectory = GetInstalledUserDataDirectory(codeCommand);
            if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
            {
                try
                {
                    SyncSharedState(sourceDirectory, targetDirectory);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(ex);
                }
            }
        }

        try
        {
            PruneRegenerableCaches(targetDirectory);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        // 各スロット専用プロファイルでは前回セッションのウィンドウ復元を必ず無効化する。
        // これを行わないと VS Code が --new-window で開くウィンドウに加えて前回開いていた
        // ウィンドウまで復元してしまい、余分なウィンドウが開いて 2x2 配置が崩れる。
        // ※ SyncSharedState の後に適用することで、設定の再コピー後も上書きを維持する。
        try
        {
            EnsureLauncherManagedSettings(targetDirectory);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    public static long ReclaimUnusedUserData(AppConfig config)
    {
        var reclaimed = 0L;
        var slotUserDataRoot = Path.Combine(config.StateDirectory, "user-data");

        // 共有プロファイル運用ではスロット別 user-data は使わない。過去の専用プロファイルが
        // 残っていると Cache / WebStorage だけで数 GB になるため、起動時に回収する。
        if (!config.UseDedicatedUserDataDirs && Directory.Exists(slotUserDataRoot))
        {
            reclaimed += TryDeleteDirectoryBestEffort(slotUserDataRoot);
        }

        var installedDirectory = GetInstalledUserDataDirectory(config.CodeCommand);
        if (!string.IsNullOrWhiteSpace(installedDirectory))
        {
            reclaimed += PruneCacheDirectories(installedDirectory, AlwaysSafeCacheDirectoryNames);

            if (IsAnyVsCodeProcessAlive())
            {
                DiagnosticLog.Write("Skipped heavy VS Code cache prune: a VS Code process is running.");
            }
            else
            {
                reclaimed += PruneCacheDirectories(installedDirectory, RegenerableCacheDirectoryNames);
            }
        }

        if (config.UseDedicatedUserDataDirs && Directory.Exists(slotUserDataRoot))
        {
            foreach (var slotDirectory in Directory.EnumerateDirectories(slotUserDataRoot))
            {
                reclaimed += PruneRegenerableCaches(slotDirectory);
            }
        }

        return reclaimed;
    }

    private static void EnsureLauncherManagedSettings(string targetDirectory)
    {
        var userDirectory = Path.Combine(targetDirectory, "User");
        Directory.CreateDirectory(userDirectory);
        var settingsPath = Path.Combine(userDirectory, "settings.json");

        JsonObject root;
        var settingsExist = File.Exists(settingsPath);
        if (settingsExist)
        {
            var text = File.ReadAllText(settingsPath);
            root = string.IsNullOrWhiteSpace(text)
                ? new JsonObject()
                : JsonNode.Parse(text, documentOptions: SettingsParseOptions) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var changed = SetStringSetting(root, "window.restoreWindows", "none");
        if (!changed && settingsExist)
        {
            return;
        }

        File.WriteAllText(settingsPath, root.ToJsonString(SettingsWriteOptions));
    }

    private static bool SetStringSetting(JsonObject root, string key, string value)
    {
        if (root[key] is JsonValue existing
            && existing.TryGetValue<string>(out var current)
            && string.Equals(current, value, StringComparison.Ordinal))
        {
            return false;
        }

        root[key] = value;
        return true;
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

    private static long PruneRegenerableCaches(string userDataDirectory)
    {
        return PruneCacheDirectories(userDataDirectory, RegenerableCacheDirectoryNames);
    }

    private static long PruneCacheDirectories(string userDataDirectory, IReadOnlyList<string> directoryNames)
    {
        if (!Directory.Exists(userDataDirectory))
        {
            return 0;
        }

        var reclaimed = 0L;
        foreach (var directoryName in directoryNames)
        {
            reclaimed += TryDeleteDirectoryBestEffort(Path.Combine(userDataDirectory, directoryName));
        }

        return reclaimed;
    }

    private static bool IsAnyVsCodeProcessAlive()
    {
        foreach (var processName in VsCodeProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                var count = processes.Length;
                foreach (var process in processes)
                {
                    process.Dispose();
                }

                if (count > 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
                return true;
            }
        }

        return false;
    }

    private static long TryDeleteDirectoryBestEffort(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        var reclaimed = 0L;
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                reclaimed += TryDeleteFile(filePath);
            }

            foreach (var childDirectory in Directory.EnumerateDirectories(directoryPath))
            {
                reclaimed += TryDeleteDirectoryBestEffort(childDirectory);
            }

            Directory.Delete(directoryPath, false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        return reclaimed;
    }

    private static long TryDeleteFile(string filePath)
    {
        try
        {
            var length = new FileInfo(filePath).Length;
            File.Delete(filePath);
            return length;
        }
        catch
        {
            // ロック中のキャッシュファイルは次回の回収に回す。大量の個別失敗で panel.log を埋めない。
            return 0;
        }
    }
}
