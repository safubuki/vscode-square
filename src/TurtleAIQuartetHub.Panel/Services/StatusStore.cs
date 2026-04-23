using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class StatusStore : INotifyPropertyChanged
{
    private static readonly TimeSpan WorkspaceRefreshInterval = TimeSpan.FromSeconds(4);
    private const int StoredPanelsPerPage = 4;
    private const int StoredPanelPageCount = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly record struct PanelSnapshot(string PanelTitle, string WorkspacePath)
    {
        public bool HasContent => !string.IsNullOrWhiteSpace(PanelTitle) || !string.IsNullOrWhiteSpace(WorkspacePath);
    }

    private string _message;
    private bool _suppressPersistence;
    private readonly Dictionary<string, DateTimeOffset> _workspaceRefreshTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly AiStatusDetector _aiStatusDetector = new();

    public StatusStore(AppConfig config)
    {
        Config = config;
        Slots = new ObservableCollection<WindowSlot>(config.Slots.Select(slot => new WindowSlot(slot)));
        StoredPanels = new ObservableCollection<StoredPanelSlot>(
            Enumerable.Range(1, StoredPanelsPerPage * StoredPanelPageCount).Select(index => new StoredPanelSlot(index)));
        StoredPanelPages = new ObservableCollection<StoredPanelPage>(
            Enumerable.Range(0, StoredPanelPageCount)
                .Select(pageIndex => new StoredPanelPage(
                    pageIndex + 1,
                    StoredPanels.Skip(pageIndex * StoredPanelsPerPage).Take(StoredPanelsPerPage))));
        LoadSavedPanelStates();
        foreach (var slot in Slots)
        {
            slot.PropertyChanged += Slot_PropertyChanged;
        }

        foreach (var storedPanel in StoredPanels)
        {
            storedPanel.PropertyChanged += StoredPanel_PropertyChanged;
        }

        _message = $"設定を読み込みました: {config.ConfigSource}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppConfig Config { get; }

    public ObservableCollection<WindowSlot> Slots { get; }

    public ObservableCollection<StoredPanelSlot> StoredPanels { get; }

    public ObservableCollection<StoredPanelPage> StoredPanelPages { get; }

    public string Message
    {
        get => _message;
        set
        {
            if (_message == value)
            {
                return;
            }

            _message = value;
            DiagnosticLog.Write(value);
            OnPropertyChanged();
        }
    }

    public void AssignWindow(WindowSlot slot, WindowInfo window)
    {
        slot.WindowHandle = window.Handle;
        slot.WindowTitle = window.Title;
        slot.CurrentWorkspacePath = string.Empty;
        slot.WindowStatus = SlotWindowStatus.Ready;
        slot.AiStatus = AiStatus.Idle;
        slot.AiStatusDetail = "AI は待機中です。";
        _aiStatusDetector.ResetSlotSession(slot);
        slot.LastEventAt = DateTimeOffset.Now;
        slot.WindowLayerMode = WindowSlot.SlotWindowLayerMode.Topmost;

        if (ShouldAutoAssignWorkspaceTitle(slot))
        {
            var preferredTitle = !string.IsNullOrWhiteSpace(slot.Path)
                ? GetBaseTitleFromWorkspacePath(slot.Path)
                : $"スロット{slot.Name}";
            slot.PanelTitle = MakeUniquePanelTitle(preferredTitle, slot);
        }

        SavePanelStates();
    }

    public void ClearWindow(WindowSlot slot)
    {
        _workspaceRefreshTimestamps.Remove(slot.Name);
        _aiStatusDetector.ResetSlotSession(slot);
        slot.ClearWindow();
        SavePanelStates();
    }

    public void AcknowledgeAiStatus(WindowSlot slot)
    {
        if (slot.AiStatus != AiStatus.Completed)
        {
            return;
        }

        _aiStatusDetector.Acknowledge(slot);
        slot.AiStatus = AiStatus.Idle;
        slot.AiStatusDetail = "AI は待機中です。";
    }

    public void SetFocusedSlot(WindowSlot focusedSlot)
    {
        foreach (var slot in Slots)
        {
            slot.IsFocused = ReferenceEquals(slot, focusedSlot);
        }
    }

    public void ClearFocusedSlot()
    {
        foreach (var slot in Slots)
        {
            slot.IsFocused = false;
        }
    }

    public bool UpdatePreferredLayout(WindowSlot slot, VscodeLayoutPreference preference)
    {
        if (!preference.HasAnyValue || Equals(slot.PreferredLayout, preference))
        {
            return false;
        }

        slot.PreferredLayout = preference;
        SavePanelStates();
        return true;
    }

    public void CaptureWorkspacePaths()
    {
        foreach (var slot in Slots)
        {
            CaptureWorkspacePath(slot);
        }

        SavePanelStates();
    }

    public void CaptureWorkspacePath(WindowSlot slot)
    {
        if (slot.WindowHandle == IntPtr.Zero)
        {
            return;
        }

        var workspacePath = !string.IsNullOrWhiteSpace(slot.CurrentWorkspacePath)
            ? slot.CurrentWorkspacePath
            : VscodeWorkspaceState.TryReadCurrentWorkspacePath(slot, Config);
        _workspaceRefreshTimestamps[slot.Name] = DateTimeOffset.UtcNow;
        slot.CurrentWorkspacePath = workspacePath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            slot.Path = workspacePath;
            slot.SavedWorkspacePath = workspacePath;
            slot.SavedWorkspaceConfirmed = true;

            if (ShouldAutoAssignWorkspaceTitle(slot))
            {
                slot.PanelTitle = MakeUniquePanelTitle(GetBaseTitleFromWorkspacePath(workspacePath), slot);
            }

            return;
        }

        slot.SavedWorkspaceConfirmed = false;
        slot.SavedWorkspacePath = string.Empty;
    }

    public void LoadSavedSettings()
    {
        LoadSavedPanelStates();
    }

    public void SaveCurrentSettings()
    {
        CaptureWorkspacePaths();
    }

    public WindowSlot? FindSlot(string slotName)
    {
        return Slots.FirstOrDefault(slot => string.Equals(slot.Name, slotName, StringComparison.OrdinalIgnoreCase));
    }

    public bool TryStoreSlotInBack(WindowSlot slot, out StoredPanelSlot? storedPanel)
    {
        storedPanel = StoredPanels.FirstOrDefault(item => !item.HasContent);
        if (storedPanel is null)
        {
            return false;
        }

        var snapshot = CreateSnapshot(slot);
        if (!snapshot.HasContent)
        {
            storedPanel = null;
            return false;
        }

        storedPanel.LoadFrom(snapshot.PanelTitle, snapshot.WorkspacePath);
        slot.ClearAssignedPanel();
        slot.ClearWindow();
        SavePanelStates();
        return true;
    }

    public bool TryShowStoredPanel(StoredPanelSlot storedPanel, WindowSlot targetSlot, out bool swappedVisiblePanel)
    {
        swappedVisiblePanel = false;

        var storedSnapshot = CreateSnapshot(storedPanel);
        if (!storedSnapshot.HasContent)
        {
            return false;
        }

        var visibleSnapshot = CreateSnapshot(targetSlot);

        if (visibleSnapshot.HasContent)
        {
            storedPanel.LoadFrom(visibleSnapshot.PanelTitle, visibleSnapshot.WorkspacePath);
            swappedVisiblePanel = true;
        }
        else
        {
            storedPanel.Clear();
        }

        targetSlot.ClearWindow();
        targetSlot.ApplyAssignedPanel(storedSnapshot.PanelTitle, storedSnapshot.WorkspacePath);
        if (ShouldAutoAssignWorkspaceTitle(targetSlot))
        {
            var preferredTitle = !string.IsNullOrWhiteSpace(storedSnapshot.PanelTitle)
                ? storedSnapshot.PanelTitle
                : !string.IsNullOrWhiteSpace(storedSnapshot.WorkspacePath)
                    ? GetBaseTitleFromWorkspacePath(storedSnapshot.WorkspacePath)
                    : $"スロット{targetSlot.Name}";

            targetSlot.PanelTitle = MakeUniquePanelTitle(preferredTitle, targetSlot);
        }

        SavePanelStates();
        return true;
    }

    public void ClearStoredPanel(StoredPanelSlot storedPanel)
    {
        _suppressPersistence = true;

        try
        {
            storedPanel.Clear();
        }
        finally
        {
            _suppressPersistence = false;
        }

        SavePanelStates();
    }

    public void SwapSlotContents(WindowSlot source, WindowSlot target)
    {
        if (ReferenceEquals(source, target))
        {
            return;
        }

        _suppressPersistence = true;

        try
        {
            (source.PanelTitle, target.PanelTitle) = (target.PanelTitle, source.PanelTitle);
            (source.Path, target.Path) = (target.Path, source.Path);
            (source.SavedWorkspacePath, target.SavedWorkspacePath) = (target.SavedWorkspacePath, source.SavedWorkspacePath);
            (source.SavedWorkspaceConfirmed, target.SavedWorkspaceConfirmed) = (target.SavedWorkspaceConfirmed, source.SavedWorkspaceConfirmed);
            (source.CurrentWorkspacePath, target.CurrentWorkspacePath) = (target.CurrentWorkspacePath, source.CurrentWorkspacePath);
            (source.WindowHandle, target.WindowHandle) = (target.WindowHandle, source.WindowHandle);
            (source.WindowTitle, target.WindowTitle) = (target.WindowTitle, source.WindowTitle);
            (source.WindowStatus, target.WindowStatus) = (target.WindowStatus, source.WindowStatus);
            (source.AiStatus, target.AiStatus) = (target.AiStatus, source.AiStatus);
            (source.AiStatusDetail, target.AiStatusDetail) = (target.AiStatusDetail, source.AiStatusDetail);
            (source.LastEventAt, target.LastEventAt) = (target.LastEventAt, source.LastEventAt);
            (source.IsFocused, target.IsFocused) = (target.IsFocused, source.IsFocused);
            (source.WindowLayerMode, target.WindowLayerMode) = (target.WindowLayerMode, source.WindowLayerMode);
            (source.IsHidden, target.IsHidden) = (target.IsHidden, source.IsHidden);
            (source.PreferredLayout, target.PreferredLayout) = (target.PreferredLayout, source.PreferredLayout);
        }
        finally
        {
            _suppressPersistence = false;
        }

        SwapDictEntry(_workspaceRefreshTimestamps, source.Name, target.Name);
        _aiStatusDetector.SwapSlotSessions(source.Name, target.Name);
        SavePanelStates();
    }

    public async Task RefreshWindowStatusesAsync(
        WindowEnumerator windowEnumerator,
        CancellationToken cancellationToken)
    {
        var refreshStartedAt = DateTimeOffset.UtcNow;
        var requests = Slots
            .Select(slot => new WindowSlotStatusSnapshot(
                slot.Name,
                slot.WindowHandle,
                slot.WindowTitle,
                slot.CurrentWorkspacePath,
                _workspaceRefreshTimestamps.TryGetValue(slot.Name, out var refreshedAt) ? refreshedAt : null))
            .ToList();

        var stopwatch = Stopwatch.StartNew();
        var results = await Task.Run(
            () => RefreshWindowStatusesInBackground(windowEnumerator, requests, refreshStartedAt, cancellationToken),
            cancellationToken);
        stopwatch.Stop();

        ApplyWindowStatusRefreshResults(results);
        if (stopwatch.ElapsedMilliseconds >= 250)
        {
            DiagnosticLog.Write($"Status refresh took {stopwatch.ElapsedMilliseconds}ms for {results.Count} slots.");
        }
    }

    private IReadOnlyList<WindowSlotStatusRefreshResult> RefreshWindowStatusesInBackground(
        WindowEnumerator windowEnumerator,
        IReadOnlyList<WindowSlotStatusSnapshot> requests,
        DateTimeOffset refreshStartedAt,
        CancellationToken cancellationToken)
    {
        var results = new WindowSlotStatusRefreshResult[requests.Count];
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, requests.Count))
        };

        Parallel.For(0, requests.Count, options, index =>
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            results[index] = RefreshWindowStatusInBackground(
                windowEnumerator,
                requests[index],
                refreshStartedAt,
                options.CancellationToken);
        });

        return results;
    }

    private WindowSlotStatusRefreshResult RefreshWindowStatusInBackground(
        WindowEnumerator windowEnumerator,
        WindowSlotStatusSnapshot request,
        DateTimeOffset refreshStartedAt,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (request.WindowHandle == IntPtr.Zero)
            {
                return new WindowSlotStatusRefreshResult(
                    request.Name,
                    request.WindowHandle,
                    WindowSlotRefreshState.NoWindow,
                    null,
                    null,
                    null,
                    null,
                    stopwatch.ElapsedMilliseconds);
            }

            var window = windowEnumerator.TryGetWindow(request.WindowHandle);
            if (window is null)
            {
                return new WindowSlotStatusRefreshResult(
                    request.Name,
                    request.WindowHandle,
                    WindowSlotRefreshState.Missing,
                    null,
                    null,
                    null,
                    null,
                    stopwatch.ElapsedMilliseconds);
            }

            var snapshot = request with { WindowTitle = window.Title };
            var aiStatus = _aiStatusDetector.Detect(snapshot, Config);
            string? workspacePath = null;
            DateTimeOffset? workspaceRefreshedAt = null;
            if (ShouldRefreshWorkspacePath(snapshot, !string.Equals(request.WindowTitle, window.Title, StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                workspacePath = VscodeWorkspaceState.TryReadCurrentWorkspacePath(snapshot.Name, window.Title, Config);
                workspaceRefreshedAt = refreshStartedAt;
            }

            return new WindowSlotStatusRefreshResult(
                request.Name,
                request.WindowHandle,
                WindowSlotRefreshState.Ready,
                window,
                aiStatus,
                workspacePath,
                workspaceRefreshedAt,
                stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds >= 150)
            {
                DiagnosticLog.Write($"Slot {request.Name} status probe took {stopwatch.ElapsedMilliseconds}ms.");
            }
        }
    }

    private void ApplyWindowStatusRefreshResults(IReadOnlyList<WindowSlotStatusRefreshResult> results)
    {
        foreach (var result in results)
        {
            var slot = FindSlot(result.SlotName);
            if (slot is null || slot.WindowHandle != result.WindowHandle)
            {
                continue;
            }

            switch (result.State)
            {
                case WindowSlotRefreshState.NoWindow:
                    _workspaceRefreshTimestamps.Remove(slot.Name);
                    _aiStatusDetector.ResetSlotSession(slot);
                    slot.CurrentWorkspacePath = string.Empty;
                    slot.WindowStatus = SlotWindowStatus.Missing;
                    slot.AiStatus = AiStatus.Idle;
                    slot.AiStatusDetail = "VS Code は起動していません。";
                    break;

                case WindowSlotRefreshState.Missing:
                    ClearWindow(slot);
                    break;

                case WindowSlotRefreshState.Ready:
                    if (result.Window is not null)
                    {
                        slot.WindowTitle = result.Window.Title;
                    }

                    slot.WindowStatus = SlotWindowStatus.Ready;
                    if (result.AiStatus is not null)
                    {
                        ApplyAiStatus(slot, result.AiStatus);
                    }

                    if (result.WorkspaceRefreshedAt.HasValue)
                    {
                        slot.CurrentWorkspacePath = result.CurrentWorkspacePath ?? string.Empty;
                        _workspaceRefreshTimestamps[slot.Name] = result.WorkspaceRefreshedAt.Value;
                    }

                    break;
            }
        }
    }

    private static void ApplyAiStatus(WindowSlot slot, AiStatusSnapshot status)
    {
        slot.AiStatus = status.Status;
        slot.AiStatusDetail = status.Detail;
        if (status.EventAt.HasValue)
        {
            slot.LastEventAt = status.EventAt;
        }
    }

    private static bool ShouldRefreshWorkspacePath(WindowSlotStatusSnapshot slot, bool force)
    {
        if (force || string.IsNullOrWhiteSpace(slot.CurrentWorkspacePath))
        {
            return true;
        }

        return !slot.WorkspaceRefreshedAt.HasValue
            || DateTimeOffset.UtcNow - slot.WorkspaceRefreshedAt.Value >= WorkspaceRefreshInterval;
    }

    private void LoadSavedPanelStates()
    {
        var statePath = GetStatePath();
        if (!File.Exists(statePath))
        {
            return;
        }

        try
        {
            _suppressPersistence = true;
            var json = File.ReadAllText(statePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                var legacyStates = JsonSerializer.Deserialize<List<SavedSlotState>>(json, JsonOptions) ?? [];
                ApplyVisibleStates(legacyStates);
                return;
            }

            var stateDocument = JsonSerializer.Deserialize<SavedPanelStateDocument>(json, JsonOptions) ?? new SavedPanelStateDocument();
            ApplyVisibleStates(stateDocument.VisibleSlots);
            ApplyStoredStates(stateDocument.StoredPanels);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
        finally
        {
            _suppressPersistence = false;
        }
    }

    private void SavePanelStates()
    {
        if (_suppressPersistence)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Config.StateDirectory);
            var document = new SavedPanelStateDocument
            {
                VisibleSlots = Slots
                    .Select(slot => new SavedSlotState
                    {
                        Name = slot.Name,
                        PanelTitle = slot.PanelTitle,
                        AssignedPath = slot.Path,
                        SavedWorkspacePath = slot.SavedWorkspacePath,
                        SavedWorkspaceConfirmed = slot.SavedWorkspaceConfirmed,
                        WindowHandle = slot.WindowHandle.ToInt64(),
                        PreferredLayout = slot.PreferredLayout.HasAnyValue ? slot.PreferredLayout : null
                    })
                    .ToList(),
                StoredPanels = StoredPanels
                    .Select(slot => new SavedStoredPanelState
                    {
                        Index = slot.Index,
                        PanelTitle = slot.PanelTitle,
                        WorkspacePath = slot.WorkspacePath
                    })
                    .ToList()
            };

            File.WriteAllText(GetStatePath(), JsonSerializer.Serialize(document, JsonOptions));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private string GetStatePath()
    {
        return Path.Combine(Config.StateDirectory, "slots.json");
    }

    private void ApplyVisibleStates(IEnumerable<SavedSlotState> states)
    {
        foreach (var slot in Slots)
        {
            slot.PanelTitle = string.Empty;
            slot.Path = string.Empty;
            slot.SavedWorkspacePath = string.Empty;
            slot.SavedWorkspaceConfirmed = false;
            slot.WindowHandle = IntPtr.Zero;
            slot.CurrentWorkspacePath = string.Empty;
            slot.WindowTitle = string.Empty;
            slot.WindowStatus = SlotWindowStatus.Missing;
            slot.PreferredLayout = VscodeLayoutPreference.Empty;
        }

        foreach (var state in states)
        {
            var slot = Slots.FirstOrDefault(item => string.Equals(item.Name, state.Name, StringComparison.OrdinalIgnoreCase));
            if (slot is null)
            {
                continue;
            }

            slot.PanelTitle = state.PanelTitle ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(state.AssignedPath))
            {
                slot.Path = state.AssignedPath;
            }

            if (!string.IsNullOrWhiteSpace(state.SavedWorkspacePath))
            {
                slot.SavedWorkspacePath = state.SavedWorkspacePath;
            }

            slot.SavedWorkspaceConfirmed = state.SavedWorkspaceConfirmed;
            slot.PreferredLayout = state.PreferredLayout ?? VscodeLayoutPreference.Empty;

            if (state.WindowHandle != 0)
            {
                slot.WindowHandle = new IntPtr(state.WindowHandle);
            }
        }
    }

    private void ApplyStoredStates(IEnumerable<SavedStoredPanelState> states)
    {
        foreach (var storedPanel in StoredPanels)
        {
            storedPanel.Clear();
        }

        foreach (var state in states)
        {
            var storedPanel = StoredPanels.FirstOrDefault(item => item.Index == state.Index);
            storedPanel?.LoadFrom(state.PanelTitle, state.WorkspacePath);
        }
    }

    private static PanelSnapshot CreateSnapshot(WindowSlot slot)
    {
        var workspacePath = slot.CurrentWorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            workspacePath = slot.SavedWorkspaceConfirmed && !string.IsNullOrWhiteSpace(slot.SavedWorkspacePath)
                ? slot.SavedWorkspacePath
                : slot.Path;
        }

        var panelTitle = IsMeaningfulPanelTitle(slot)
            || (slot.WindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(slot.PanelTitle))
                ? slot.PanelTitle
                : string.Empty;
            return new PanelSnapshot(panelTitle, workspacePath);
    }

    private static PanelSnapshot CreateSnapshot(StoredPanelSlot slot)
    {
        return new PanelSnapshot(slot.PanelTitle, slot.WorkspacePath);
    }

    public string MakeUniqueTitle(string desiredTitle, params object?[] excludedItems)
    {
        return MakeUniquePanelTitle(desiredTitle, excludedItems);
    }

    private string MakeUniquePanelTitle(string desiredTitle, params object?[] excludedItems)
    {
        var baseTitle = string.IsNullOrWhiteSpace(desiredTitle) ? "スロット" : desiredTitle.Trim();
        var usedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in Slots)
        {
            if (excludedItems.Any(item => ReferenceEquals(item, slot)) || string.IsNullOrWhiteSpace(slot.PanelTitle))
            {
                continue;
            }

            usedTitles.Add(slot.PanelTitle.Trim());
        }

        foreach (var storedPanel in StoredPanels)
        {
            if (excludedItems.Any(item => ReferenceEquals(item, storedPanel)) || string.IsNullOrWhiteSpace(storedPanel.PanelTitle))
            {
                continue;
            }

            usedTitles.Add(storedPanel.PanelTitle.Trim());
        }

        if (!usedTitles.Contains(baseTitle))
        {
            return baseTitle;
        }

        for (var index = 1; ; index++)
        {
            var candidate = $"{baseTitle}({index})";
            if (!usedTitles.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string GetBaseTitleFromWorkspacePath(string workspacePath)
    {
        var trimmed = workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? "スロット" : fileName;
    }

    private static bool IsMeaningfulPanelTitle(WindowSlot slot)
    {
        return !string.IsNullOrWhiteSpace(slot.PanelTitle)
            && !string.Equals(slot.PanelTitle.Trim(), slot.DefaultPanelTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldAutoAssignWorkspaceTitle(WindowSlot slot)
    {
        return string.IsNullOrWhiteSpace(slot.PanelTitle)
            || string.Equals(slot.PanelTitle.Trim(), slot.DefaultPanelTitle, StringComparison.OrdinalIgnoreCase)
            || IsGeneratedLaunchTitle(slot.PanelTitle, slot.Name);
    }

    private static bool IsGeneratedLaunchTitle(string? title, string slotName)
    {
        return IsCopyTitleOf(title, $"スロット{slotName}");
    }

    private static bool IsCopyTitleOf(string? title, string baseTitle)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalized = title.Trim();
        if (string.Equals(normalized, baseTitle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!normalized.StartsWith(baseTitle, StringComparison.OrdinalIgnoreCase)
            || normalized.Length <= baseTitle.Length + 2
            || normalized[baseTitle.Length] != '('
            || normalized[^1] != ')')
        {
            return false;
        }

        return int.TryParse(normalized.AsSpan(baseTitle.Length + 1, normalized.Length - baseTitle.Length - 2), out _);
    }

    private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressPersistence)
        {
            return;
        }

        if (e.PropertyName is nameof(WindowSlot.PanelTitle)
            or nameof(WindowSlot.Path)
            or nameof(WindowSlot.SavedWorkspacePath)
            or nameof(WindowSlot.SavedWorkspaceConfirmed))
        {
            SavePanelStates();
        }
    }

    private void StoredPanel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressPersistence)
        {
            return;
        }

        if (e.PropertyName is nameof(StoredPanelSlot.PanelTitle) or nameof(StoredPanelSlot.WorkspacePath))
        {
            SavePanelStates();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static void SwapDictEntry<TKey, TValue>(Dictionary<TKey, TValue> dict, TKey keyA, TKey keyB)
        where TKey : notnull
    {
        var hasA = dict.Remove(keyA, out var valueA);
        var hasB = dict.Remove(keyB, out var valueB);
        if (hasA) dict[keyB] = valueA!;
        if (hasB) dict[keyA] = valueB!;
    }
}
