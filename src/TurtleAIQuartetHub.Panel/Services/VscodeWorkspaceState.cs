using System.IO;
using System.Text.Json;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public static class VscodeWorkspaceState
{
    private const string AntigravityApplicationId = "antigravity";

    // workspaceStorage のスキャン結果キャッシュ。共有プロファイルでは配下が数百フォルダに
    // なり得るのに、状態リフレッシュのたび（既定 1 秒周期×4 スロット）に全列挙＋全 JSON
    // パースするのはディスク IO と GC の無駄が大きい。新しいワークスペースを開くと
    // workspaceStorage 直下にフォルダが増えてルートの更新時刻が変わるため、
    // 「ルートの LastWriteTimeUtc が変わったら再スキャン＋保険の TTL」で十分追従できる。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedScan> ScanCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ScanCacheTtl = TimeSpan.FromSeconds(60);

    private sealed record CachedScan(
        DateTime ScannedAtUtc,
        DateTime RootWriteTimeUtc,
        IReadOnlyList<WorkspacePathCandidate> Candidates);

    public static string? TryReadCurrentWorkspacePath(WindowSlot slot, AppConfig config)
    {
        return TryReadCurrentWorkspacePath(slot.ApplicationId, slot.RuntimeSlotName, slot.WindowTitle, config);
    }

    public static string? TryReadCurrentWorkspacePath(string slotName, string windowTitle, AppConfig config)
    {
        return TryReadCurrentWorkspacePath(AppConfig.VsCodeApplicationId, slotName, windowTitle, config);
    }

    public static string? TryReadCurrentWorkspacePath(string? applicationId, string slotName, string windowTitle, AppConfig config)
    {
        foreach (var candidate in TryReadWorkspacePathCandidates(applicationId, slotName, config))
        {
            if (IsWorkspaceVisibleInWindowTitle(windowTitle, candidate.WorkspacePath))
            {
                return candidate.WorkspacePath;
            }
        }

        return null;
    }

    public static string? TryReadLastWorkspacePath(WindowSlot slot, AppConfig config)
    {
        return TryReadLastWorkspacePath(slot.ApplicationId, slot.RuntimeSlotName, config);
    }

    public static string? TryReadLastWorkspacePath(string slotName, AppConfig config)
    {
        return TryReadLastWorkspacePath(AppConfig.VsCodeApplicationId, slotName, config);
    }

    public static string? TryReadLastWorkspacePath(string? applicationId, string slotName, AppConfig config)
    {
        var candidate = TryReadWorkspacePathCandidates(applicationId, slotName, config).FirstOrDefault();
        return string.IsNullOrWhiteSpace(candidate.WorkspacePath)
            ? null
            : candidate.WorkspacePath;
    }

    private static IReadOnlyList<WorkspacePathCandidate> TryReadWorkspacePathCandidates(
        string? applicationId,
        string slotName,
        AppConfig config)
    {
        var candidates = new List<WorkspacePathCandidate>();

        foreach (var workspaceStorageDirectory in GetWorkspaceStorageDirectories(applicationId, slotName, config)
                     .Where(directory => !string.IsNullOrWhiteSpace(directory))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            candidates.AddRange(GetCandidatesForDirectory(workspaceStorageDirectory));
        }

        return candidates
            .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
            .ToList();
    }

    private static IReadOnlyList<WorkspacePathCandidate> GetCandidatesForDirectory(string workspaceStorageDirectory)
    {
        if (!Directory.Exists(workspaceStorageDirectory))
        {
            return [];
        }

        var now = DateTime.UtcNow;
        var rootWriteTimeUtc = Directory.GetLastWriteTimeUtc(workspaceStorageDirectory);
        if (ScanCache.TryGetValue(workspaceStorageDirectory, out var cached)
            && cached.RootWriteTimeUtc == rootWriteTimeUtc
            && now - cached.ScannedAtUtc < ScanCacheTtl)
        {
            return cached.Candidates;
        }

        var candidates = new List<WorkspacePathCandidate>();
        try
        {
            // VS Code / Antigravity とも workspace.json は workspaceStorage/<hash>/ 直下の
            // 1 階層目に置かれる。AllDirectories で state.vscdb 等まで含む全ツリーを歩くのは
            // 無駄なので、サブフォルダ直下だけを見る。
            foreach (var directory in Directory.EnumerateDirectories(workspaceStorageDirectory))
            {
                var workspaceJsonPath = Path.Combine(directory, "workspace.json");
                if (!File.Exists(workspaceJsonPath))
                {
                    continue;
                }

                var workspacePath = TryReadWorkspaceJson(workspaceJsonPath);
                if (!string.IsNullOrWhiteSpace(workspacePath))
                {
                    candidates.Add(new WorkspacePathCandidate(workspacePath, File.GetLastWriteTimeUtc(workspaceJsonPath)));
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        ScanCache[workspaceStorageDirectory] = new CachedScan(now, rootWriteTimeUtc, candidates);
        return candidates;
    }

    private static IEnumerable<string> GetWorkspaceStorageDirectories(string? applicationId, string slotName, AppConfig config)
    {
        var normalizedApplicationId = AppConfig.NormalizeApplicationId(applicationId);

        if (string.Equals(normalizedApplicationId, AppConfig.VsCodeApplicationId, StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(
                SlotUserDataPaths.GetEffectiveUserDataDirectory(slotName, config),
                "User",
                "workspaceStorage");
            yield break;
        }

        if (!string.Equals(normalizedApplicationId, AntigravityApplicationId, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        foreach (var appDataRoot in GetAntigravityApplicationDataRoots())
        {
            yield return Path.Combine(appDataRoot, "User", "workspaceStorage");
        }
    }

    private static IEnumerable<string> GetAntigravityApplicationDataRoots()
    {
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            yield return Path.Combine(roamingAppData, "Antigravity");
            yield return Path.Combine(roamingAppData, "Google", "Antigravity");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Antigravity");
            yield return Path.Combine(localAppData, "Google", "Antigravity");
        }
    }

    public static bool IsWorkspaceVisibleInWindowTitle(string? windowTitle, string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        foreach (var candidate in GetWorkspaceTitleCandidates(workspacePath))
        {
            if (windowTitle.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetWorkspaceTitleCandidates(string workspacePath)
    {
        var normalizedPath = GetComparableWorkspacePath(workspacePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            yield break;
        }

        var fileName = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            yield return fileName;
        }

        if (Path.HasExtension(normalizedPath))
        {
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalizedPath);
            if (!string.IsNullOrWhiteSpace(fileNameWithoutExtension)
                && !string.Equals(fileNameWithoutExtension, fileName, StringComparison.OrdinalIgnoreCase))
            {
                yield return fileNameWithoutExtension;
            }
        }

        if (TryCreateNonFileUri(workspacePath, out var uri))
        {
            var authority = GetReadableRemoteAuthority(uri.Authority);
            if (!string.IsNullOrWhiteSpace(authority))
            {
                yield return authority;
            }
        }
    }

    private static string? TryReadWorkspaceJson(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (TryReadWorkspaceLocation(root, "folder", out var folderPath))
            {
                return folderPath;
            }

            if (TryReadWorkspaceLocation(root, "workspace", out var workspacePath))
            {
                return workspacePath;
            }

            if (root.TryGetProperty("workspace", out var workspaceElement)
                && workspaceElement.ValueKind == JsonValueKind.Object
                && TryReadWorkspaceLocation(workspaceElement, "configPath", out var configPath))
            {
                return configPath;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        return null;
    }

    private static bool TryReadWorkspaceLocation(JsonElement element, string propertyName, out string? path)
    {
        path = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        path = ToWorkspaceLocation(property.GetString());
        return !string.IsNullOrWhiteSpace(path);
    }

    private static string? ToWorkspaceLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var localPath = uri.LocalPath;
            if (localPath.Length >= 3 && localPath[0] == '/' && char.IsLetter(localPath[1]) && localPath[2] == ':')
            {
                localPath = localPath[1..];
            }

            return localPath.Replace('/', Path.DirectorySeparatorChar);
        }

        if (TryCreateNonFileUri(value, out var nonFileUri))
        {
            return nonFileUri.AbsoluteUri;
        }

        return value;
    }

    private static string GetComparableWorkspacePath(string workspacePath)
    {
        if (!TryCreateNonFileUri(workspacePath, out var uri))
        {
            return workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var uriPath = Uri.UnescapeDataString(uri.AbsolutePath);
        return uriPath.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryCreateNonFileUri(string value, out NonFileUriInfo uri)
    {
        uri = default;
        if (IsWindowsPath(value) || value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var parsedUri)
            && parsedUri is not null
            && !string.IsNullOrWhiteSpace(parsedUri.Scheme)
            && !parsedUri.IsFile)
        {
            uri = new NonFileUriInfo(parsedUri.Scheme, parsedUri.Authority, parsedUri.AbsolutePath, parsedUri.AbsoluteUri);
            return true;
        }

        return TryParseUriParts(value, out uri)
            && !string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseUriParts(string value, out NonFileUriInfo uri)
    {
        uri = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var schemeSeparatorIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparatorIndex <= 0)
        {
            return false;
        }

        var scheme = value[..schemeSeparatorIndex];
        if (!IsValidUriScheme(scheme))
        {
            return false;
        }

        var remainder = value[(schemeSeparatorIndex + 3)..];
        var pathIndex = remainder.IndexOf('/');
        var authority = pathIndex >= 0 ? remainder[..pathIndex] : remainder;
        var absolutePath = pathIndex >= 0 ? remainder[pathIndex..] : "/";
        uri = new NonFileUriInfo(scheme, authority, absolutePath, value);
        return true;
    }

    private static bool IsValidUriScheme(string scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme) || !char.IsLetter(scheme[0]))
        {
            return false;
        }

        for (var index = 1; index < scheme.Length; index++)
        {
            var character = scheme[index];
            if (!char.IsLetterOrDigit(character)
                && character != '+'
                && character != '-'
                && character != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static string GetReadableRemoteAuthority(string authority)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return string.Empty;
        }

        var plusIndex = authority.IndexOf('+', StringComparison.Ordinal);
        return Uri.UnescapeDataString(plusIndex >= 0 && plusIndex < authority.Length - 1
            ? authority[(plusIndex + 1)..]
            : authority);
    }

    private static bool IsWindowsPath(string value)
    {
        return value.Length >= 3
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/');
    }

    private readonly record struct NonFileUriInfo(string Scheme, string Authority, string AbsolutePath, string AbsoluteUri);

    private readonly record struct WorkspacePathCandidate(string WorkspacePath, DateTime LastWriteTimeUtc);
}
