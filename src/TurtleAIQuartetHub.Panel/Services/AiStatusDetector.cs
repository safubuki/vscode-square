using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class AiStatusDetector
{
    private static readonly TimeSpan ErrorSignalWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CodexStreamQuietCompletionWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UiRunningObservationHoldWindow = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan CodexCarryForwardRunningWindow = TimeSpan.FromMinutes(30);
    private const int MaxRecentLogBytes = 96 * 1024;
    private const int MaxCodexCarryForwardLogBytes = 512 * 1024;
    private const int MaxCandidateLogFilesPerSource = 6;
    private static readonly string[] ExtensionHostDirectoryNames = ["exthost", "remoteexthost", "remoteexhost"];
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly VscodeChatUiStatusReader _uiStatusReader = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRunningSeenBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _completedAtBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _confirmationRequestedAtBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dismissedAtBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _slotStartedAtByName = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ExtensionLogSource[] LogSources =
    [
        new(
            "Codex",
            ["openai.chatgpt"],
            "Codex.log",
            "Conversation created",
            [],
            ["Activating Codex extension", "Initialize received", "method=client-status-changed"],
            [],
            [],
            CodexStreamQuietCompletionWindow,
            ["ephemeral-generation"],
            ["thread-stream-state-changed"],
            ["commandExecution/requestApproval"]),
        new(
            "Copilot",
            ["GitHub.copilot-chat", "github.copilot-chat"],
            "GitHub Copilot Chat.log",
            "ccreq:",
            [" | success |", " | cancelled |", " | unknown |", "request done:", "message 0 returned", "Stop hook result:"],
            ["Copilot Chat:", "Logged in as", "Got Copilot token"],
            ["Latest entry:", " | markdown", " | success |", " | cancelled |", " | networkError |"],
            [" | networkError |"],
            null,
            [],
            [],
            [])
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
            _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
            return uiEvidence;
        }

        if (uiEvidence is { Status: AiStatus.WaitingForConfirmation })
        {
            _confirmationRequestedAtBySlot[slotKey] = uiEvidence.EventAt ?? now;
            _completedAtBySlot.TryRemove(slotKey, out _);
            return uiEvidence;
        }

        var hadPreviousRunningState = _lastRunningSeenBySlot.TryGetValue(slotKey, out var lastRunningSeenAt);
        if (false && _lastRunningSeenBySlot.TryRemove(slotKey, out _))
        {
            _completedAtBySlot[slotKey] = now;
            return new AiStatusSnapshot(AiStatus.Completed, $"VS Code UI: {lastRunningSeenAt:HH:mm:ss} の実行中表示が終了しました。", now);
        }

        var userDataDirectory = SlotUserDataPaths.GetEffectiveUserDataDirectory(slot.Name, config);
        var canReadLogs = !string.IsNullOrWhiteSpace(userDataDirectory) && Directory.Exists(userDataDirectory);
        if (canReadLogs)
        {
            var evidences = LogSources
                .Select(source => ReadEvidence(userDataDirectory!, source))
                .Select(evidence => KeepOnlyCurrentEvidence(slotKey, slotStartedAt, evidence))
                .Where(evidence => evidence.Status is AiStatus.Running or AiStatus.Completed or AiStatus.Error or AiStatus.NeedsAttention or AiStatus.WaitingForConfirmation)
                .ToList();

            if (evidences.Count > 0)
            {
                var bestEvidence = GetBestEvidence(evidences);
                RememberDetectedState(slotKey, bestEvidence, now);
                return bestEvidence;
            }

            var codexContinuation = TryDetectCodexStreamContinuation(slotKey, slotStartedAt, userDataDirectory!);
            if (codexContinuation is not null)
            {
                RememberDetectedState(slotKey, codexContinuation, now);
                return codexContinuation;
            }
        }

        if (_confirmationRequestedAtBySlot.TryGetValue(slotKey, out var confirmationRequestedAt))
        {
            return new AiStatusSnapshot(AiStatus.WaitingForConfirmation, "VS Code UI: 直前のユーザー確認待ちを保持しています。", confirmationRequestedAt);
        }

        if (_completedAtBySlot.TryGetValue(slotKey, out var completedAt))
        {
            return new AiStatusSnapshot(AiStatus.Completed, "VS Code UI: 直前のAI実行は完了しました。", completedAt);
        }

        if (hadPreviousRunningState)
        {
            if (now - lastRunningSeenAt <= UiRunningObservationHoldWindow)
            {
                return new AiStatusSnapshot(AiStatus.Running, "VS Code UI: 直前の実行表示を短時間保持しています。", lastRunningSeenAt);
            }

            _lastRunningSeenBySlot.TryRemove(slotKey, out _);
            _completedAtBySlot[slotKey] = now;
            _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
            return new AiStatusSnapshot(AiStatus.Completed, $"VS Code UI: {lastRunningSeenAt:HH:mm:ss} の実行表示が消えました。", now);
        }

        if (!canReadLogs)
        {
            return new AiStatusSnapshot(AiStatus.Idle, "VS Code の user-data-dir が見つかりません。AI は待機中として扱います。", null);
        }

        return new AiStatusSnapshot(AiStatus.Idle, "AI は待機中です。", null);
    }

    public void Acknowledge(WindowSlot slot)
    {
        var slotKey = GetSlotKey(slot);
        _lastRunningSeenBySlot.TryRemove(slotKey, out _);
        _completedAtBySlot.TryRemove(slotKey, out _);
        _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
        _dismissedAtBySlot[slotKey] = DateTimeOffset.Now;
    }

    public void ResetSlotSession(WindowSlot slot)
    {
        ClearSlotState(slot);
        _slotStartedAtByName[slot.Name] = DateTimeOffset.Now;
    }

    public void SwapSlotSessions(string sourceSlotName, string targetSlotName)
    {
        SwapPrefixedEntries(_lastRunningSeenBySlot, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_completedAtBySlot, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_confirmationRequestedAtBySlot, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_dismissedAtBySlot, sourceSlotName, targetSlotName);

        var hasSource = _slotStartedAtByName.TryRemove(sourceSlotName, out var sourceStarted);
        var hasTarget = _slotStartedAtByName.TryRemove(targetSlotName, out var targetStarted);
        if (hasSource) _slotStartedAtByName[targetSlotName] = sourceStarted;
        if (hasTarget) _slotStartedAtByName[sourceSlotName] = targetStarted;
    }

    private static void SwapPrefixedEntries(ConcurrentDictionary<string, DateTimeOffset> dict, string slotNameA, string slotNameB)
    {
        var prefixA = $"{slotNameA}:";
        var prefixB = $"{slotNameB}:";

        var entriesA = dict.Where(kv => kv.Key.StartsWith(prefixA, StringComparison.OrdinalIgnoreCase)).ToList();
        var entriesB = dict.Where(kv => kv.Key.StartsWith(prefixB, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var entry in entriesA) dict.TryRemove(entry.Key, out _);
        foreach (var entry in entriesB) dict.TryRemove(entry.Key, out _);

        foreach (var entry in entriesA) dict[$"{slotNameB}:{entry.Key[prefixA.Length..]}"] = entry.Value;
        foreach (var entry in entriesB) dict[$"{slotNameA}:{entry.Key[prefixB.Length..]}"] = entry.Value;
    }

    private void RememberDetectedState(string slotKey, AiStatusSnapshot snapshot, DateTimeOffset fallbackAt)
    {
        var eventAt = snapshot.EventAt ?? fallbackAt;
        switch (snapshot.Status)
        {
            case AiStatus.Running:
                _lastRunningSeenBySlot[slotKey] = eventAt;
                _completedAtBySlot.TryRemove(slotKey, out _);
                _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
                break;
            case AiStatus.WaitingForConfirmation:
                _confirmationRequestedAtBySlot[slotKey] = eventAt;
                _completedAtBySlot.TryRemove(slotKey, out _);
                break;
            case AiStatus.Completed:
                _lastRunningSeenBySlot.TryRemove(slotKey, out _);
                _completedAtBySlot[slotKey] = eventAt;
                _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
                break;
            case AiStatus.Error:
            case AiStatus.NeedsAttention:
            case AiStatus.Idle:
                _lastRunningSeenBySlot.TryRemove(slotKey, out _);
                _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
                break;
        }
    }

    private AiStatusSnapshot KeepOnlyCurrentEvidence(
        string slotKey,
        DateTimeOffset slotStartedAt,
        AiStatusSnapshot evidence)
    {
        if (!evidence.EventAt.HasValue)
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

        if (evidence.Status == AiStatus.Running)
        {
            _completedAtBySlot.TryRemove(slotKey, out _);
        }
        else if (evidence.Status == AiStatus.Completed)
        {
            _completedAtBySlot[slotKey] = eventAt;
        }

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
        foreach (var key in _confirmationRequestedAtBySlot.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _confirmationRequestedAtBySlot.TryRemove(key, out _);
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
            AiStatus.WaitingForConfirmation => 6,
            AiStatus.Running => 5,
            AiStatus.NeedsAttention => 3,
            AiStatus.Error => 2,
            AiStatus.Completed => 1,
            AiStatus.Idle => 0,
            _ => -1
        };
    }

    private static AiStatusSnapshot ReadEvidence(string userDataDirectory, ExtensionLogSource source)
    {
        return ToSnapshot(ReadLatestEvidence(userDataDirectory, source));
    }

    private AiStatusSnapshot? TryDetectCodexStreamContinuation(string slotKey, DateTimeOffset slotStartedAt, string userDataDirectory)
    {
        var source = LogSources.FirstOrDefault(candidate => string.Equals(candidate.DisplayName, "Codex", StringComparison.Ordinal));
        if (source is null)
        {
            return null;
        }

        var recentEvidence = ReadLatestEvidence(userDataDirectory, source);
        if (!_lastRunningSeenBySlot.TryGetValue(slotKey, out var lastRunningSeenAt))
        {
            return TryCarryForwardCodexFromCurrentSession(source, userDataDirectory, recentEvidence);
        }

        if (!recentEvidence.LastEventAt.HasValue
            || recentEvidence.LastEventAt.Value < _startedAt
            || recentEvidence.LastEventAt.Value < slotStartedAt
            || recentEvidence.LastEventAt.Value <= lastRunningSeenAt)
        {
            return null;
        }

        if (recentEvidence.LastCompletionSignalAt is { } completedAt && completedAt >= lastRunningSeenAt)
        {
            _completedAtBySlot[slotKey] = completedAt;
            return new AiStatusSnapshot(AiStatus.Completed, $"{source.DisplayName}: {completedAt:HH:mm:ss} に完了イベントを検出しました。", completedAt);
        }

        if (recentEvidence.LastConfirmationSignalAt is { } confirmAt
            && confirmAt >= lastRunningSeenAt)
        {
            return new AiStatusSnapshot(AiStatus.WaitingForConfirmation, $"{source.DisplayName}: {confirmAt:HH:mm:ss} にユーザー確認待ちを検出しました。", confirmAt);
        }

        if (recentEvidence.RunningSignalQuietCompletionWindow is not { } quietWindow
            || recentEvidence.LastActivitySignalAt is not { } activityAt
            || activityAt <= lastRunningSeenAt)
        {
            return null;
        }

        if (DateTimeOffset.Now - activityAt <= quietWindow)
        {
            return new AiStatusSnapshot(AiStatus.Running, $"{source.DisplayName}: {activityAt:HH:mm:ss} にストリーム更新を検出しました。", activityAt, quietWindow);
        }

        _completedAtBySlot[slotKey] = activityAt;
        return new AiStatusSnapshot(AiStatus.Completed, $"{source.DisplayName}: {activityAt:HH:mm:ss} 以降のストリーム停止を検出しました。", activityAt, quietWindow);
    }

    private static AiStatusSnapshot? TryCarryForwardCodexFromCurrentSession(
        ExtensionLogSource source,
        string userDataDirectory,
        AiLogEvidence recentEvidence)
    {
        if (recentEvidence.RunningSignalQuietCompletionWindow is not { } quietWindow
            || recentEvidence.LastActivitySignalAt is not { } activityAt
            || DateTimeOffset.Now - activityAt > quietWindow)
        {
            return null;
        }

        var carryForwardEvidence = ReadLatestEvidence(userDataDirectory, source, MaxCodexCarryForwardLogBytes);
        var runningAt = GetEffectiveRunningSignalAt(carryForwardEvidence);
        if (!runningAt.HasValue
            || DateTimeOffset.Now - runningAt.Value > CodexCarryForwardRunningWindow
            || activityAt < runningAt.Value)
        {
            return null;
        }

        if (carryForwardEvidence.LastCompletionSignalAt is { } completedAt && completedAt >= runningAt.Value)
        {
            return null;
        }

        if (carryForwardEvidence.LastConfirmationSignalAt is { } confirmAt && confirmAt >= runningAt.Value)
        {
            return new AiStatusSnapshot(AiStatus.WaitingForConfirmation, $"{source.DisplayName}: approval requested at {confirmAt:HH:mm:ss}.", confirmAt);
        }

        return new AiStatusSnapshot(AiStatus.Running, $"{source.DisplayName}: carried forward from current session activity at {activityAt:HH:mm:ss}.", activityAt, quietWindow);
    }

    private static AiLogEvidence ReadLatestEvidence(string userDataDirectory, ExtensionLogSource source, int maxRecentLogBytes = MaxRecentLogBytes)
    {
        var newestEvidence = AiLogEvidence.Empty(source);

        foreach (var logPath in EnumerateCandidateLogFiles(userDataDirectory, source)
                     .Select(TryGetLogFileInfo)
                     .Where(fileInfo => fileInfo is not null)
                     .Cast<FileInfo>()
                     .OrderByDescending(fileInfo => fileInfo.LastWriteTimeUtc)
                     .Take(MaxCandidateLogFilesPerSource)
                     .Select(fileInfo => fileInfo.FullName))
        {
            var evidence = ReadLogEvidence(logPath, source, maxRecentLogBytes);
            if (evidence.LastEventAt.HasValue
                && (!newestEvidence.LastEventAt.HasValue || evidence.LastEventAt.Value > newestEvidence.LastEventAt.Value))
            {
                newestEvidence = evidence;
            }
        }

        return newestEvidence;
    }

    private static FileInfo? TryGetLogFileInfo(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists ? fileInfo : null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            return null;
        }
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
                foreach (var logPath in EnumerateExtensionHostLogFiles(window.FullName, source))
                {
                    yield return logPath;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateExtensionHostLogFiles(string windowDirectory, ExtensionLogSource source)
    {
        var yieldedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hostDirectoryName in ExtensionHostDirectoryNames)
        {
            foreach (var logPath in EnumerateHostLogFiles(Path.Combine(windowDirectory, hostDirectoryName), source))
            {
                if (yieldedPaths.Add(logPath))
                {
                    yield return logPath;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateHostLogFiles(string hostDirectory, ExtensionLogSource source)
    {
        if (!Directory.Exists(hostDirectory))
        {
            yield break;
        }

        foreach (var logPath in EnumerateDirectLogFiles(hostDirectory, source))
        {
            yield return logPath;
        }

        HashSet<string> extensionDirectoryNames = new(source.ExtensionDirectoryNames, StringComparer.OrdinalIgnoreCase);
        List<DirectoryInfo> nestedDirectories;
        try
        {
            nestedDirectories = new DirectoryInfo(hostDirectory)
                .EnumerateDirectories()
                .Where(directory => !extensionDirectoryNames.Contains(directory.Name))
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .Take(12)
                .ToList();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            yield break;
        }

        foreach (var nestedDirectory in nestedDirectories)
        {
            foreach (var logPath in EnumerateDirectLogFiles(nestedDirectory.FullName, source))
            {
                yield return logPath;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectLogFiles(string parentDirectory, ExtensionLogSource source)
    {
        foreach (var extensionDirectoryName in source.ExtensionDirectoryNames)
        {
            var logPath = Path.Combine(parentDirectory, extensionDirectoryName, source.LogFileName);
            if (File.Exists(logPath))
            {
                yield return logPath;
            }
        }
    }

    private static AiLogEvidence ReadLogEvidence(string logPath, ExtensionLogSource source, int maxRecentLogBytes)
    {
        var evidence = AiLogEvidence.Empty(source);

        try
        {
            foreach (var line in ReadRecentLines(logPath, maxRecentLogBytes))
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

                if (source.SecondaryRunningSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastSecondaryRunningSignalAt = Max(evidence.LastSecondaryRunningSignalAt, timestamp) };
                    continue;
                }

                if (source.ActivitySignals.Length > 0
                    && source.ActivitySignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastActivitySignalAt = Max(evidence.LastActivitySignalAt, timestamp) };
                    continue;
                }

                if (source.ConfirmationSignals.Length > 0
                    && source.ConfirmationSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastConfirmationSignalAt = Max(evidence.LastConfirmationSignalAt, timestamp) };
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

    private static IEnumerable<string> ReadRecentLines(string path, int maxRecentLogBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytesToRead = (int)Math.Min(stream.Length, maxRecentLogBytes);
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

        var hasPrimaryRunning = evidence.LastRunningSignalAt.HasValue;
        var hasSecondaryRunning = evidence.LastSecondaryRunningSignalAt.HasValue;

        DateTimeOffset? effectiveRunningAt = null;
        if (hasPrimaryRunning)
        {
            effectiveRunningAt = evidence.LastRunningSignalAt;
        }

        if (hasSecondaryRunning)
        {
            effectiveRunningAt = Max(effectiveRunningAt, evidence.LastSecondaryRunningSignalAt!.Value);
        }

        if (effectiveRunningAt is { } runningAt)
        {
            var completionIsNewer = evidence.LastCompletionSignalAt.HasValue
                && evidence.LastCompletionSignalAt.Value >= runningAt;

            if (completionIsNewer && evidence.LastCompletionSignalAt is { } completedAt)
            {
                return new AiStatusSnapshot(AiStatus.Completed, $"{evidence.SourceName}: {completedAt:HH:mm:ss} に完了イベントを検出しました。", completedAt);
            }

            if (evidence.LastConfirmationSignalAt is { } confirmAt && confirmAt >= runningAt)
            {
                return new AiStatusSnapshot(AiStatus.WaitingForConfirmation, $"{evidence.SourceName}: {confirmAt:HH:mm:ss} にユーザー確認待ちを検出しました。", confirmAt);
            }

            if (evidence.RunningSignalQuietCompletionWindow is { } quietWindow)
            {
                var lastActiveAt = runningAt;
                if (evidence.LastActivitySignalAt is { } activityAt && activityAt > lastActiveAt)
                {
                    lastActiveAt = activityAt;
                }

                if (now - lastActiveAt > quietWindow)
                {
                    return new AiStatusSnapshot(AiStatus.Completed, $"{evidence.SourceName}: {lastActiveAt:HH:mm:ss} 以降のストリーム停止を検出しました。", lastActiveAt, quietWindow);
                }
            }

            return new AiStatusSnapshot(AiStatus.Running, $"{evidence.SourceName}: {runningAt:HH:mm:ss} に実行イベントを検出しました。", runningAt, evidence.RunningSignalQuietCompletionWindow);
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

    private static DateTimeOffset? GetEffectiveRunningSignalAt(AiLogEvidence evidence)
    {
        DateTimeOffset? runningAt = null;
        if (evidence.LastRunningSignalAt.HasValue)
        {
            runningAt = evidence.LastRunningSignalAt.Value;
        }

        if (evidence.LastSecondaryRunningSignalAt.HasValue)
        {
            runningAt = Max(runningAt, evidence.LastSecondaryRunningSignalAt.Value);
        }

        return runningAt;
    }

    private sealed record ExtensionLogSource(
        string DisplayName,
        string[] ExtensionDirectoryNames,
        string LogFileName,
        string RunningSignal,
        string[] CompletionSignals,
        string[] IdleSignals,
        string[] IgnoredRunningSignals,
        string[] ErrorSignals,
        TimeSpan? RunningSignalQuietCompletionWindow,
        string[] SecondaryRunningSignals,
        string[] ActivitySignals,
        string[] ConfirmationSignals);

    private readonly record struct AiLogEvidence(
        string SourceName,
        TimeSpan? RunningSignalQuietCompletionWindow,
        DateTimeOffset? LastEventAt,
        DateTimeOffset? LastRunningSignalAt,
        DateTimeOffset? LastCompletionSignalAt,
        DateTimeOffset? LastErrorSignalAt,
        DateTimeOffset? LastIdleSignalAt,
        DateTimeOffset? LastSecondaryRunningSignalAt,
        DateTimeOffset? LastActivitySignalAt,
        DateTimeOffset? LastConfirmationSignalAt)
    {
        public static AiLogEvidence Empty(ExtensionLogSource source)
        {
            return new AiLogEvidence(
                source.DisplayName,
                source.RunningSignalQuietCompletionWindow,
                null, null, null, null, null, null, null, null);
        }
    }
}

public sealed record AiStatusSnapshot(
    AiStatus Status,
    string Detail,
    DateTimeOffset? EventAt,
    TimeSpan? RunningSignalQuietCompletionWindow = null);
