using System.Diagnostics;
using System.Text.Json;
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
            "共有プロファイルでは標準 Code プロファイルへ書き込まない",
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

                var writable = VscodeUserSettings.EnumerateWritableUserDataDirectories(config);
                var installed = SlotUserDataPaths.GetInstalledUserDataDirectory("code");
                return writable.Count == 0
                    && (string.IsNullOrWhiteSpace(installed)
                        || !writable.Contains(installed, StringComparer.OrdinalIgnoreCase));
            });

        Verify(
            failures,
            "専用プロファイルの書き込み対象に標準 Code プロファイルを含めない",
            () =>
            {
                var config = new AppConfig
                {
                    UseDedicatedUserDataDirs = true,
                    StateDirectory = Path.Combine(Path.GetTempPath(), "quartet-settings-writable", Guid.NewGuid().ToString("N"))
                };
                config.Slots =
                [
                    new SlotConfig { Name = "A" },
                    new SlotConfig { Name = "B" },
                    new SlotConfig { Name = "C" },
                    new SlotConfig { Name = "D" }
                ];

                var writable = VscodeUserSettings.EnumerateWritableUserDataDirectories(config);
                var installed = SlotUserDataPaths.GetInstalledUserDataDirectory("code");
                return writable.Count == 4
                    && writable.All(directory => !VscodeUserSettings.IsInstalledCodeUserDataDirectory(directory))
                    && (string.IsNullOrWhiteSpace(installed)
                        || !writable.Contains(installed, StringComparer.OrdinalIgnoreCase));
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

        Verify(
            failures,
            "専用プロファイル同期はインストール済み settings があってもパネル側の既存キーを消さない",
            () =>
            {
                using var installed = new TempWorkspace();
                using var slot = new TempWorkspace();
                installed.WriteSettings(
                    """
                    {
                      "workbench.colorTheme": "Dark Modern",
                      "http.proxy": "http://old-proxy:8080"
                    }
                    """);
                slot.WriteSettings(
                    """
                    {
                      "editor.fontSize": 18,
                      "http.proxy": "http://panel-proxy:3128",
                      "http.proxySupport": "override"
                    }
                    """);

                var config = new AppConfig
                {
                    UseDedicatedUserDataDirs = true,
                    ManageVsCodeUserSettings = false
                };

                VscodeUserSettings.MergeDedicatedSlotSettings(
                    slot.UserDataDirectory,
                    config,
                    installed.UserDataDirectory);

                var root = slot.ReadSettings();
                var installedRoot = installed.ReadSettings();
                return root["editor.fontSize"]?.GetValue<int>() == 18
                    && root[VscodeUserSettings.HttpProxyKey]?.GetValue<string>() == "http://panel-proxy:3128"
                    && root[VscodeUserSettings.RestoreWindowsKey]?.GetValue<string>() == VscodeUserSettings.RestoreWindowsNone
                    && root["workbench.colorTheme"] is null
                    && installedRoot[VscodeUserSettings.HttpProxyKey]?.GetValue<string>() == "http://old-proxy:8080"
                    && installedRoot[VscodeUserSettings.RestoreWindowsKey] is null;
            });

        Verify(
            failures,
            "専用プロファイルの settings.json が無い初回だけインストール済み設定を種にする",
            () =>
            {
                using var installed = new TempWorkspace();
                using var slot = new TempWorkspace();
                installed.WriteSettings(
                    """
                    {
                      "editor.tabSize": 2,
                      "http.proxy": "http://seed-proxy:8080"
                    }
                    """);

                var settingsPath = Path.Combine(slot.UserDataDirectory, "User", "settings.json");
                if (File.Exists(settingsPath))
                {
                    File.Delete(settingsPath);
                }

                var config = new AppConfig
                {
                    UseDedicatedUserDataDirs = true,
                    ManageVsCodeUserSettings = false
                };

                VscodeUserSettings.MergeDedicatedSlotSettings(
                    slot.UserDataDirectory,
                    config,
                    installed.UserDataDirectory);

                var root = slot.ReadSettings();
                return root["editor.tabSize"]?.GetValue<int>() == 2
                    && root[VscodeUserSettings.HttpProxyKey]?.GetValue<string>() == "http://seed-proxy:8080"
                    && root[VscodeUserSettings.RestoreWindowsKey]?.GetValue<string>() == VscodeUserSettings.RestoreWindowsNone;
            });

        Verify(
            failures,
            "標準 Code プロファイルは書き込み対象判定で除外する",
            () =>
            {
                var installed = SlotUserDataPaths.GetInstalledUserDataDirectory("code");
                if (string.IsNullOrWhiteSpace(installed))
                {
                    return true;
                }

                var config = new AppConfig
                {
                    UseDedicatedUserDataDirs = true,
                    StateDirectory = Path.Combine(Path.GetTempPath(), "quartet-settings-exclude", Guid.NewGuid().ToString("N"))
                };
                config.Slots =
                [
                    new SlotConfig { Name = "A" },
                    new SlotConfig { Name = "B" },
                    new SlotConfig { Name = "C" },
                    new SlotConfig { Name = "D" }
                ];

                var writable = VscodeUserSettings.EnumerateWritableUserDataDirectories(config);
                return VscodeUserSettings.IsInstalledCodeUserDataDirectory(installed)
                    && !writable.Contains(installed, StringComparer.OrdinalIgnoreCase);
            });

        Verify(
            failures,
            "プロキシ環境は起動プロセスへ HTTP_PROXY を渡す",
            () =>
            {
                var startInfo = new ProcessStartInfo { UseShellExecute = false };
                VscodeUserSettings.ApplyManagedProxyEnvironment(
                    startInfo,
                    new VscodeUserSettings.NetworkSettings(true, "http://proxy.example.local:8080", "localhost"));
                return startInfo.Environment["HTTP_PROXY"] == "http://proxy.example.local:8080"
                    && startInfo.Environment["HTTPS_PROXY"] == "http://proxy.example.local:8080"
                    && startInfo.Environment["NO_PROXY"] == "localhost";
            });

        Verify(
            failures,
            "オープンネットワークは起動プロセスの HTTP_PROXY を空にする",
            () =>
            {
                var startInfo = new ProcessStartInfo { UseShellExecute = false };
                startInfo.Environment["HTTP_PROXY"] = "http://old-proxy:8080";
                VscodeUserSettings.ApplyManagedProxyEnvironment(
                    startInfo,
                    VscodeUserSettings.NetworkSettings.Open);
                return startInfo.Environment["HTTP_PROXY"] == string.Empty
                    && startInfo.Environment["NO_PROXY"] == "*";
            });

        Verify(
            failures,
            "専用プロファイル同期はコメントと SSH 設定を残し JSON 全体を書き直さない",
            () =>
            {
                using var slot = new TempWorkspace();
                const string original =
                    """
                    {
                      // 【社内プロキシ環境用】
                      "http.proxy": "http://proxy.mei.co.jp:8080",
                      "http.proxyStrictSSL": false,
                      "http.proxySupport": "override",
                      "remote.SSH.httpsProxy": "http://proxy.mei.co.jp:8080",
                      "remote.SSH.localServerDownload": "always",
                      "remote.SSH.remotePlatform": {
                        "ubuntu-R5-TA6": "linux"
                      }
                    }
                    """;
                slot.WriteSettings(original);

                var config = new AppConfig
                {
                    UseDedicatedUserDataDirs = true,
                    ManageVsCodeUserSettings = false
                };

                VscodeUserSettings.MergeDedicatedSlotSettings(slot.UserDataDirectory, config, installedUserDataDirectory: null);
                VscodeUserSettings.MergeDedicatedSlotSettings(slot.UserDataDirectory, config, installedUserDataDirectory: null);

                var raw = slot.ReadRawSettings();
                var root = slot.ReadSettings();
                return raw.Contains("【社内プロキシ環境用】", StringComparison.Ordinal)
                    && raw.Contains("\"remote.SSH.localServerDownload\": \"always\"", StringComparison.Ordinal)
                    && raw.Contains("ubuntu-R5-TA6", StringComparison.Ordinal)
                    && root[VscodeUserSettings.HttpProxyKey]?.GetValue<string>() == "http://proxy.mei.co.jp:8080"
                    && root[VscodeUserSettings.RemoteSshHttpsProxyKey]?.GetValue<string>() == "http://proxy.mei.co.jp:8080"
                    && root[VscodeUserSettings.RestoreWindowsKey]?.GetValue<string>() == VscodeUserSettings.RestoreWindowsNone;
            });

        Verify(
            failures,
            "専用プロファイルの settings.json は Roaming と同じ実体になり restoreWindows を書かない",
            () =>
            {
                using var installed = new TempWorkspace();
                using var slot = new TempWorkspace();
                installed.WriteSettings(
                    """
                    {
                      "http.proxy": "http://proxy.mei.co.jp:8080",
                      "remote.SSH.httpsProxy": "http://proxy.mei.co.jp:8080"
                    }
                    """);
                slot.WriteSettings(
                    """
                    {
                      "editor.fontSize": 99
                    }
                    """);

                var shared = VscodeUserSettings.TryShareInstalledSettingsFile(
                    slot.UserDataDirectory,
                    installed.UserDataDirectory);
                var config = new AppConfig
                {
                    UseDedicatedUserDataDirs = true,
                    ManageVsCodeUserSettings = false
                };
                VscodeUserSettings.MergeDedicatedSlotSettings(
                    slot.UserDataDirectory,
                    config,
                    installed.UserDataDirectory);

                var installedRoot = installed.ReadSettings();
                var slotRoot = slot.ReadSettings();
                var backupPath = Path.Combine(
                    slot.UserDataDirectory,
                    "User",
                    VscodeUserSettings.QuartetSettingsBackupFileName);
                return shared
                    && VscodeUserSettings.SharesInstalledSettingsFile(slot.UserDataDirectory, installed.UserDataDirectory)
                    && slotRoot[VscodeUserSettings.HttpProxyKey]?.GetValue<string>() == "http://proxy.mei.co.jp:8080"
                    && installedRoot[VscodeUserSettings.RestoreWindowsKey] is null
                    && slotRoot[VscodeUserSettings.RestoreWindowsKey] is null
                    && File.Exists(backupPath)
                    && File.ReadAllText(backupPath).Contains("editor.fontSize", StringComparison.Ordinal);
            });

        Verify(
            failures,
            "Roaming の settings.json が無いときは作らず専用側もリンクしない",
            () =>
            {
                using var installed = new TempWorkspace();
                using var slot = new TempWorkspace();
                var installedSettings = Path.Combine(installed.UserDataDirectory, "User", "settings.json");
                if (File.Exists(installedSettings))
                {
                    File.Delete(installedSettings);
                }

                slot.WriteSettings("""{ "editor.fontSize": 14 }""");
                var shared = VscodeUserSettings.TryShareInstalledSettingsFile(
                    slot.UserDataDirectory,
                    installed.UserDataDirectory);
                return !shared
                    && !File.Exists(installedSettings)
                    && slot.ReadSettings()["editor.fontSize"]?.GetValue<int>() == 14;
            });

        Verify(
            failures,
            "コメントアウトしたプロキシ行は同期で有効化しない",
            () =>
            {
                using var slot = new TempWorkspace();
                slot.WriteSettings(
                    """
                    {
                      // "http.proxy": "http://proxy.mei.co.jp:8080",
                      "editor.fontSize": 14
                    }
                    """);

                var config = new AppConfig
                {
                    UseDedicatedUserDataDirs = true,
                    ManageVsCodeUserSettings = false
                };

                VscodeUserSettings.MergeDedicatedSlotSettings(slot.UserDataDirectory, config, installedUserDataDirectory: null);

                var raw = slot.ReadRawSettings();
                var root = slot.ReadSettings();
                return raw.Contains("// \"http.proxy\": \"http://proxy.mei.co.jp:8080\"", StringComparison.Ordinal)
                    && root[VscodeUserSettings.HttpProxyKey] is null
                    && root["editor.fontSize"]?.GetValue<int>() == 14;
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
            return JsonNode.Parse(
                    File.ReadAllText(path),
                    documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    }) as JsonObject
                ?? throw new InvalidOperationException("settings.json を読めませんでした。");
        }

        public string ReadRawSettings()
        {
            return File.ReadAllText(Path.Combine(UserDataDirectory, "User", "settings.json"));
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
