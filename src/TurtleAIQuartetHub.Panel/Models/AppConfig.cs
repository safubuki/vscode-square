using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurtleAIQuartetHub.Panel.Models;

public sealed class AppConfig
{
    public const string VsCodeApplicationId = "vscode";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string CodeCommand { get; set; } = "code";

    public string Monitor { get; set; } = "primary";

    public int Gap { get; set; } = 6;

    // 既定は標準 VS Code プロファイルを共有する。スロット別に user-data-dir を切ると
    // Cache / WebStorage / CachedExtensionVSIXs が 4 倍になりディスクを圧迫する。
    // ウィンドウ別のレイアウトは slots.json の PreferredLayout で覚える。
    public bool UseDedicatedUserDataDirs { get; set; }

    public bool InheritMainUserState { get; set; }

    // 歯車設定からネットワーク設定をハブ側で管理するか。
    // false の間は専用プロファイルの既存 settings.json を起動時に上書きしない。
    // true でも標準の %APPDATA%/Code/User/settings.json は書き換えない。
    public bool ManageVsCodeUserSettings { get; set; }

    public bool VsCodeUseHttpProxy { get; set; }

    public string VsCodeHttpProxy { get; set; } = string.Empty;

    public string VsCodeHttpNoProxy { get; set; } = string.Empty;

    public bool ReopenLastWorkspace { get; set; } = true;

    public string StateDirectory { get; set; } = GetDefaultStateDirectory();

    public int LaunchTimeoutSeconds { get; set; } = 20;

    public int RemoteReconnectTimeoutSeconds { get; set; } = 5;

    public int StatusRefreshIntervalMilliseconds { get; set; } = 1000;

    public string DefaultWorkspaceApplicationId { get; set; } = VsCodeApplicationId;

    public List<ToolApplicationConfig> Applications { get; set; } = DefaultApplications();

    public List<SlotConfig> Slots { get; set; } = DefaultSlots();

    [JsonIgnore]
    public string ConfigSource { get; private set; } = "built-in defaults";

    public static AppConfig Load()
    {
        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            config.ConfigSource = path;
            config.Normalize();
            return config;
        }

        var fallback = new AppConfig();
        fallback.Normalize();
        return fallback;
    }

    public static string GetUserConfigPath()
    {
        return Path.Combine(GetDefaultStateDirectory(), "config", "turtle-ai-quartet-hub.json");
    }

    public void SaveToUserConfig()
    {
        Normalize();
        var path = GetUserConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        ConfigSource = path;
    }

    public void Normalize()
    {
        CodeCommand = string.IsNullOrWhiteSpace(CodeCommand) ? "code" : CodeCommand.Trim();
        Monitor = string.IsNullOrWhiteSpace(Monitor) ? "primary" : Monitor.Trim();
        Gap = Math.Clamp(Gap, 0, 64);
        StateDirectory = string.IsNullOrWhiteSpace(StateDirectory)
            ? GetDefaultStateDirectory()
            : Environment.ExpandEnvironmentVariables(StateDirectory.Trim());
        LaunchTimeoutSeconds = Math.Clamp(LaunchTimeoutSeconds, 3, 60);
        RemoteReconnectTimeoutSeconds = Math.Clamp(RemoteReconnectTimeoutSeconds, 1, Math.Min(LaunchTimeoutSeconds, 8));
        StatusRefreshIntervalMilliseconds = Math.Clamp(StatusRefreshIntervalMilliseconds, 250, 5000);
        DefaultWorkspaceApplicationId = NormalizeApplicationId(DefaultWorkspaceApplicationId, VsCodeApplicationId);
        VsCodeHttpProxy = VsCodeHttpProxy?.Trim() ?? string.Empty;
        VsCodeHttpNoProxy = VsCodeHttpNoProxy?.Trim() ?? string.Empty;
        Applications = NormalizeApplications(Applications, CodeCommand);

        var configuredSlots = Slots ?? DefaultSlots();
        Slots = configuredSlots
            .Where(slot => slot is not null && !string.IsNullOrWhiteSpace(slot.Name))
            .Take(4)
            .Select(slot => new SlotConfig
            {
                Name = slot.Name.Trim(),
                Path = slot.Path?.Trim() ?? string.Empty,
                ApplicationId = NormalizeApplicationId(slot.ApplicationId, DefaultWorkspaceApplicationId)
            })
            .ToList();

        var defaults = DefaultSlots();
        while (Slots.Count < 4)
        {
            Slots.Add(defaults[Slots.Count]);
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return GetUserConfigPath();

        var roots = GetSearchRoots().ToList();

        foreach (var root in roots)
        {
            yield return Path.Combine(root, "config", "turtle-ai-quartet-hub.json");
        }

        foreach (var root in roots)
        {
            yield return Path.Combine(root, "config", "turtle-ai-quartet-hub.example.json");
        }
    }

    private static string GetDefaultStateDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TurtleAIQuartetHub");
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                continue;
            }

            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (seen.Add(directory.FullName))
                {
                    yield return directory.FullName;
                }

                directory = directory.Parent;
            }
        }
    }

    private static List<SlotConfig> DefaultSlots()
    {
        return
        [
            new() { Name = "A", Path = string.Empty, ApplicationId = VsCodeApplicationId },
            new() { Name = "B", Path = string.Empty, ApplicationId = VsCodeApplicationId },
            new() { Name = "C", Path = string.Empty, ApplicationId = VsCodeApplicationId },
            new() { Name = "D", Path = string.Empty, ApplicationId = VsCodeApplicationId }
        ];
    }

    public static string NormalizeApplicationId(string? applicationId, string fallback = VsCodeApplicationId)
    {
        return string.IsNullOrWhiteSpace(applicationId)
            ? fallback
            : applicationId.Trim().ToLowerInvariant();
    }

    private static List<ToolApplicationConfig> NormalizeApplications(
        IEnumerable<ToolApplicationConfig>? configuredApplications,
        string codeCommand)
    {
        var defaults = DefaultApplications();
        var byId = new Dictionary<string, ToolApplicationConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in defaults)
        {
            byId[NormalizeApplicationId(app.Id)] = app;
        }

        if (configuredApplications is not null)
        {
            foreach (var app in configuredApplications.Where(app => app is not null && !string.IsNullOrWhiteSpace(app.Id)))
            {
                var id = NormalizeApplicationId(app.Id);
                byId[id] = MergeApplication(byId.TryGetValue(id, out var fallback) ? fallback : null, app);
            }
        }

        if (byId.TryGetValue(VsCodeApplicationId, out var vsCode)
            && string.IsNullOrWhiteSpace(vsCode.Command))
        {
            vsCode.Command = codeCommand;
        }

        return byId.Values
            .Select(NormalizeApplication)
            .OrderBy(app => GetApplicationSortOrder(app.Id))
            .ThenBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ToolApplicationConfig MergeApplication(ToolApplicationConfig? fallback, ToolApplicationConfig configured)
    {
        configured.Detection ??= new ApplicationDetectionConfig();
        configured.Arguments ??= [];
        configured.Detection.Commands ??= [];
        configured.Detection.ProcessNames ??= [];
        configured.Detection.StartMenuNames ??= [];
        configured.Detection.AppPathNames ??= [];
        if (fallback is not null)
        {
            fallback.Arguments ??= [];
            fallback.Detection ??= new ApplicationDetectionConfig();
            fallback.Detection.Commands ??= [];
            fallback.Detection.ProcessNames ??= [];
            fallback.Detection.StartMenuNames ??= [];
            fallback.Detection.AppPathNames ??= [];
        }

        if (fallback is null)
        {
            return configured;
        }

        var useFallbackDetection = ShouldUseFallbackDetection(configured);

        return new ToolApplicationConfig
        {
            Id = configured.Id,
            DisplayName = string.IsNullOrWhiteSpace(configured.DisplayName) ? fallback.DisplayName : configured.DisplayName,
            ShortName = string.IsNullOrWhiteSpace(configured.ShortName) ? fallback.ShortName : configured.ShortName,
            Kind = fallback.Kind,
            Command = MergeApplicationCommand(fallback, configured),
            Arguments = configured.Arguments.Count > 0 ? configured.Arguments : fallback.Arguments,
            SupportsMultipleWindows = configured.SupportsMultipleWindows || fallback.SupportsMultipleWindows,
            Detection = new ApplicationDetectionConfig
            {
                Commands = useFallbackDetection || configured.Detection.Commands.Count == 0
                    ? fallback.Detection.Commands
                    : configured.Detection.Commands,
                ProcessNames = useFallbackDetection || configured.Detection.ProcessNames.Count == 0
                    ? fallback.Detection.ProcessNames
                    : configured.Detection.ProcessNames,
                StartMenuNames = useFallbackDetection || configured.Detection.StartMenuNames.Count == 0
                    ? fallback.Detection.StartMenuNames
                    : configured.Detection.StartMenuNames,
                AppPathNames = useFallbackDetection || configured.Detection.AppPathNames.Count == 0
                    ? fallback.Detection.AppPathNames
                    : configured.Detection.AppPathNames
            }
        };
    }

    private static ToolApplicationConfig NormalizeApplication(ToolApplicationConfig app)
    {
        app.Id = NormalizeApplicationId(app.Id);
        app.DisplayName = string.IsNullOrWhiteSpace(app.DisplayName) ? app.Id : app.DisplayName.Trim();
        app.ShortName = string.IsNullOrWhiteSpace(app.ShortName) ? app.DisplayName : app.ShortName.Trim();
        app.Command = Environment.ExpandEnvironmentVariables(app.Command?.Trim() ?? string.Empty);
        app.Arguments ??= [];
        app.Arguments = app.Arguments
            .Where(argument => argument is not null)
            .Select(argument => argument.Trim())
            .Where(argument => !string.IsNullOrWhiteSpace(argument))
            .ToList();
        app.Detection ??= new ApplicationDetectionConfig();
        app.Detection.Commands ??= [];
        app.Detection.ProcessNames ??= [];
        app.Detection.StartMenuNames ??= [];
        app.Detection.AppPathNames ??= [];
        app.Detection.Commands = NormalizeStringList(app.Detection.Commands);
        app.Detection.ProcessNames = NormalizeStringList(app.Detection.ProcessNames);
        app.Detection.StartMenuNames = NormalizeStringList(app.Detection.StartMenuNames);
        app.Detection.AppPathNames = NormalizeStringList(app.Detection.AppPathNames);

        if (app.Detection.Commands.Count == 0 && !string.IsNullOrWhiteSpace(app.Command))
        {
            app.Detection.Commands.Add(app.Command);
        }

        if (app.Detection.ProcessNames.Count == 0)
        {
            app.Detection.ProcessNames.Add(app.DisplayName);
        }

        return app;
    }

    private static List<string> NormalizeStringList(IEnumerable<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    private static int GetApplicationSortOrder(string applicationId)
    {
        return NormalizeApplicationId(applicationId) switch
        {
            VsCodeApplicationId => 0,
            "antigravity" => 1,
            "codex" => 2,
            "claude" => 3,
            "github-copilot" => 4,
            "grok" => 5,
            "gemini" => 6,
            "chatgpt-app" => 7,
            "codex-app" => 8,
            "claude-app" => 9,
            "antigravity-app" => 10,
            _ => 100
        };
    }

    private static string MergeApplicationCommand(ToolApplicationConfig fallback, ToolApplicationConfig configured)
    {
        var configuredCommand = configured.Command?.Trim() ?? string.Empty;
        if (IsLegacyAntigravityIdeCommand(configured.Id, configuredCommand))
        {
            return fallback.Command;
        }

        return string.IsNullOrWhiteSpace(configuredCommand) ? fallback.Command : configuredCommand;
    }

    private static bool IsLegacyAntigravityIdeCommand(string? applicationId, string command)
    {
        return string.Equals(NormalizeApplicationId(applicationId), "antigravity", StringComparison.OrdinalIgnoreCase)
            && string.Equals(command.Trim('"'), "antigravity", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseFallbackDetection(ToolApplicationConfig configured)
    {
        if (!string.Equals(NormalizeApplicationId(configured.Id), "antigravity", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (configured.Detection.AppPathNames.Any(name =>
            string.Equals(name, "Antigravity IDE.exe", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return IsLegacyAntigravityIdeCommand(configured.Id, configured.Command?.Trim() ?? string.Empty)
            || configured.Detection.Commands.Any(command => string.Equals(command, "antigravity", StringComparison.OrdinalIgnoreCase))
            || configured.Detection.ProcessNames.Any(name => string.Equals(name, "Antigravity", StringComparison.OrdinalIgnoreCase))
            || configured.Detection.StartMenuNames.Any(name =>
                string.Equals(name, "Antigravity", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Google Antigravity", StringComparison.OrdinalIgnoreCase))
            || configured.Detection.AppPathNames.Any(name => string.Equals(name, "Antigravity.exe", StringComparison.OrdinalIgnoreCase));
    }

    private static List<ToolApplicationConfig> DefaultApplications()
    {
        return
        [
            new()
            {
                Id = VsCodeApplicationId,
                DisplayName = "VS Code",
                ShortName = "VS Code",
                Kind = ApplicationKind.WorkspaceIde,
                Command = "code",
                Arguments = ["--new-window", "{workspacePath}"],
                SupportsMultipleWindows = true,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = ["code", "code-insiders"],
                    ProcessNames = ["Code", "Code - Insiders", "VSCodium", "Codium"],
                    StartMenuNames = ["Visual Studio Code", "Visual Studio Code - Insiders", "VSCodium"],
                    AppPathNames = ["Code.exe", "Code - Insiders.exe", "VSCodium.exe", "Codium.exe"]
                }
            },
            new()
            {
                Id = "antigravity",
                DisplayName = "Antigravity",
                ShortName = "Antigravity",
                Kind = ApplicationKind.WorkspaceIde,
                Command = string.Empty,
                Arguments = ["--new-window", "{workspacePath}"],
                SupportsMultipleWindows = true,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = ["antigravity-ide"],
                    ProcessNames = ["Antigravity IDE"],
                    StartMenuNames = ["Antigravity IDE", "Google Antigravity IDE"],
                    AppPathNames = ["Antigravity IDE.exe"]
                }
            },
            new()
            {
                Id = "codex",
                DisplayName = "Codex CLI",
                ShortName = "Codex",
                Kind = ApplicationKind.WorkspaceCli,
                Command = "codex",
                Arguments = [],
                SupportsMultipleWindows = false,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = ["codex"],
                    ProcessNames = ["cmd", "WindowsTerminal", "OpenConsole", "powershell", "pwsh"],
                    StartMenuNames = [],
                    AppPathNames = []
                }
            },
            new()
            {
                Id = "github-copilot",
                DisplayName = "GitHub Copilot CLI",
                ShortName = "Copilot",
                Kind = ApplicationKind.WorkspaceCli,
                Command = "copilot",
                Arguments = [],
                SupportsMultipleWindows = false,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = ["copilot"],
                    ProcessNames = ["cmd", "WindowsTerminal", "OpenConsole", "powershell", "pwsh"],
                    StartMenuNames = [],
                    AppPathNames = []
                }
            },
            new()
            {
                Id = "gemini",
                DisplayName = "Gemini CLI",
                ShortName = "Gemini",
                Kind = ApplicationKind.WorkspaceCli,
                Command = "gemini",
                Arguments = [],
                SupportsMultipleWindows = false,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = ["gemini"],
                    ProcessNames = ["cmd", "WindowsTerminal", "OpenConsole", "powershell", "pwsh"],
                    StartMenuNames = [],
                    AppPathNames = []
                }
            },
            new()
            {
                Id = "grok",
                DisplayName = "Grok Build CLI",
                ShortName = "Grok",
                Kind = ApplicationKind.WorkspaceCli,
                Command = "grok",
                Arguments = [],
                SupportsMultipleWindows = false,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = ["grok"],
                    ProcessNames = ["cmd", "WindowsTerminal", "OpenConsole", "powershell", "pwsh"],
                    StartMenuNames = [],
                    AppPathNames = []
                }
            },
            new()
            {
                Id = "claude",
                DisplayName = "Claude CLI",
                ShortName = "Claude",
                Kind = ApplicationKind.WorkspaceCli,
                Command = "claude",
                Arguments = [],
                SupportsMultipleWindows = false,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = ["claude"],
                    ProcessNames = ["cmd", "WindowsTerminal", "OpenConsole", "powershell", "pwsh"],
                    StartMenuNames = [],
                    AppPathNames = []
                }
            },
            new()
            {
                Id = "chatgpt-app",
                DisplayName = "ChatGPT",
                ShortName = "ChatGPT",
                Kind = ApplicationKind.SingleWindowAgent,
                Command = string.Empty,
                Arguments = [],
                SupportsMultipleWindows = false,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = [],
                    ProcessNames = ["ChatGPT", "OpenAI ChatGPT"],
                    StartMenuNames = ["ChatGPT", "OpenAI ChatGPT"],
                    AppPathNames = ["ChatGPT.exe", "OpenAI ChatGPT.exe"]
                }
            },
            new()
            {
                Id = "codex-app",
                DisplayName = "Codex",
                ShortName = "Codex",
                Kind = ApplicationKind.SingleWindowAgent,
                Command = string.Empty,
                Arguments = [],
                SupportsMultipleWindows = false,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = [],
                    ProcessNames = ["Codex"],
                    StartMenuNames = ["Codex", "OpenAI Codex"],
                    AppPathNames = ["Codex.exe"]
                }
            },
            new()
            {
                Id = "claude-app",
                DisplayName = "Claude",
                ShortName = "Claude",
                Kind = ApplicationKind.SingleWindowAgent,
                Command = string.Empty,
                Arguments = [],
                SupportsMultipleWindows = false,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = [],
                    ProcessNames = ["Claude"],
                    StartMenuNames = ["Claude"],
                    AppPathNames = ["Claude.exe"]
                }
            },
            new()
            {
                Id = "antigravity-app",
                DisplayName = "Antigravity2",
                ShortName = "Antigravity2",
                Kind = ApplicationKind.SingleWindowAgent,
                Command = string.Empty,
                Arguments = [],
                SupportsMultipleWindows = false,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = [],
                    ProcessNames = ["Antigravity"],
                    StartMenuNames = ["Antigravity2"],
                    AppPathNames = ["Antigravity.exe"]
                }
            }
        ];
    }
}
