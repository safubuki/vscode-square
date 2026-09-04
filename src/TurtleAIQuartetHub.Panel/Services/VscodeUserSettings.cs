using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public static class VscodeUserSettings
{
    public const string HttpProxyKey = "http.proxy";
    public const string HttpProxySupportKey = "http.proxySupport";
    public const string HttpNoProxyKey = "http.noProxy";
    public const string RemoteSshHttpsProxyKey = "remote.SSH.httpsProxy";
    public const string RestoreWindowsKey = "window.restoreWindows";
    public const string RestoreWindowsNone = "none";
    public const string ProxySupportOff = "off";
    public const string ProxySupportOverride = "override";
    public const string QuartetSettingsBackupFileName = "settings.json.quartet-bak";

    private static readonly JsonDocumentOptions SettingsParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
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
        return EnumerateWritableUserDataDirectories(config);
    }

    // ハブが書き込んでよいのは専用 user-data だけ。
    // 標準の %APPDATA%/Code/User/settings.json は通常 VS Code のファイルなので触らない。
    public static IReadOnlyList<string> EnumerateWritableUserDataDirectories(AppConfig config)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!config.UseDedicatedUserDataDirs)
        {
            return directories.ToArray();
        }

        foreach (var slot in config.Slots)
        {
            var directory = SlotUserDataPaths.GetUserDataDirectory(slot.Name, config);
            if (!IsInstalledCodeUserDataDirectory(directory))
            {
                directories.Add(directory);
            }
        }

        return directories.ToArray();
    }

    public static IReadOnlyList<string> EnumerateReadableUserDataDirectories(AppConfig config)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in EnumerateWritableUserDataDirectories(config))
        {
            directories.Add(directory);
        }

        var installed = SlotUserDataPaths.GetInstalledUserDataDirectory(config);
        if (!string.IsNullOrWhiteSpace(installed))
        {
            directories.Add(installed);
        }

        return directories.ToArray();
    }

    public static bool IsInstalledCodeUserDataDirectory(string? userDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(userDataDirectory))
        {
            return false;
        }

        foreach (var command in new[] { "code", "code-insiders" })
        {
            var installed = SlotUserDataPaths.GetInstalledUserDataDirectory(command);
            if (!string.IsNullOrWhiteSpace(installed)
                && string.Equals(userDataDirectory, installed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

        foreach (var userDataDirectory in EnumerateWritableUserDataDirectories(config))
        {
            if (IsInstalledCodeUserDataDirectory(userDataDirectory))
            {
                continue;
            }

            try
            {
                if (SharesAnyInstalledSettingsFile(userDataDirectory))
                {
                    continue;
                }

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
        MergeDedicatedSlotSettings(
            targetUserDataDirectory,
            config,
            SlotUserDataPaths.GetInstalledUserDataDirectory(codeCommand));
    }

    public static string GetSettingsFilePath(string userDataDirectory)
    {
        return GetSettingsPath(userDataDirectory);
    }

    public static bool SharesAnyInstalledSettingsFile(string userDataDirectory)
    {
        foreach (var command in new[] { "code", "code-insiders" })
        {
            if (SharesInstalledSettingsFile(userDataDirectory, SlotUserDataPaths.GetInstalledUserDataDirectory(command)))
            {
                return true;
            }
        }

        return false;
    }

    public static bool SharesInstalledSettingsFile(string userDataDirectory, string? installedUserDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(installedUserDataDirectory))
        {
            return false;
        }

        return AreSameFile(
            GetSettingsPath(userDataDirectory),
            GetSettingsPath(installedUserDataDirectory));
    }

    public static bool TryShareInstalledSettingsFile(string targetUserDataDirectory, string? installedUserDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(installedUserDataDirectory)
            || IsInstalledCodeUserDataDirectory(targetUserDataDirectory))
        {
            return false;
        }

        var installedSettingsPath = GetSettingsPath(installedUserDataDirectory);
        if (!File.Exists(installedSettingsPath))
        {
            return false;
        }

        var targetSettingsPath = GetSettingsPath(targetUserDataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(targetSettingsPath)!);

        if (AreSameFile(targetSettingsPath, installedSettingsPath))
        {
            return true;
        }

        string? backupPath = null;
        try
        {
            if (File.Exists(targetSettingsPath))
            {
                backupPath = Path.Combine(Path.GetDirectoryName(targetSettingsPath)!, QuartetSettingsBackupFileName);
                File.Copy(targetSettingsPath, backupPath, overwrite: true);
                File.Delete(targetSettingsPath);
            }

            if (NativeMethods.CreateHardLink(targetSettingsPath, installedSettingsPath, IntPtr.Zero))
            {
                return true;
            }

            DiagnosticLog.Write(
                LogLevel.Warn,
                $"Failed to share VS Code settings.json with Roaming profile: {Marshal.GetLastWin32Error()}.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath) && !File.Exists(targetSettingsPath))
        {
            File.Copy(backupPath, targetSettingsPath, overwrite: false);
        }

        return false;
    }

    public static void MergeDedicatedSlotSettings(
        string targetUserDataDirectory,
        AppConfig config,
        string? installedUserDataDirectory)
    {
        if (IsInstalledCodeUserDataDirectory(targetUserDataDirectory)
            || SharesInstalledSettingsFile(targetUserDataDirectory, installedUserDataDirectory)
            || SharesAnyInstalledSettingsFile(targetUserDataDirectory))
        {
            return;
        }

        var targetSettingsPath = GetSettingsPath(targetUserDataDirectory);
        EnsureSettingsFileSeeded(targetSettingsPath, installedUserDataDirectory);

        var text = File.ReadAllText(targetSettingsPath);
        var original = text;
        SetJsoncStringValue(ref text, RestoreWindowsKey, RestoreWindowsNone);
        if (config.ManageVsCodeUserSettings)
        {
            ApplyNetworkSettingsToJsonc(ref text, NetworkSettings.FromConfig(config));
        }

        if (string.Equals(original, text, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(targetSettingsPath, text);
    }

    public static bool TryReadNetworkSettings(AppConfig config, out NetworkSettings settings)
    {
        settings = NetworkSettings.Open;
        foreach (var userDataDirectory in EnumerateReadableUserDataDirectories(config))
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

    public static void ApplyManagedProxyEnvironment(ProcessStartInfo startInfo, AppConfig config)
    {
        if (!config.ManageVsCodeUserSettings)
        {
            return;
        }

        ApplyManagedProxyEnvironment(startInfo, NetworkSettings.FromConfig(config));
    }

    public static void ApplyManagedProxyEnvironment(ProcessStartInfo startInfo, NetworkSettings settings)
    {
        if (settings.UseHttpProxy)
        {
            var proxy = settings.HttpProxy.Trim();
            SetEnvironmentValue(startInfo, "HTTP_PROXY", proxy);
            SetEnvironmentValue(startInfo, "HTTPS_PROXY", proxy);
            SetEnvironmentValue(startInfo, "http_proxy", proxy);
            SetEnvironmentValue(startInfo, "https_proxy", proxy);
            if (!string.IsNullOrWhiteSpace(settings.HttpNoProxy))
            {
                var noProxy = settings.HttpNoProxy.Trim();
                SetEnvironmentValue(startInfo, "NO_PROXY", noProxy);
                SetEnvironmentValue(startInfo, "no_proxy", noProxy);
            }

            return;
        }

        SetEnvironmentValue(startInfo, "HTTP_PROXY", string.Empty);
        SetEnvironmentValue(startInfo, "HTTPS_PROXY", string.Empty);
        SetEnvironmentValue(startInfo, "http_proxy", string.Empty);
        SetEnvironmentValue(startInfo, "https_proxy", string.Empty);
        SetEnvironmentValue(startInfo, "NO_PROXY", "*");
        SetEnvironmentValue(startInfo, "no_proxy", "*");
    }

    public static bool ApplyToUserDataDirectory(
        string userDataDirectory,
        NetworkSettings settings,
        bool stampRestoreWindowsNone)
    {
        if (IsInstalledCodeUserDataDirectory(userDataDirectory)
            || SharesAnyInstalledSettingsFile(userDataDirectory))
        {
            return false;
        }

        var settingsPath = GetSettingsPath(userDataDirectory);
        EnsureSettingsFileSeeded(settingsPath, installedUserDataDirectory: null);

        var text = File.ReadAllText(settingsPath);
        var original = text;
        ApplyNetworkSettingsToJsonc(ref text, settings);
        if (stampRestoreWindowsNone)
        {
            SetJsoncStringValue(ref text, RestoreWindowsKey, RestoreWindowsNone);
        }

        if (string.Equals(original, text, StringComparison.Ordinal))
        {
            return false;
        }

        File.WriteAllText(settingsPath, text);
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

    private static void EnsureSettingsFileSeeded(string targetSettingsPath, string? installedUserDataDirectory)
    {
        if (File.Exists(targetSettingsPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetSettingsPath)!);
        var installedSettingsPath = string.IsNullOrWhiteSpace(installedUserDataDirectory)
            ? null
            : GetSettingsPath(installedUserDataDirectory);
        if (!string.IsNullOrWhiteSpace(installedSettingsPath)
            && File.Exists(installedSettingsPath)
            && !string.Equals(installedSettingsPath, targetSettingsPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(installedSettingsPath, targetSettingsPath, overwrite: false);
            return;
        }

        File.WriteAllText(targetSettingsPath, "{\n}\n");
    }

    private static void ApplyNetworkSettingsToJsonc(ref string text, NetworkSettings settings)
    {
        if (settings.UseHttpProxy)
        {
            var proxy = settings.HttpProxy.Trim();
            SetJsoncStringValue(ref text, HttpProxyKey, proxy);
            SetJsoncStringValue(ref text, HttpProxySupportKey, ProxySupportOverride);
            SetJsoncStringValue(ref text, RemoteSshHttpsProxyKey, proxy);
            if (!string.IsNullOrWhiteSpace(settings.HttpNoProxy))
            {
                SetJsoncStringValue(ref text, HttpNoProxyKey, settings.HttpNoProxy.Trim());
            }

            return;
        }

        SetJsoncStringValue(ref text, HttpProxyKey, string.Empty);
        SetJsoncStringValue(ref text, HttpProxySupportKey, ProxySupportOff);
        SetJsoncStringValue(ref text, RemoteSshHttpsProxyKey, string.Empty);
    }

    private static bool SetJsoncStringValue(ref string text, string key, string value)
    {
        return SetJsoncLiteral(ref text, key, JsonSerializer.Serialize(value));
    }

    private static bool SetJsoncLiteral(ref string text, string key, string literal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "{\n  " + QuoteJsoncKey(key) + ": " + literal + "\n}\n";
            return true;
        }

        var pattern = new Regex(
            @"^(?<prefix>[ \t]*""" + Regex.Escape(key) + @"""[ \t]*:[ \t]*)(?<value>""(?:\\.|[^""])*""|true|false|null|-?\d+(?:\.\d+)?)",
            RegexOptions.Multiline);
        var match = pattern.Match(text);
        if (match.Success)
        {
            if (string.Equals(match.Groups["value"].Value, literal, StringComparison.Ordinal))
            {
                return false;
            }

            text = pattern.Replace(text, current => current.Groups["prefix"].Value + literal, 1);
            return true;
        }

        return InsertJsoncProperty(ref text, QuoteJsoncKey(key) + ": " + literal);
    }

    private static bool InsertJsoncProperty(ref string text, string property)
    {
        var close = text.LastIndexOf('}');
        if (close < 0)
        {
            text = "{\n  " + property + "\n}\n";
            return true;
        }

        var before = text[..close];
        var trimmed = before.TrimEnd();
        var needsComma = trimmed.Length > 0
            && trimmed[^1] != '{'
            && trimmed[^1] != ',';
        var insertion = (needsComma ? "," : string.Empty) + "\n  " + property + "\n";
        text = before + insertion + text[close..];
        return true;
    }

    private static string QuoteJsoncKey(string key)
    {
        return "\"" + key + "\"";
    }

    private static bool AreSameFile(string pathA, string pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
        {
            return false;
        }

        if (string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(pathA);
        }

        if (!File.Exists(pathA) || !File.Exists(pathB))
        {
            return false;
        }

        try
        {
            using var streamA = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var streamB = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!NativeMethods.GetFileInformationByHandle(streamA.SafeFileHandle.DangerousGetHandle(), out var infoA)
                || !NativeMethods.GetFileInformationByHandle(streamB.SafeFileHandle.DangerousGetHandle(), out var infoB))
            {
                return false;
            }

            return infoA.VolumeSerialNumber == infoB.VolumeSerialNumber
                && infoA.FileIndexHigh == infoB.FileIndexHigh
                && infoA.FileIndexLow == infoB.FileIndexLow;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            return false;
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetFileInformationByHandle(IntPtr fileHandle, out ByHandleFileInformation fileInformation);

        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }

    private static void SetEnvironmentValue(ProcessStartInfo startInfo, string key, string value)
    {
        startInfo.Environment[key] = value;
        startInfo.EnvironmentVariables[key] = value;
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

}
