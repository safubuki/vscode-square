using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using VscodeSquare.Panel.Models;

namespace VscodeSquare.Panel.Services;

public sealed class AiStatusDetector
{
    private static readonly TimeSpan ErrorSignalWindow = TimeSpan.FromMinutes(3);
    private const int MaxRecentLogBytes = 96 * 1024;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly VscodeChatUiStatusReader _uiStatusReader = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRunningSeenBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _completedAtBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dismissedAtBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _slotStartedAtByName = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ExtensionLogSource[] LogSources =
    [
        new(
            "Codex",
            ["openai.chatgpt"],
            "Codex.log",
            "method=thread-stream-state-changed",
            ["method=thread-read-state-changed"],
            ["Activating Codex extension", "Initialize received", "method=client-status-changed"],
            [],
            []),
        new(
            "Copilot",
            ["GitHub.copilot-chat", "github.copilot-chat"],
            "GitHub Copilot Chat.log",
            "ccreq:",
            [" | success |", " | cancelled |", " | unknown |", "request done:", "message 0 returned", "Stop hook result:"],
            ["Copilot Chat:", "Logged in as", "Got Copilot token"],
            ["Latest entry:", " | markdown", " | success |", " | cancelled |", " | networkError |"],
            [" | networkError |"])
    ];

    public AiStatusSnapshot Detect(WindowSlot slot, AppConfig config)
    {
        return Detect(
            new WindowSlotStatusSnapshot(
                slot.Name,
                slot.WindowHandle,
                slot.WindowTitle,
                slot.CurrentWorkspacePath,
                null),
            config);
    }

    internal AiStatusSnapshot Detect(WindowSlotStatusSnapshot slot, AppConfig config)
    {
        if (slot.WindowHandle == IntPtr.Zero)
        {
            ClearSlotState(slot);
            return new AiStatusSnapshot(AiStatus.Idle, "VS Code は起動していません。", null);
        }

        var slotKey = GetSlotKey(slot);
        var slotStartedAt = GetSlotStartedAt(slot);
        var now = DateTimeOffset.Now;
        var uiEvidence = _uiStatusReader.TryRead(slot);
        if (uiEvidence is { Status: AiStatus.Running })
        {
            _lastRunningSeenBySlot[slotKey] = now;
            _completedAtBySlot.TryRemove(slotKey, out _);
            return uiEvidence;
        }

        if (_lastRunningSeenBySlot.TryRemove(slotKey, out var lastRunningSeenAt))
        {
            _completedAtBySlot[slotKey] = now;
            return new AiStatusSnapshot(AiStatus.Completed, $"VS Code UI: {lastRunningSeenAt:HH:mm:ss} の実行中表示が終了しました。", now);
        }

        if (_completedAtBySlot.TryGetValue(slotKey, out var completedAt))
        {
            return new AiStatusSnapshot(AiStatus.Completed, "VS Code UI: 直前のAI実行は完了しました。", completedAt);
        }

        var userDataDirectory = SlotUserDataPaths.GetEffectiveUserDataDirectory(slot.Name, config);
        if (string.IsNullOrWhiteSpace(userDataDirectory) || !Directory.Exists(userDataDirectory))
        {
            return new AiStatusSnapshot(AiStatus.Idle, "VS Code の user-data-dir が見つかりません。AI は待機中として扱います。", null);
        }

        var evidences = LogSources
            .Select(source => ReadEvidence(userDataDirectory, source))
            .Select(evidence => KeepOnlyCurrentEvidence(slotKey, slotStartedAt, evidence))
            .Where(evidence => evidence.Status is AiStatus.Completed or AiStatus.Error or AiStatus.NeedsAttention or AiStatus.WaitingForConfirmation)
            .ToList();

        if (evidences.Count == 0)
        {
            return new AiStatusSnapshot(AiStatus.Idle, "AI は待機中です。", null);
        }

        return GetBestEvidence(evidences);
    }

    public void Acknowledge(WindowSlot slot)
    {
        var slotKey = GetSlotKey(slot);
        _lastRunningSeenBySlot.TryRemove(slotKey, out _);
        _completedAtBySlot.TryRemove(slotKey, out _);
        _dismissedAtBySlot[slotKey] = DateTimeOffset.Now;
    }

    public void ResetSlotSession(WindowSlot slot)
    {
        ClearSlotState(slot);
        _slotStartedAtByName[slot.Name] = DateTimeOffset.Now;
    }

    private AiStatusSnapshot KeepOnlyCurrentEvidence(string slotKey, DateTimeOffset slotStartedAt, AiStatusSnapshot evidence)
    {
        if (evidence.Status != AiStatus.Completed || !evidence.EventAt.HasValue)
        {
            return evidence;
        }

        var eventAt = evidence.EventAt.Value;
        if (eventAt < _startedAt || eventAt < slotStartedAt)
        {
            return new AiStatusSnapshot(AiStatus.Idle, "AI は待機中です。", eventAt);
        }

        if (_dismissedAtBySlot.TryGetValue(slotKey, out var dismissedAt) && eventAt <= dismissedAt)
        {
            return new AiStatusSnapshot(AiStatus.Idle, "AI は待機中です。", eventAt);
        }

        _completedAtBySlot[slotKey] = eventAt;
        return evidence;
    }

    private static AiStatusSnapshot GetBestEvidence(IEnumerable<AiStatusSnapshot> evidences)
    {
        return evidences
            .OrderByDescending(GetStatusPriority)
            .ThenByDescending(evidence => evidence.EventAt ?? DateTimeOffset.MinValue)
            .First();
    }

    private static string GetSlotKey(WindowSlot slot)
    {
        return $"{slot.Name}:{slot.WindowHandle.ToInt64()}";
    }

    private static string GetSlotKey(WindowSlotStatusSnapshot slot)
    {
        return $"{slot.Name}:{slot.WindowHandle.ToInt64()}";
    }

    private DateTimeOffset GetSlotStartedAt(WindowSlotStatusSnapshot slot)
    {
        return _slotStartedAtByName.GetOrAdd(slot.Name, _startedAt);
    }

    private void ClearSlotState(WindowSlot slot)
    {
        ClearSlotState(slot.Name);
    }

    private void ClearSlotState(WindowSlotStatusSnapshot slot)
    {
        ClearSlotState(slot.Name);
    }

    private void ClearSlotState(string slotName)
    {
        var prefix = $"{slotName}:";
        foreach (var key in _lastRunningSeenBySlot.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _lastRunningSeenBySlot.TryRemove(key, out _);
        }

        foreach (var key in _completedAtBySlot.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _completedAtBySlot.TryRemove(key, out _);
        }

        foreach (var key in _dismissedAtBySlot.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _dismissedAtBySlot.TryRemove(key, out _);
        }
    }

    private static int GetStatusPriority(AiStatusSnapshot evidence)
    {
        return evidence.Status switch
        {
            AiStatus.Running => 5,
            AiStatus.WaitingForConfirmation => 4,
            AiStatus.NeedsAttention => 3,
            AiStatus.Error => 2,
            AiStatus.Completed => 1,
            AiStatus.Idle => 0,
            _ => -1
        };
    }

    private static AiStatusSnapshot ReadEvidence(string userDataDirectory, ExtensionLogSource source)
    {
        var newestEvidence = AiLogEvidence.Empty(source.DisplayName);

        foreach (var logPath in EnumerateCandidateLogFiles(userDataDirectory, source))
        {
            var evidence = ReadLogEvidence(logPath, source);
            if (evidence.LastEventAt > newestEvidence.LastEventAt)
            {
                newestEvidence = evidence;
            }
        }

        return ToSnapshot(newestEvidence);
    }

    private static IEnumerable<string> EnumerateCandidateLogFiles(string userDataDirectory, ExtensionLogSource source)
    {
        var logsDirectory = Path.Combine(userDataDirectory, "logs");
        if (!Directory.Exists(logsDirectory))
        {
            yield break;
        }

        List<DirectoryInfo> sessions;
        try
        {
            sessions = new DirectoryInfo(logsDirectory)
                .EnumerateDirectories()
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .Take(8)
                .ToList();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            yield break;
        }

        foreach (var session in sessions)
        {
            IEnumerable<DirectoryInfo> windows;
            try
            {
                windows = session.EnumerateDirectories("window*").ToList();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
                continue;
            }

            foreach (var window in windows)
            {
                foreach (var extensionDirectoryName in source.ExtensionDirectoryNames)
                {
                    var logPath = Path.Combine(window.FullName, "exthost", extensionDirectoryName, source.LogFileName);
                    if (File.Exists(logPath))
                    {
                        yield return logPath;
                    }
                }
            }
        }
    }

    private static AiLogEvidence ReadLogEvidence(string logPath, ExtensionLogSource source)
    {
        var evidence = AiLogEvidence.Empty(source.DisplayName);

        try
        {
            foreach (var line in ReadRecentLines(logPath))
            {
                if (!TryParseLogTimestamp(line, out var timestamp))
                {
                    continue;
                }

                evidence = evidence with { LastEventAt = Max(evidence.LastEventAt, timestamp) };

                if (source.ErrorSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastErrorSignalAt = Max(evidence.LastErrorSignalAt, timestamp) };
                    continue;
                }

                if (source.CompletionSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastCompletionSignalAt = Max(evidence.LastCompletionSignalAt, timestamp) };
                    continue;
                }

                if (line.Contains(source.RunningSignal, StringComparison.OrdinalIgnoreCase)
                    && !source.IgnoredRunningSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastRunningSignalAt = Max(evidence.LastRunningSignalAt, timestamp) };
                    continue;
                }

                if (source.IdleSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastIdleSignalAt = Max(evidence.LastIdleSignalAt, timestamp) };
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        return evidence;
    }

    private static IEnumerable<string> ReadRecentLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytesToRead = (int)Math.Min(stream.Length, MaxRecentLogBytes);
        if (bytesToRead <= 0)
        {
            yield break;
        }

        stream.Seek(-bytesToRead, SeekOrigin.End);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        if (stream.Position > 0)
        {
            _ = reader.ReadLine();
        }

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static bool TryParseLogTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (line.Length < 23)
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                line[..23],
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var localTime))
        {
            return false;
        }

        timestamp = new DateTimeOffset(localTime);
        return true;
    }

    private static AiStatusSnapshot ToSnapshot(AiLogEvidence evidence)
    {
        var now = DateTimeOffset.Now;

        if (evidence.LastErrorSignalAt is { } errorAt
            && now - errorAt <= ErrorSignalWindow
            && (!evidence.LastRunningSignalAt.HasValue || errorAt >= evidence.LastRunningSignalAt.Value)
            && (!evidence.LastCompletionSignalAt.HasValue || errorAt >= evidence.LastCompletionSignalAt.Value))
        {
            return new AiStatusSnapshot(AiStatus.Error, $"{evidence.SourceName}: {errorAt:HH:mm:ss} にエラーイベントを検出しました。", errorAt);
        }

        if (evidence.LastRunningSignalAt is { } runningAt)
        {
            var completionIsNewer = evidence.LastCompletionSignalAt.HasValue
                && evidence.LastCompletionSignalAt.Value >= runningAt;

            if (completionIsNewer && evidence.LastCompletionSignalAt is { } completedAt)
            {
                return new AiStatusSnapshot(AiStatus.Completed, $"{evidence.SourceName}: {completedAt:HH:mm:ss} に完了イベントを検出しました。", completedAt);
            }
        }

        if (evidence.LastCompletionSignalAt is { } standaloneCompletedAt)
        {
            return new AiStatusSnapshot(AiStatus.Completed, $"{evidence.SourceName}: {standaloneCompletedAt:HH:mm:ss} に完了イベントを検出しました。", standaloneCompletedAt);
        }

        return new AiStatusSnapshot(AiStatus.Idle, $"{evidence.SourceName}: AI は待機中です。", evidence.LastIdleSignalAt ?? evidence.LastEventAt);
    }

    private static DateTimeOffset? Max(DateTimeOffset? current, DateTimeOffset candidate)
    {
        return !current.HasValue || candidate > current.Value ? candidate : current;
    }

    private sealed record ExtensionLogSource(
        string DisplayName,
        string[] ExtensionDirectoryNames,
        string LogFileName,
        string RunningSignal,
        string[] CompletionSignals,
        string[] IdleSignals,
        string[] IgnoredRunningSignals,
        string[] ErrorSignals);

    private readonly record struct AiLogEvidence(
        string SourceName,
        DateTimeOffset? LastEventAt,
        DateTimeOffset? LastRunningSignalAt,
        DateTimeOffset? LastCompletionSignalAt,
        DateTimeOffset? LastErrorSignalAt,
        DateTimeOffset? LastIdleSignalAt)
    {
        public static AiLogEvidence Empty(string sourceName)
        {
            return new AiLogEvidence(sourceName, null, null, null, null, null);
        }
    }
}

public sealed record AiStatusSnapshot(AiStatus Status, string Detail, DateTimeOffset? EventAt);
