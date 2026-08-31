using System.Collections.ObjectModel;
using System.Collections.Concurrent;
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
    private static readonly TimeSpan SlowRefreshLogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowSlotProbeLogInterval = TimeSpan.FromSeconds(5);
    private const int StoredPanelsPerPage = 4;
    private const int StoredPanelPageCount = 6;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly record struct PanelSnapshot(string PanelTitle, string WorkspacePath, string ApplicationId)
    {
        public bool HasContent => !string.IsNullOrWhiteSpace(PanelTitle) || !string.IsNullOrWhiteSpace(WorkspacePath);
    }

    private string _message;
    private bool _vsCodeUseHttpProxy;
    private string _vsCodeHttpProxy = string.Empty;
    private string _vsCodeHttpNoProxy = string.Empty;
    private LauncherApplication? _selectedWorkspaceApplication;
    private StoredPanelPage? _selectedStoredPanelPage;
    private bool _suppressPersistence;
    private readonly Dictionary<string, DateTimeOffset> _workspaceRefreshTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSlowSlotProbeLogAtBySlot = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastSlowRefreshLogAt = DateTimeOffset.MinValue;

    public StatusStore(AppConfig config)
    {
        Config = config;
        var applicationDetectionService = new ApplicationDetectionService();
        Applications = new ObservableCollection<LauncherApplication>(applicationDetectionService.Detect(config));
        WorkspaceApplications = new ObservableCollection<LauncherApplication>(Applications.Where(app => app.IsWorkspaceApplication));
        AuxiliaryApplications = new ObservableCollection<LauncherApplication>(Applications.Where(app => app.IsSingleWindowAgent));
        ApplicationPathSettings = new ObservableCollection<ApplicationPathSetting>(
            Applications.Select(app => new ApplicationPathSetting(app)));
        Slots = new ObservableCollection<WindowSlot>(config.Slots.Select(slot => new WindowSlot(slot)));
        StoredPanels = new ObservableCollection<StoredPanelSlot>(
            Enumerable.Range(1, StoredPanelsPerPage * StoredPanelPageCount).Select(index => new StoredPanelSlot(index)));
        StoredPanelPages = new ObservableCollection<StoredPanelPage>(
            Enumerable.Range(0, StoredPanelPageCount)
                .Select(pageIndex => new StoredPanelPage(
                    pageIndex + 1,
                    StoredPanels.Skip(pageIndex * StoredPanelsPerPage).Take(StoredPanelsPerPage))));
        SelectStoredPanelPage(StoredPanelPages.FirstOrDefault());
        LoadSavedPanelStates();
        foreach (var slot in Slots)
        {
            ApplyApplicationMetadata(slot);
            slot.PropertyChanged += Slot_PropertyChanged;
        }

        foreach (var storedPanel in StoredPanels)
        {
            ApplyApplicationMetadata(storedPanel);
            storedPanel.PropertyChanged += StoredPanel_PropertyChanged;
        }

        SelectWorkspaceApplication(config.DefaultWorkspaceApplicationId);
        _message = $"設定を読み込みました: {config.ConfigSource}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppConfig Config { get; }

    public ObservableCollection<LauncherApplication> Applications { get; }

    public ObservableCollection<LauncherApplication> WorkspaceApplications { get; }

    public ObservableCollection<LauncherApplication> AuxiliaryApplications { get; }

    public ObservableCollection<ApplicationPathSetting> ApplicationPathSettings { get; }

    public LauncherApplication? SelectedWorkspaceApplication
    {
        get => _selectedWorkspaceApplication;
        private set
        {
            if (ReferenceEquals(_selectedWorkspaceApplication, value))
            {
                return;
            }

            if (_selectedWorkspaceApplication is not null)
            {
                _selectedWorkspaceApplication.IsSelected = false;
            }

            _selectedWorkspaceApplication = value;
            if (_selectedWorkspaceApplication is not null)
            {
                _selectedWorkspaceApplication.IsSelected = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(LaunchButtonText));
            OnPropertyChanged(nameof(LaunchButtonToolTip));
            OnPropertyChanged(nameof(CanLaunchSelectedWorkspaceApplication));
        }
    }

    public string LaunchButtonText => "Launch Quartet（一括起動）";

    public string LaunchButtonToolTip => "各パネルで選択されている VS Code / Antigravity / CLI で未起動のスロットを一括起動します。";

    public bool CanLaunchSelectedWorkspaceApplication => SelectedWorkspaceApplication?.IsAvailable == true;

    public ObservableCollection<WindowSlot> Slots { get; }

    public ObservableCollection<StoredPanelSlot> StoredPanels { get; }

    public ObservableCollection<StoredPanelPage> StoredPanelPages { get; }

    public StoredPanelPage? SelectedStoredPanelPage
    {
        get => _selectedStoredPanelPage;
        private set
        {
            if (ReferenceEquals(_selectedStoredPanelPage, value))
            {
                return;
            }

            _selectedStoredPanelPage = value;
            OnPropertyChanged();
        }
    }

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

    public bool VsCodeUseHttpProxy
    {
        get => _vsCodeUseHttpProxy;
        set
        {
            if (_vsCodeUseHttpProxy == value)
            {
                return;
            }

            _vsCodeUseHttpProxy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VsCodeProxyFieldsEnabled));
        }
    }

    public bool VsCodeProxyFieldsEnabled => VsCodeUseHttpProxy;

    public string VsCodeHttpProxy
    {
        get => _vsCodeHttpProxy;
        set => SetNetworkField(ref _vsCodeHttpProxy, value ?? string.Empty);
    }

    public string VsCodeHttpNoProxy
    {
        get => _vsCodeHttpNoProxy;
        set => SetNetworkField(ref _vsCodeHttpNoProxy, value ?? string.Empty);
    }

    public LauncherApplication? FindApplication(string? applicationId)
    {
        var normalizedId = AppConfig.NormalizeApplicationId(applicationId, Config.DefaultWorkspaceApplicationId);
        return Applications.FirstOrDefault(app => string.Equals(app.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
            ?? Applications.FirstOrDefault(app => string.Equals(app.Id, AppConfig.VsCodeApplicationId, StringComparison.OrdinalIgnoreCase));
    }

    public LauncherApplication? FindAvailableWorkspaceApplication(string? applicationId)
    {
        var application = FindApplication(applicationId);
        if (application is { IsWorkspaceApplication: true, IsAvailable: true })
        {
            return application;
        }

        return WorkspaceApplications.FirstOrDefault(app => app.IsAvailable)
            ?? WorkspaceApplications.FirstOrDefault();
    }

    public bool SelectWorkspaceApplication(string? applicationId)
    {
        var application = FindApplication(applicationId);
        if (application is null || !application.IsWorkspaceApplication)
        {
            application = WorkspaceApplications.FirstOrDefault(app => string.Equals(app.Id, AppConfig.VsCodeApplicationId, StringComparison.OrdinalIgnoreCase))
                ?? WorkspaceApplications.FirstOrDefault();
        }

        if (application is null)
        {
            return false;
        }

        SelectedWorkspaceApplication = application;
        return application.IsAvailable;
    }

    public bool SelectWorkspaceApplication(LauncherApplication application)
    {
        if (!WorkspaceApplications.Contains(application))
        {
            return false;
        }

        SelectedWorkspaceApplication = application;
        return application.IsAvailable;
    }

    public void SetSlotApplication(WindowSlot slot, LauncherApplication application)
    {
        slot.ApplicationId = application.Id;
        ApplyApplicationMetadata(slot);
        SavePanelStates();
    }

    public void ResetApplicationPathSettings()
    {
        ApplicationPathSettings.Clear();
        foreach (var application in Applications)
        {
            ApplicationPathSettings.Add(new ApplicationPathSetting(application));
        }

        ResetVsCodeNetworkSettings();
    }

    public void ResetVsCodeNetworkSettings()
    {
        if (Config.ManageVsCodeUserSettings)
        {
            VsCodeUseHttpProxy = Config.VsCodeUseHttpProxy;
            VsCodeHttpProxy = Config.VsCodeHttpProxy;
            VsCodeHttpNoProxy = Config.VsCodeHttpNoProxy;
            return;
        }

        if (VscodeUserSettings.TryReadNetworkSettings(Config, out var current))
        {
            VsCodeUseHttpProxy = current.UseHttpProxy;
            VsCodeHttpProxy = current.HttpProxy;
            VsCodeHttpNoProxy = current.HttpNoProxy;
            return;
        }

        VsCodeUseHttpProxy = false;
        VsCodeHttpProxy = string.Empty;
        VsCodeHttpNoProxy = string.Empty;
    }

    public string ApplyVsCodeNetworkSettings()
    {
        var settings = new VscodeUserSettings.NetworkSettings(
            VsCodeUseHttpProxy,
            VsCodeHttpProxy,
            VsCodeHttpNoProxy);
        var validationError = settings.Validate();
        if (validationError is not null)
        {
            return validationError;
        }

        Config.ManageVsCodeUserSettings = true;
        Config.VsCodeUseHttpProxy = settings.UseHttpProxy;
        Config.VsCodeHttpProxy = settings.HttpProxy.Trim();
        Config.VsCodeHttpNoProxy = settings.HttpNoProxy.Trim();
        Config.SaveToUserConfig();

        var result = VscodeUserSettings.ApplyNetworkSettings(Config, settings);
        if (result.Errors.Count > 0)
        {
            return $"一部の settings.json へ書けませんでした: {string.Join(" / ", result.Errors)}";
        }

        var mode = settings.UseHttpProxy ? "プロキシ環境" : "オープンネットワーク";
        return $"VS Code ユーザー設定を {result.ProfilesWritten} 件のプロファイルへ適用しました（{mode}）。既に開いている窓では、必要ならウィンドウの再読み込みを行ってください。";
    }

    public void SaveApplicationPathSettings()
    {
        foreach (var setting in ApplicationPathSettings)
        {
            var appConfig = Config.Applications.FirstOrDefault(app =>
                string.Equals(app.Id, setting.Id, StringComparison.OrdinalIgnoreCase));
            if (appConfig is null)
            {
                continue;
            }

            appConfig.Command = setting.Command?.Trim() ?? string.Empty;
            if (string.Equals(setting.Id, AppConfig.VsCodeApplicationId, StringComparison.OrdinalIgnoreCase))
            {
                Config.CodeCommand = string.IsNullOrWhiteSpace(appConfig.Command) ? "code" : appConfig.Command;
            }
        }

        Config.SaveToUserConfig();
        ReloadApplicationsFromConfig();
    }

    public void ReloadApplicationsFromConfig()
    {
        Config.Normalize();
        var selectedApplicationId = SelectedWorkspaceApplication?.Id ?? Config.DefaultWorkspaceApplicationId;
        var applicationDetectionService = new ApplicationDetectionService();
        var detectedApplications = applicationDetectionService.Detect(Config);

        Applications.Clear();
        WorkspaceApplications.Clear();
        AuxiliaryApplications.Clear();
        foreach (var application in detectedApplications)
        {
            Applications.Add(application);
            if (application.IsWorkspaceApplication)
            {
                WorkspaceApplications.Add(application);
            }
            else if (application.IsSingleWindowAgent)
            {
                AuxiliaryApplications.Add(application);
            }
        }

        foreach (var slot in Slots)
        {
            ApplyApplicationMetadata(slot);
        }

        foreach (var storedPanel in StoredPanels)
        {
            ApplyApplicationMetadata(storedPanel);
        }

        ResetApplicationPathSettings();
        SelectWorkspaceApplication(selectedApplicationId);
        OnPropertyChanged(nameof(CanLaunchSelectedWorkspaceApplication));
    }

    public bool IsVsCodeSlot(WindowSlot slot)
    {
        return IsVsCodeApplication(slot.ApplicationId);
    }

    public bool IsVsCodeApplication(string? applicationId)
    {
        return string.Equals(AppConfig.NormalizeApplicationId(applicationId), AppConfig.VsCodeApplicationId, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsAntigravityApplication(string? applicationId)
    {
        return string.Equals(AppConfig.NormalizeApplicationId(applicationId), "antigravity", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanReadWorkspacePathFromApplication(string? applicationId)
    {
        return IsVsCodeApplication(applicationId) || IsAntigravityApplication(applicationId);
    }

    public void ApplyApplicationMetadata(WindowSlot slot)
    {
        var application = FindApplication(slot.ApplicationId);
        slot.ApplicationId = application?.Id ?? Config.DefaultWorkspaceApplicationId;
        slot.ApplicationDisplayName = application?.DisplayName ?? slot.ApplicationId;
        slot.ApplicationShortName = application?.ShortName ?? slot.ApplicationId;
        slot.ApplicationAvailabilityText = application?.StatusText ?? "未検出";
        slot.ApplicationToolTip = application?.ToolTip ?? "アプリケーション定義が見つかりません。";
        slot.IsApplicationAvailable = application?.IsAvailable == true;
        var vsCode = FindApplication(AppConfig.VsCodeApplicationId);
        var antigravity = FindApplication("antigravity");
        slot.IsVsCodeAvailable = vsCode?.IsAvailable == true;
        slot.IsAntigravityAvailable = antigravity?.IsAvailable == true;
        slot.VsCodeApplicationToolTip = vsCode?.ToolTip ?? "VS Code が検出できません。";
        slot.AntigravityApplicationToolTip = antigravity?.ToolTip ?? "Antigravity が検出できません。";

        slot.WorkspaceApplicationOptions.Clear();
        slot.IdeApplicationOptions.Clear();
        slot.CliApplicationOptions.Clear();
        foreach (var workspaceApplication in WorkspaceApplications)
        {
            var option = new SlotApplicationOption(
                slot,
                workspaceApplication,
                string.Equals(workspaceApplication.Id, slot.ApplicationId, StringComparison.OrdinalIgnoreCase));
            slot.WorkspaceApplicationOptions.Add(option);
            if (workspaceApplication.IsWorkspaceIde)
            {
                slot.IdeApplicationOptions.Add(option);
            }
            else if (workspaceApplication.IsWorkspaceCli)
            {
                slot.CliApplicationOptions.Add(option);
            }
        }
    }

    public void ApplyApplicationMetadata(StoredPanelSlot storedPanel)
    {
        var application = FindApplication(storedPanel.ApplicationId);
        storedPanel.ApplicationId = application?.Id ?? Config.DefaultWorkspaceApplicationId;
        storedPanel.ApplicationShortName = application?.ShortName ?? storedPanel.ApplicationId;
    }

    public void SelectStoredPanelPage(StoredPanelPage? page)
    {
        if (page is null || !StoredPanelPages.Contains(page))
        {
            return;
        }

        foreach (var storedPanelPage in StoredPanelPages)
        {
            storedPanelPage.IsSelected = ReferenceEquals(storedPanelPage, page);
        }

        SelectedStoredPanelPage = page;
    }

    public void AssignWindow(WindowSlot slot, WindowInfo window)
    {
        slot.WindowHandle = window.Handle;
        slot.WindowTitle = window.Title;
        slot.CurrentWorkspacePath = string.Empty;
        slot.WindowStatus = SlotWindowStatus.Ready;
        slot.WindowLayerMode = WindowSlot.SlotWindowLayerMode.Topmost;

        var workspacePath = !string.IsNullOrWhiteSpace(slot.SavedWorkspacePath)
            ? slot.SavedWorkspacePath
            : slot.Path;
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            slot.Path = workspacePath;
            slot.SavedWorkspacePath = workspacePath;
            slot.SavedWorkspaceConfirmed = true;
        }

        if (ShouldAutoAssignWorkspaceTitle(slot))
        {
            var preferredTitle = !string.IsNullOrWhiteSpace(workspacePath)
                ? GetBaseTitleFromWorkspacePath(workspacePath)
                : $"スロット{slot.Name}";
            slot.PanelTitle = MakeUniquePanelTitle(preferredTitle, slot);
        }

        SavePanelStates();
    }

    public void ClearWindow(WindowSlot slot)
    {
        _workspaceRefreshTimestamps.Remove(slot.Name);
        slot.ClearWindow();
        SavePanelStates();
    }

    public void ClearSlotPanelInfo(WindowSlot slot)
    {
        _workspaceRefreshTimestamps.Remove(slot.Name);
        _suppressPersistence = true;

        try
        {
            slot.ClearAssignedPanel();
            slot.ClearWindow();
            slot.ApplicationId = Config.DefaultWorkspaceApplicationId;
            ApplyApplicationMetadata(slot);
        }
        finally
        {
            _suppressPersistence = false;
        }

        SavePanelStates();
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
            : CanReadWorkspacePathFromApplication(slot.ApplicationId)
                ? VscodeWorkspaceState.TryReadCurrentWorkspacePath(slot, Config)
                : null;
        _workspaceRefreshTimestamps[slot.Name] = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            slot.CurrentWorkspacePath = workspacePath;
            slot.Path = workspacePath;
            slot.SavedWorkspacePath = workspacePath;
            slot.SavedWorkspaceConfirmed = true;

            if (ShouldAutoAssignWorkspaceTitle(slot))
            {
                slot.PanelTitle = MakeUniquePanelTitle(GetBaseTitleFromWorkspacePath(workspacePath), slot);
            }

            return;
        }

        if (CanReadWorkspacePathFromApplication(slot.ApplicationId))
        {
            slot.CurrentWorkspacePath = string.Empty;
        }
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

        storedPanel.LoadFrom(snapshot.PanelTitle, snapshot.WorkspacePath, snapshot.ApplicationId);
        ApplyApplicationMetadata(storedPanel);
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
            storedPanel.LoadFrom(visibleSnapshot.PanelTitle, visibleSnapshot.WorkspacePath, visibleSnapshot.ApplicationId);
            ApplyApplicationMetadata(storedPanel);
            swappedVisiblePanel = true;
        }
        else
        {
            storedPanel.Clear();
        }

        targetSlot.ClearWindow();
        targetSlot.ApplyAssignedPanel(storedSnapshot.PanelTitle, storedSnapshot.WorkspacePath, storedSnapshot.ApplicationId);
        ApplyApplicationMetadata(targetSlot);
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

    public bool RegisterStoredPanelWorkspace(StoredPanelSlot storedPanel, string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return false;
        }

        _suppressPersistence = true;
        try
        {
            storedPanel.LoadFrom(string.Empty, workspacePath, Config.DefaultWorkspaceApplicationId);
            if (!storedPanel.HasContent)
            {
                storedPanel.Clear();
                return false;
            }

            storedPanel.PanelTitle = MakeUniquePanelTitle(
                GetBaseTitleFromWorkspacePath(storedPanel.WorkspacePath),
                storedPanel);
            ApplyApplicationMetadata(storedPanel);
        }
        finally
        {
            _suppressPersistence = false;
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
            ApplyApplicationMetadata(storedPanel);
        }
        finally
        {
            _suppressPersistence = false;
        }

        SavePanelStates();
    }

    public PanelStateRepairResult RepairPanelState()
    {
        var visiblePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedVisible = 0;
        var normalizedStored = 0;
        var clearedIncomplete = 0;
        var clearedDuplicates = 0;

        _suppressPersistence = true;
        try
        {
            foreach (var slot in Slots)
            {
                var workspacePath = GetBestWorkspacePath(slot);
                if (string.IsNullOrWhiteSpace(workspacePath))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(slot.Path))
                {
                    slot.Path = workspacePath;
                    normalizedVisible++;
                }

                if (string.IsNullOrWhiteSpace(slot.SavedWorkspacePath))
                {
                    slot.SavedWorkspacePath = workspacePath;
                    normalizedVisible++;
                }

                if (!slot.SavedWorkspaceConfirmed)
                {
                    slot.SavedWorkspaceConfirmed = true;
                    normalizedVisible++;
                }

                var comparablePath = GetComparableWorkspacePath(workspacePath);
                if (!string.IsNullOrWhiteSpace(comparablePath))
                {
                    visiblePaths.Add(comparablePath);
                }
            }

            foreach (var storedPanel in StoredPanels)
            {
                if (!storedPanel.HasContent)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(storedPanel.WorkspacePath))
                {
                    storedPanel.Clear();
                    ApplyApplicationMetadata(storedPanel);
                    clearedIncomplete++;
                    continue;
                }

                var comparablePath = GetComparableWorkspacePath(storedPanel.WorkspacePath);
                if (!string.IsNullOrWhiteSpace(comparablePath)
                    && (visiblePaths.Contains(comparablePath) || !storedPaths.Add(comparablePath)))
                {
                    storedPanel.Clear();
                    ApplyApplicationMetadata(storedPanel);
                    clearedDuplicates++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(storedPanel.PanelTitle))
                {
                    storedPanel.PanelTitle = GetBaseTitleFromWorkspacePath(storedPanel.WorkspacePath);
                    normalizedStored++;
                }
            }
        }
        finally
        {
            _suppressPersistence = false;
        }

        SavePanelStates();

        return new PanelStateRepairResult(
            normalizedVisible,
            normalizedStored,
            clearedIncomplete,
            clearedDuplicates);
    }

    public void SwapStoredPanels(StoredPanelSlot source, StoredPanelSlot target)
    {
        if (ReferenceEquals(source, target))
        {
            return;
        }

        _suppressPersistence = true;

        try
        {
            (source.PanelTitle, target.PanelTitle) = (target.PanelTitle, source.PanelTitle);
            (source.WorkspacePath, target.WorkspacePath) = (target.WorkspacePath, source.WorkspacePath);
            (source.ApplicationId, target.ApplicationId) = (target.ApplicationId, source.ApplicationId);
            ApplyApplicationMetadata(source);
            ApplyApplicationMetadata(target);
        }
        finally
        {
            _suppressPersistence = false;
        }

        SavePanelStates();
    }

    /// <summary>
    /// 控え（全ページ通し）の空き隙間を前詰めする。9と11の間の10が空きなら11を10へ、
    /// ページをまたいで控え2の8が空きで控え3の9に内容があれば8へ、という具合に歯抜けを解消する。
    /// </summary>
    /// <returns>前へ移動した控えの件数。0なら歯抜けが無く何も変わっていない。</returns>
    public int CompactStoredPanels()
    {
        var contents = StoredPanels
            .Where(slot => slot.HasContent)
            .Select(slot => (slot.PanelTitle, slot.WorkspacePath, slot.ApplicationId))
            .ToList();

        // 既に前詰め済みなら（先頭から contents.Count 個がすべて中身あり）何もしない。
        var alreadyCompact = StoredPanels
            .Take(contents.Count)
            .All(slot => slot.HasContent);
        if (alreadyCompact)
        {
            return 0;
        }

        var movedCount = 0;
        _suppressPersistence = true;

        try
        {
            for (var i = 0; i < StoredPanels.Count; i++)
            {
                var slot = StoredPanels[i];
                var hadContent = slot.HasContent;

                if (i < contents.Count)
                {
                    var (title, path, applicationId) = contents[i];
                    slot.LoadFrom(title, path, applicationId);
                }
                else if (hadContent)
                {
                    slot.Clear();
                }

                ApplyApplicationMetadata(slot);

                if (!hadContent && slot.HasContent)
                {
                    movedCount++;
                }
            }
        }
        finally
        {
            _suppressPersistence = false;
        }

        SavePanelStates();
        return movedCount;
    }

    public bool TryMoveStoredPanelToPage(
        StoredPanelSlot source,
        StoredPanelPage targetPage,
        out StoredPanelSlot? targetSlot,
        out bool swapped)
    {
        targetSlot = null;
        swapped = false;

        if (!source.HasContent || targetPage.Slots.Contains(source))
        {
            return false;
        }

        targetSlot = targetPage.Slots.FirstOrDefault(slot => !slot.HasContent)
            ?? targetPage.Slots.FirstOrDefault();
        if (targetSlot is null || ReferenceEquals(source, targetSlot))
        {
            return false;
        }

        swapped = targetSlot.HasContent;
        SwapStoredPanels(source, targetSlot);
        return true;
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
            (source.ApplicationId, target.ApplicationId) = (target.ApplicationId, source.ApplicationId);
            (source.SavedWorkspacePath, target.SavedWorkspacePath) = (target.SavedWorkspacePath, source.SavedWorkspacePath);
            (source.SavedWorkspaceConfirmed, target.SavedWorkspaceConfirmed) = (target.SavedWorkspaceConfirmed, source.SavedWorkspaceConfirmed);
            (source.CurrentWorkspacePath, target.CurrentWorkspacePath) = (target.CurrentWorkspacePath, source.CurrentWorkspacePath);
            (source.RuntimeSlotName, target.RuntimeSlotName) = (target.RuntimeSlotName, source.RuntimeSlotName);
            (source.WindowHandle, target.WindowHandle) = (target.WindowHandle, source.WindowHandle);
            (source.WindowTitle, target.WindowTitle) = (target.WindowTitle, source.WindowTitle);
            (source.WindowStatus, target.WindowStatus) = (target.WindowStatus, source.WindowStatus);
            (source.IsFocused, target.IsFocused) = (target.IsFocused, source.IsFocused);
            // ディスプレイ割当はワークスペース側に付いて行く。これにより入替はカード上の象限だけを
            // 交換し、各ワークスペースは元のディスプレイ（とフォーカス/4 面の見え方）を維持する。
            (source.MonitorOverride, target.MonitorOverride) = (target.MonitorOverride, source.MonitorOverride);
            (source.WindowLayerMode, target.WindowLayerMode) = (target.WindowLayerMode, source.WindowLayerMode);
            (source.IsHidden, target.IsHidden) = (target.IsHidden, source.IsHidden);
            (source.PreferredLayout, target.PreferredLayout) = (target.PreferredLayout, source.PreferredLayout);
            ApplyApplicationMetadata(source);
            ApplyApplicationMetadata(target);
        }
        finally
        {
            _suppressPersistence = false;
        }

        SwapDictEntry(_workspaceRefreshTimestamps, source.Name, target.Name);
        SavePanelStates();
    }

    public async Task RefreshWindowStatusesAsync(
        WindowEnumerator windowEnumerator,
        CancellationToken cancellationToken)
    {
        var refreshStartedAt = DateTimeOffset.UtcNow;
        var requests = Slots
            .Select(slot =>
            {
                var application = FindApplication(slot.ApplicationId);
                return new WindowSlotStatusSnapshot(
                    slot.Name,
                    slot.RuntimeSlotName,
                    slot.ApplicationId,
                    application?.ProcessNames ?? [],
                    slot.WindowHandle,
                    slot.WindowTitle,
                    slot.CurrentWorkspacePath,
                    _workspaceRefreshTimestamps.TryGetValue(slot.Name, out var refreshedAt) ? refreshedAt : null);
            })
            .ToList();

        var stopwatch = Stopwatch.StartNew();
        var results = await Task.Run(
            () => RefreshWindowStatusesInBackground(windowEnumerator, requests, refreshStartedAt, cancellationToken),
            cancellationToken);
        stopwatch.Stop();

        ApplyWindowStatusRefreshResults(results);
        if (stopwatch.ElapsedMilliseconds >= 250 && ShouldLogSlowRefresh(refreshStartedAt))
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
            MaxDegreeOfParallelism = Math.Min(2, Math.Max(1, requests.Count))
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
                    stopwatch.ElapsedMilliseconds);
            }

            var window = windowEnumerator.TryGetWindow(request.WindowHandle, request.ProcessNames);
            if (window is null)
            {
                return new WindowSlotStatusRefreshResult(
                    request.Name,
                    request.WindowHandle,
                    WindowSlotRefreshState.Missing,
                    null,
                    null,
                    null,
                    stopwatch.ElapsedMilliseconds);
            }

            var snapshot = request with { WindowTitle = window.Title };
            string? workspacePath = null;
            DateTimeOffset? workspaceRefreshedAt = null;
            if (CanReadWorkspacePathFromApplication(request.ApplicationId)
                && ShouldRefreshWorkspacePath(snapshot, !string.Equals(request.WindowTitle, window.Title, StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                workspacePath = VscodeWorkspaceState.TryReadCurrentWorkspacePath(
                    snapshot.ApplicationId,
                    snapshot.RuntimeSlotName,
                    window.Title,
                    Config);
                workspaceRefreshedAt = refreshStartedAt;
            }

            return new WindowSlotStatusRefreshResult(
                request.Name,
                request.WindowHandle,
                WindowSlotRefreshState.Ready,
                window,
                workspacePath,
                workspaceRefreshedAt,
                stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds >= 150 && ShouldLogSlowSlotProbe(request.Name, DateTimeOffset.UtcNow))
            {
                DiagnosticLog.Write($"Slot {request.Name} status probe took {stopwatch.ElapsedMilliseconds}ms.");
            }
        }
    }

    private bool ShouldLogSlowRefresh(DateTimeOffset now)
    {
        if (now - _lastSlowRefreshLogAt < SlowRefreshLogInterval)
        {
            return false;
        }

        _lastSlowRefreshLogAt = now;
        return true;
    }

    private bool ShouldLogSlowSlotProbe(string slotName, DateTimeOffset now)
    {
        var updated = _lastSlowSlotProbeLogAtBySlot.AddOrUpdate(
            slotName,
            now,
            (_, previous) => now - previous >= SlowSlotProbeLogInterval ? now : previous);

        return updated == now;
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
                    slot.CurrentWorkspacePath = string.Empty;
                    slot.WindowStatus = SlotWindowStatus.Missing;
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

                    if (result.WorkspaceRefreshedAt.HasValue)
                    {
                        var workspacePath = result.CurrentWorkspacePath ?? string.Empty;
                        slot.CurrentWorkspacePath = workspacePath;
                        _workspaceRefreshTimestamps[slot.Name] = result.WorkspaceRefreshedAt.Value;
                        if (!string.IsNullOrWhiteSpace(workspacePath))
                        {
                            slot.Path = workspacePath;
                            slot.SavedWorkspacePath = workspacePath;
                            slot.SavedWorkspaceConfirmed = true;

                            if (ShouldAutoAssignWorkspaceTitle(slot))
                            {
                                slot.PanelTitle = MakeUniquePanelTitle(GetBaseTitleFromWorkspacePath(workspacePath), slot);
                            }
                        }
                    }

                    break;
            }
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
            ApplyStoredPageStates(stateDocument.StoredPanelPages);
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
                        RuntimeSlotName = slot.RuntimeSlotName,
                        PanelTitle = slot.PanelTitle,
                        AssignedPath = slot.Path,
                        ApplicationId = slot.ApplicationId,
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
                        WorkspacePath = slot.WorkspacePath,
                        ApplicationId = slot.ApplicationId
                    })
                    .ToList(),
                StoredPanelPages = StoredPanelPages
                    .Select(page => new SavedStoredPanelPageState
                    {
                        Index = page.Index,
                        CustomHeader = page.CustomHeader
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
            slot.RuntimeSlotName = slot.Name;
            slot.Path = string.Empty;
            slot.SavedWorkspacePath = string.Empty;
            slot.SavedWorkspaceConfirmed = false;
            slot.WindowHandle = IntPtr.Zero;
            slot.CurrentWorkspacePath = string.Empty;
            slot.WindowTitle = string.Empty;
            slot.WindowStatus = SlotWindowStatus.Missing;
            slot.PreferredLayout = VscodeLayoutPreference.Empty;
            slot.ApplicationId = Config.DefaultWorkspaceApplicationId;
            ApplyApplicationMetadata(slot);
        }

        foreach (var state in states)
        {
            var slot = Slots.FirstOrDefault(item => string.Equals(item.Name, state.Name, StringComparison.OrdinalIgnoreCase));
            if (slot is null)
            {
                continue;
            }

            slot.PanelTitle = state.PanelTitle ?? string.Empty;
            slot.RuntimeSlotName = state.RuntimeSlotName;

            if (!string.IsNullOrWhiteSpace(state.AssignedPath))
            {
                slot.Path = state.AssignedPath;
            }

            slot.ApplicationId = AppConfig.NormalizeApplicationId(state.ApplicationId, Config.DefaultWorkspaceApplicationId);
            ApplyApplicationMetadata(slot);

            if (!string.IsNullOrWhiteSpace(state.SavedWorkspacePath))
            {
                slot.SavedWorkspacePath = state.SavedWorkspacePath;
            }

            slot.SavedWorkspaceConfirmed = state.SavedWorkspaceConfirmed;
            slot.PreferredLayout = state.PreferredLayout ?? VscodeLayoutPreference.Empty;

            var restoredWorkspacePath = !string.IsNullOrWhiteSpace(slot.SavedWorkspacePath)
                ? slot.SavedWorkspacePath
                : slot.Path;
            if (!string.IsNullOrWhiteSpace(restoredWorkspacePath) && ShouldAutoAssignWorkspaceTitle(slot))
            {
                slot.PanelTitle = MakeUniquePanelTitle(GetBaseTitleFromWorkspacePath(restoredWorkspacePath), slot);
            }

            if (state.WindowHandle != 0)
            {
                slot.WindowHandle = new IntPtr(state.WindowHandle);
            }
        }

        EnsureUniqueRuntimeSlotNames();
    }

    private void ApplyStoredStates(IEnumerable<SavedStoredPanelState> states)
    {
        foreach (var storedPanel in StoredPanels)
        {
            storedPanel.Clear();
            ApplyApplicationMetadata(storedPanel);
        }

        foreach (var state in states)
        {
            var storedPanel = StoredPanels.FirstOrDefault(item => item.Index == state.Index);
            storedPanel?.LoadFrom(state.PanelTitle, state.WorkspacePath, state.ApplicationId);
            if (storedPanel is not null)
            {
                ApplyApplicationMetadata(storedPanel);
            }
        }
    }

    private void ApplyStoredPageStates(IEnumerable<SavedStoredPanelPageState> states)
    {
        foreach (var page in StoredPanelPages)
        {
            page.ResetHeader();
        }

        foreach (var state in states)
        {
            var page = StoredPanelPages.FirstOrDefault(item => item.Index == state.Index);
            if (page is not null)
            {
                page.CustomHeader = state.CustomHeader ?? string.Empty;
            }
        }
    }

    /// <summary>控えタブの名前を変更する。空文字なら既定名に戻る。</summary>
    public void RenameStoredPanelPage(StoredPanelPage page, string? header)
    {
        if (!StoredPanelPages.Contains(page))
        {
            return;
        }

        page.CustomHeader = header ?? string.Empty;
        SavePanelStates();
    }

    /// <summary>控えタブの名前を既定に戻す。</summary>
    public void ResetStoredPanelPageHeader(StoredPanelPage page)
    {
        RenameStoredPanelPage(page, string.Empty);
    }

    private void EnsureUniqueRuntimeSlotNames()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in Slots)
        {
            if (string.IsNullOrWhiteSpace(slot.RuntimeSlotName) || !used.Add(slot.RuntimeSlotName))
            {
                slot.RuntimeSlotName = Slots
                    .Select(candidate => candidate.Name)
                    .FirstOrDefault(candidate => !used.Contains(candidate)) ?? slot.Name;
                used.Add(slot.RuntimeSlotName);
            }
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
            return new PanelSnapshot(panelTitle, workspacePath, slot.ApplicationId);
    }

    private static PanelSnapshot CreateSnapshot(StoredPanelSlot slot)
    {
        return new PanelSnapshot(slot.PanelTitle, slot.WorkspacePath, slot.ApplicationId);
    }

    private static string GetBestWorkspacePath(WindowSlot slot)
    {
        if (!string.IsNullOrWhiteSpace(slot.CurrentWorkspacePath))
        {
            return slot.CurrentWorkspacePath;
        }

        if (!string.IsNullOrWhiteSpace(slot.SavedWorkspacePath))
        {
            return slot.SavedWorkspacePath;
        }

        return slot.Path;
    }

    private static string GetComparableWorkspacePath(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return string.Empty;
        }

        var trimmed = workspacePath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
        }

        try
        {
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
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
            or nameof(WindowSlot.SavedWorkspaceConfirmed)
            or nameof(WindowSlot.ApplicationId)
            or nameof(WindowSlot.RuntimeSlotName))
        {
            if (sender is WindowSlot slot && e.PropertyName == nameof(WindowSlot.ApplicationId))
            {
                ApplyApplicationMetadata(slot);
            }

            SavePanelStates();
        }
    }

    private void StoredPanel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressPersistence)
        {
            return;
        }

        if (e.PropertyName is nameof(StoredPanelSlot.PanelTitle) or nameof(StoredPanelSlot.WorkspacePath) or nameof(StoredPanelSlot.ApplicationId))
        {
            if (sender is StoredPanelSlot storedPanel && e.PropertyName == nameof(StoredPanelSlot.ApplicationId))
            {
                ApplyApplicationMetadata(storedPanel);
            }

            SavePanelStates();
        }
    }

    private void SetNetworkField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
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

// 不整合修復の結果。件数の内訳と「何か直したか」を保持し、結果ダイアログの文面に使う。
public sealed record PanelStateRepairResult(
    int NormalizedVisible,
    int NormalizedStored,
    int ClearedIncomplete,
    int ClearedDuplicates)
{
    public int TotalChanges => NormalizedVisible + NormalizedStored + ClearedIncomplete + ClearedDuplicates;

    public bool HasChanges => TotalChanges > 0;

    // 状態バー向けの一行サマリー（従来と同じ文面）。
    public string Summary => HasChanges
        ? $"修復完了: 表の補正 {NormalizedVisible} 件、控えの補正 {NormalizedStored} 件、不完全な控えの削除 {ClearedIncomplete} 件、重複控えの削除 {ClearedDuplicates} 件。"
        : "不整合は見つかりませんでした。";
}
