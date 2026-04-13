using System.IO;

namespace VscodeSquare.Panel.Services;

public static class DiagnosticLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VscodeSquare",
        "panel.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the panel UI.
        }
    }

    public static void Write(Exception exception)
    {
        Write(exception.ToString());
    }
}

