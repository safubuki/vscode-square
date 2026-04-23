using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurtleAIQuartetHub.Panel.Models;

public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string CodeCommand { get; set; } = "code";

    public string Monitor { get; set; } = "primary";

    public int Gap { get; set; } = 8;

    public bool UseDedicatedUserDataDirs { get; set; } = true;

    public bool InheritMainUserState { get; set; } = true;

    public bool ReopenLastWorkspace { get; set; } = true;

    public string StateDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TurtleAIQuartetHub");

    public int LaunchTimeoutSeconds { get; set; } = 40;

    public int RemoteReconnectTimeoutSeconds { get; set; } = 15;

    public int StatusRefreshIntervalMilliseconds { get; set; } = 750;

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

    public void Normalize()
    {
        CodeCommand = string.IsNullOrWhiteSpace(CodeCommand) ? "code" : CodeCommand.Trim();
        Monitor = string.IsNullOrWhiteSpace(Monitor) ? "primary" : Monitor.Trim();
        Gap = Math.Clamp(Gap, 0, 64);
        StateDirectory = string.IsNullOrWhiteSpace(StateDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurtleAIQuartetHub")
            : Environment.ExpandEnvironmentVariables(StateDirectory.Trim());
        LaunchTimeoutSeconds = Math.Clamp(LaunchTimeoutSeconds, 5, 120);
        RemoteReconnectTimeoutSeconds = Math.Clamp(RemoteReconnectTimeoutSeconds, 1, LaunchTimeoutSeconds);
        StatusRefreshIntervalMilliseconds = Math.Clamp(StatusRefreshIntervalMilliseconds, 250, 5000);

        var configuredSlots = Slots ?? DefaultSlots();
        Slots = configuredSlots
            .Where(slot => slot is not null && !string.IsNullOrWhiteSpace(slot.Name))
            .Take(4)
            .Select(slot => new SlotConfig
            {
                Name = slot.Name.Trim(),
                Path = slot.Path?.Trim() ?? string.Empty
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
            new() { Name = "A", Path = string.Empty },
            new() { Name = "B", Path = string.Empty },
            new() { Name = "C", Path = string.Empty },
            new() { Name = "D", Path = string.Empty }
        ];
    }
}
