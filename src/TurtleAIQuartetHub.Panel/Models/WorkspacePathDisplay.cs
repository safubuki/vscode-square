using System.IO;

namespace TurtleAIQuartetHub.Panel.Models;

public static class WorkspacePathDisplay
{
    public static string GetShortPath(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return "-";
        }

        var trimmed = workspacePath.Trim();
        if (TryFormatSshRemote(trimmed, out var remoteDisplay))
        {
            return remoteDisplay;
        }

        var comparablePath = GetComparableWorkspacePath(trimmed);
        var fileName = Path.GetFileName(comparablePath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar,
            '/'));
        return string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
    }

    private static string GetComparableWorkspacePath(string workspacePath)
    {
        if (!TryCreateNonFileUri(workspacePath, out var uri))
        {
            return workspacePath;
        }

        var uriPath = Uri.UnescapeDataString(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(uriPath)
            ? workspacePath
            : uriPath.Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool TryFormatSshRemote(string workspacePath, out string remoteDisplay)
    {
        remoteDisplay = string.Empty;
        if (!TryCreateNonFileUri(workspacePath, out var uri)
            || !string.Equals(uri.Scheme, "vscode-remote", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var authority = Uri.UnescapeDataString(uri.Authority).Trim();
        const string sshRemotePrefix = "ssh-remote+";
        if (!authority.StartsWith(sshRemotePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = authority[sshRemotePrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var workspaceName = GetRemoteWorkspaceName(uri.AbsolutePath);
        remoteDisplay = string.IsNullOrWhiteSpace(workspaceName)
            ? $"ssh@{host}"
            : $"ssh@{host}-{workspaceName}";
        return true;
    }

    private static string GetRemoteWorkspaceName(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || absolutePath == "/")
        {
            return string.Empty;
        }

        var decodedPath = Uri.UnescapeDataString(absolutePath);
        var name = decodedPath
            .TrimEnd('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return name.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name)
            : name;
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
        var schemeSeparatorIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparatorIndex <= 0)
        {
            return false;
        }

        var scheme = value[..schemeSeparatorIndex];
        if (string.IsNullOrWhiteSpace(scheme) || !char.IsLetter(scheme[0]))
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

    private static bool IsWindowsPath(string value)
    {
        return value.Length >= 3
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/');
    }

    private readonly record struct NonFileUriInfo(string Scheme, string Authority, string AbsolutePath, string AbsoluteUri);
}
