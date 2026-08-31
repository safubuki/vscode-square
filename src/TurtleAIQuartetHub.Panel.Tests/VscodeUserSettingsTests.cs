using System.Text.Json.Nodes;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class VscodeUserSettingsTests
{
    public static void Run(List<string> failures)
    {
        Verify(
            failures,
            "オープンネットワークは既存キーを残したまま proxy を無効化する",
            () =>
            {
                using var workspace = new TempWorkspace();
                var settingsPath = workspace.WriteSettings(
                    """
                    {
                      "workbench.colorTheme": "Dark Modern",
                      "http.proxy": "http://old-proxy:8080",
                      "http.proxySupport": "override"
                    }
                    """);

                VscodeUserSettings.ApplyToUserDataDirectory(
                    workspace.UserDataDirectory,
                    VscodeUserSettings.NetworkSettings.Open,
                    stampRestoreWindowsNone: false);

                var root = workspace.ReadSettings();
                return root["workbench.colorTheme"]?.GetValue<string>() == "Dark Modern"
                    && root["http.proxy"]?.GetValue<string>() == string.Empty
                    && root["http.proxySupport"]?.GetValue<string>() == VscodeUserSettings.ProxySupportOff
                    && root[VscodeUserSettings.RestoreWindowsKey] is null;
            });

        Verify(
            failures,
            "プロキシ環境は URL と override を書き、専用プロファイルだけ restoreWindows を none にする",
            () =>
            {
                using var workspace = new TempWorkspace();
                var proxy = new VscodeUserSettings.NetworkSettings(
                    true,
                    "http://proxy.example.local:8080",
                    "localhost,127.0.0.1");

                VscodeUserSettings.ApplyToUserDataDirectory(workspace.UserDataDirectory, proxy, stampRestoreWindowsNone: true);

                var root = workspace.ReadSettings();
                return root[VscodeUserSettings.HttpProxyKey]?.GetValue<string>() == "http://proxy.example.local:8080"
                    && root[VscodeUserSettings.HttpProxySupportKey]?.GetValue<string>() == VscodeUserSettings.ProxySupportOverride
                    && root[VscodeUserSettings.HttpNoProxyKey]?.GetValue<string>() == "localhost,127.0.0.1"
                    && root[VscodeUserSettings.RestoreWindowsKey]?.GetValue<string>() == VscodeUserSettings.RestoreWindowsNone;
            });

        Verify(
            failures,
            "プロキシ URL が空のときは適用しない",
            () => new VscodeUserSettings.NetworkSettings(true, "  ", string.Empty).Validate() is not null);

        Verify(
            failures,
            "共有プロファイルでは対象ディレクトリが重複しない",
            () =>
            {
                var config = new AppConfig
                {
                    UseDedicatedUserDataDirs = false,
                    StateDirectory = Path.Combine(Path.GetTempPath(), "quartet-settings-test")
                };
                config.Slots =
                [
                    new SlotConfig { Name = "A" },
                    new SlotConfig { Name = "B" },
                    new SlotConfig { Name = "C" },
                    new SlotConfig { Name = "D" }
                ];

                var directories = VscodeUserSettings.EnumerateTargetUserDataDirectories(config);
                return directories.Count == 1
                    || directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() == directories.Count;
            });

        Verify(
            failures,
            "専用プロファイル同期は既存のユーザー設定を残して restoreWindows だけ none にする",
            () =>
            {
                using var slot = new TempWorkspace();
                slot.WriteSettings(
                    """
                    {
                      "editor.fontSize": 14,
                      "http.proxy": "http://office-proxy:3128",
                      "http.proxySupport": "override"
                    }
                    """);

                var config = new AppConfig
                {
                    UseDedicatedUserDataDirs = true,
                    ManageVsCodeUserSettings = false,
                    CodeCommand = "unknown-editor"
                };

                VscodeUserSettings.SynchronizeDedicatedSlotSettings(
                    slot.UserDataDirectory,
                    config,
                    "unknown-editor");

                var root = slot.ReadSettings();
                return root["editor.fontSize"]?.GetValue<int>() == 14
                    && root[VscodeUserSettings.HttpProxyKey]?.GetValue<string>() == "http://office-proxy:3128"
                    && root[VscodeUserSettings.RestoreWindowsKey]?.GetValue<string>() == VscodeUserSettings.RestoreWindowsNone;
            });
    }

    private static void Verify(List<string> failures, string name, Func<bool> assertion)
    {
        try
        {
            if (!assertion())
            {
                failures.Add(name);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            UserDataDirectory = Path.Combine(Path.GetTempPath(), "quartet-vscode-settings", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(UserDataDirectory, "User"));
        }

        public string UserDataDirectory { get; }

        public string WriteSettings(string json)
        {
            var path = Path.Combine(UserDataDirectory, "User", "settings.json");
            File.WriteAllText(path, json);
            return path;
        }

        public JsonObject ReadSettings()
        {
            var path = Path.Combine(UserDataDirectory, "User", "settings.json");
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidOperationException("settings.json を読めませんでした。");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(UserDataDirectory))
                {
                    Directory.Delete(UserDataDirectory, true);
                }
            }
            catch
            {
                // 一時ファイルの後始末失敗はテスト本体を落とさない。
            }
        }
    }
}
