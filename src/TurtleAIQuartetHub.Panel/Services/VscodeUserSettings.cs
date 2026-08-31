using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public static class VscodeUserSettings
{
    public const string HttpProxyKey = "http.proxy";
    public const string HttpProxySupportKey = "http.proxySupport";
    public const string HttpNoProxyKey = "http.noProxy";
    public const string RestoreWindowsKey = "window.restoreWindows";
    public const string RestoreWindowsNone = "none";
    public const string ProxySupportOff = "off";
    public const string ProxySupportOverride = "override";

    private static readonly JsonDocumentOptions SettingsParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions SettingsWriteOptions = new()
    {
        WriteIndented = true
    };

    public readonly record struct NetworkSettings(
        bool UseHttpProxy,
        string HttpProxy,
        string HttpNoProxy)
    {
        public static NetworkSettings Open => new(false, string.Empty, string.Empty);

        public static NetworkSettings FromConfig(AppConfig config)
        {
            return new NetworkSettings(
                config.VsCodeUseHttpProxy,
                config.VsCodeHttpProxy ?? string.Empty,
                config.VsCodeHttpNoProxy ?? string.Empty);
        }

        public string? Validate()
        {
            if (UseHttpProxy && string.IsNullOrWhiteSpace(HttpProxy))
            {
                return "プロキシ環境にする場合はプロキシ URL を入力してください。";
            }

            return null;
        }
    }

    public readonly record struct ApplyResult(int ProfilesWritten, IReadOnlyList<string> Errors)
    {
        public bool Succeeded => Errors.Count == 0 && ProfilesWritten > 0;
    }

    public static IReadOnlyList<string> EnumerateTargetUserDataDirectories(AppConfig config)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installed = SlotUserDataPaths.GetInstalledUserDataDirectory(config);

        if (config.UseDedicatedUserDataDirs)
        {
            foreach (var slot in config.Slots)
            {
                directories.Add(SlotUserDataPaths.GetUserDataDirectory(slot.Name, config));
            }
        }

        // 共有プロファイルは 1 ファイル。専用プロファイルでも通常 VS Code とネットワーク設定を揃える。
        if (!string.IsNullOrWhiteSpace(installed))
        {
            directories.Add(installed);
        }

        if (directories.Count == 0)
        {
            directories.Add(SlotUserDataPaths.GetUserDataDirectory("A", config));
        }

        return directories.ToArray();
    }

    public static bool IsDedicatedUserDataDirectory(string userDataDirectory, AppConfig config)
    {
        if (!config.UseDedicatedUserDataDirs)
        {
            return false;
        }

        var dedicatedRoot = Path.Combine(config.StateDirectory, "user-data");
        return userDataDirectory.StartsWith(dedicatedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static ApplyResult ApplyNetworkSettings(AppConfig config, NetworkSettings settings)
    {
        var validationError = settings.Validate();
        if (validationError is not null)
        {
            return new ApplyResult(0, [validationError]);
        }

        var errors = new List<string>();
        var written = 0;

        foreach (var userDataDirectory in EnumerateTargetUserDataDirectories(config))
        {
            try
            {
                ApplyToUserDataDirectory(
                    userDataDirectory,
                    settings,
                    stampRestoreWindowsNone: IsDedicatedUserDataDirectory(userDataDirectory, config));
                written++;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
                errors.Add($"{userDataDirectory}: {ex.Message}");
            }
        }

        return new ApplyResult(written, errors);
    }

    public static void SynchronizeDedicatedSlotSettings(
        string targetUserDataDirectory,
        AppConfig config,
        string codeCommand)
    {
        var targetSettingsPath = GetSettingsPath(targetUserDataDirectory);
        var installedDirectory = SlotUserDataPaths.GetInstalledUserDataDirectory(codeCommand);
        JsonObject root;

        if (!string.IsNullOrWhiteSpace(installedDirectory))
        {
            var installedSettingsPath = GetSettingsPath(installedDirectory);
            root = ReadSettingsObject(installedSettingsPath);
        }
        else
        {
            root = ReadSettingsObject(targetSettingsPath);
        }

        SetStringSetting(root, RestoreWindowsKey, RestoreWindowsNone);
        if (config.ManageVsCodeUserSettings)
        {
            ApplyNetworkSettingsToRoot(root, NetworkSettings.FromConfig(config));
        }

        var output = root.ToJsonString(SettingsWriteOptions);
        if (File.Exists(targetSettingsPath)
            && string.Equals(File.ReadAllText(targetSettingsPath), output, StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetSettingsPath)!);
        File.WriteAllText(targetSettingsPath, output);
    }

    public static bool TryReadNetworkSettings(AppConfig config, out NetworkSettings settings)
    {
        settings = NetworkSettings.Open;
        foreach (var userDataDirectory in EnumerateTargetUserDataDirectories(config))
        {
            var settingsPath = GetSettingsPath(userDataDirectory);
            if (!File.Exists(settingsPath))
            {
                continue;
            }

            try
            {
                var root = ReadSettingsObject(settingsPath);
                settings = ReadNetworkSettings(root);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        }

        return false;
    }

    public static bool ApplyToUserDataDirectory(
        string userDataDirectory,
        NetworkSettings settings,
        bool stampRestoreWindowsNone)
    {
        var settingsPath = GetSettingsPath(userDataDirectory);
        var root = ReadSettingsObject(settingsPath);
        var changed = ApplyNetworkSettingsToRoot(root, settings);
        if (stampRestoreWindowsNone)
        {
            changed |= SetStringSetting(root, RestoreWindowsKey, RestoreWindowsNone);
        }

        if (!changed && File.Exists(settingsPath))
        {
            return false;
        }

        WriteSettingsObject(settingsPath, root);
        return true;
    }

    public static NetworkSettings ReadNetworkSettings(JsonObject root)
    {
        var proxy = ReadStringSetting(root, HttpProxyKey);
        var proxySupport = ReadStringSetting(root, HttpProxySupportKey);
        var noProxy = ReadStringSetting(root, HttpNoProxyKey);
        var useProxy = !string.IsNullOrWhiteSpace(proxy)
            && !string.Equals(proxySupport, ProxySupportOff, StringComparison.OrdinalIgnoreCase);
        return new NetworkSettings(useProxy, proxy, noProxy);
    }

    private static bool ApplyNetworkSettingsToRoot(JsonObject root, NetworkSettings settings)
    {
        var changed = false;
        if (settings.UseHttpProxy)
        {
            changed |= SetStringSetting(root, HttpProxyKey, settings.HttpProxy.Trim());
            changed |= SetStringSetting(root, HttpProxySupportKey, ProxySupportOverride);
            if (!string.IsNullOrWhiteSpace(settings.HttpNoProxy))
            {
                changed |= SetStringSetting(root, HttpNoProxyKey, settings.HttpNoProxy.Trim());
            }
        }
        else
        {
            changed |= SetStringSetting(root, HttpProxyKey, string.Empty);
            changed |= SetStringSetting(root, HttpProxySupportKey, ProxySupportOff);
        }

        return changed;
    }

    private static JsonObject ReadSettingsObject(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return new JsonObject();
        }

        var text = File.ReadAllText(settingsPath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(text, documentOptions: SettingsParseOptions) as JsonObject ?? new JsonObject();
    }

    private static void WriteSettingsObject(string settingsPath, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, root.ToJsonString(SettingsWriteOptions));
    }

    private static string GetSettingsPath(string userDataDirectory)
    {
        return Path.Combine(userDataDirectory, "User", "settings.json");
    }

    private static string ReadStringSetting(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text ?? string.Empty
            : string.Empty;
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
}
