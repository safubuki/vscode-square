using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TurtleAIQuartetHub.Panel.Models;

public sealed class WindowSlot : INotifyPropertyChanged
{
    public enum SlotWindowLayerMode
    {
        Topmost,
        Backmost
    }

    private IntPtr _windowHandle;
    private string _path = string.Empty;
    private string _applicationId = AppConfig.VsCodeApplicationId;
    private string _applicationDisplayName = "VS Code";
    private string _applicationShortName = "VS Code";
    private string _applicationAvailabilityText = "未確認";
    private string _applicationToolTip = string.Empty;
    private bool _isApplicationAvailable;
    private string _runtimeSlotName;
    private bool _isVsCodeAvailable;
    private bool _isAntigravityAvailable;
    private string _vsCodeApplicationToolTip = string.Empty;
    private string _antigravityApplicationToolTip = string.Empty;
    private string _panelTitle;
    private string _savedWorkspacePath = string.Empty;
    private bool _savedWorkspaceConfirmed;
    private string _currentWorkspacePath = string.Empty;
    private string _windowTitle = string.Empty;
    private SlotWindowStatus _windowStatus = SlotWindowStatus.Missing;
    private bool _isFocused;
    private SlotWindowLayerMode _windowLayerMode = SlotWindowLayerMode.Topmost;
    private bool _isHidden;
    private int? _monitorOverride;
    private string _displayBadgeText = string.Empty;
    private Brush _displayBrush = DefaultDisplayBrush;
    private VscodeLayoutPreference _preferredLayout = VscodeLayoutPreference.Empty;

    // 既定（ベースディスプレイ）の色。テーマのアクセント緑に合わせる。
    private static readonly Brush DefaultDisplayBrush = CreateFrozenBrush("#45D483");

    // 未命名スロットに自動で付く既定タイトルの接頭辞。極小ラベルの判定に使う。
    private const string DefaultTitlePrefix = "スロット";

    public WindowSlot(SlotConfig config)
    {
        Name = config.Name;
        _runtimeSlotName = NormalizeRuntimeSlotName(config.Name, config.Name);
        _path = NormalizeWorkspacePath(config.Path);
        _applicationId = AppConfig.NormalizeApplicationId(config.ApplicationId);
        _panelTitle = GetDefaultPanelTitle();
        WorkspaceApplicationOptions = [];
        IdeApplicationOptions = [];
        CliApplicationOptions = [];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string RuntimeSlotName
    {
        get => string.IsNullOrWhiteSpace(_runtimeSlotName) ? Name : _runtimeSlotName;
        set => SetField(ref _runtimeSlotName, NormalizeRuntimeSlotName(value, Name));
    }

    public ObservableCollection<SlotApplicationOption> WorkspaceApplicationOptions { get; }

    public ObservableCollection<SlotApplicationOption> IdeApplicationOptions { get; }

    public ObservableCollection<SlotApplicationOption> CliApplicationOptions { get; }

    public string Path
    {
        get => _path;
        set
        {
            if (SetField(ref _path, NormalizeWorkspacePath(value)))
            {
                OnPropertyChanged(nameof(EffectivePath));
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(ShortPath));
                OnPropertyChanged(nameof(ExplorerWorkspacePath));
                OnPropertyChanged(nameof(CanOpenWorkspaceFolder));
                OnPropertyChanged(nameof(WorkspaceFolderToolTip));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public string EffectivePath => SavedWorkspaceConfirmed && !string.IsNullOrWhiteSpace(SavedWorkspacePath) ? SavedWorkspacePath : Path;

    public string ApplicationId
    {
        get => _applicationId;
        set
        {
            if (SetField(ref _applicationId, AppConfig.NormalizeApplicationId(value)))
            {
                OnPropertyChanged(nameof(ApplicationBadgeText));
                OnPropertyChanged(nameof(IsVsCodeApplication));
                OnPropertyChanged(nameof(IsAntigravityApplication));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public string ApplicationDisplayName
    {
        get => _applicationDisplayName;
        set
        {
            if (SetField(ref _applicationDisplayName, string.IsNullOrWhiteSpace(value) ? ApplicationId : value.Trim()))
            {
                OnPropertyChanged(nameof(ApplicationBadgeText));
            }
        }
    }

    public string ApplicationShortName
    {
        get => _applicationShortName;
        set
        {
            if (SetField(ref _applicationShortName, string.IsNullOrWhiteSpace(value) ? ApplicationDisplayName : value.Trim()))
            {
                OnPropertyChanged(nameof(ApplicationBadgeText));
            }
        }
    }

    public string ApplicationAvailabilityText
    {
        get => _applicationAvailabilityText;
        set => SetField(ref _applicationAvailabilityText, value ?? string.Empty);
    }

    public string ApplicationToolTip
    {
        get => _applicationToolTip;
        set => SetField(ref _applicationToolTip, value ?? string.Empty);
    }

    public bool IsApplicationAvailable
    {
        get => _isApplicationAvailable;
        set => SetField(ref _isApplicationAvailable, value);
    }

    public string ApplicationBadgeText => string.IsNullOrWhiteSpace(ApplicationShortName) ? ApplicationId : ApplicationShortName;

    public bool IsVsCodeApplication => string.Equals(ApplicationId, AppConfig.VsCodeApplicationId, StringComparison.OrdinalIgnoreCase);

    public bool IsAntigravityApplication => string.Equals(ApplicationId, "antigravity", StringComparison.OrdinalIgnoreCase);

    public bool IsVsCodeAvailable
    {
        get => _isVsCodeAvailable;
        set => SetField(ref _isVsCodeAvailable, value);
    }

    public bool IsAntigravityAvailable
    {
        get => _isAntigravityAvailable;
        set => SetField(ref _isAntigravityAvailable, value);
    }

    public string VsCodeApplicationToolTip
    {
        get => _vsCodeApplicationToolTip;
        set => SetField(ref _vsCodeApplicationToolTip, value ?? string.Empty);
    }

    public string AntigravityApplicationToolTip
    {
        get => _antigravityApplicationToolTip;
        set => SetField(ref _antigravityApplicationToolTip, value ?? string.Empty);
    }

    public string DisplayPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CurrentWorkspacePath))
            {
                return CurrentWorkspacePath;
            }

            return WindowHandle != IntPtr.Zero ? Path : EffectivePath;
        }
    }

    public string ShortPath
    {
        get
        {
            var path = DisplayPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return "-";
            }

            return WorkspacePathDisplay.GetShortPath(path);
        }
    }

    public string ExplorerWorkspacePath => GetExplorerWorkspacePath(DisplayPath);

    public bool CanOpenWorkspaceFolder => !string.IsNullOrWhiteSpace(ExplorerWorkspacePath);

    public string WorkspaceFolderToolTip
    {
        get
        {
            var displayPath = DisplayPath;
            if (string.IsNullOrWhiteSpace(displayPath))
            {
                return "ワークスペースフォルダが未設定です。";
            }

            if (IsRemoteWorkspacePath(displayPath))
            {
                return "SSH などのリモートワークスペースはエクスプローラで開けません。";
            }

            return CanOpenWorkspaceFolder
                ? $"エクスプローラで開く: {ExplorerWorkspacePath}"
                : $"ローカルフォルダが見つかりません: {displayPath}";
        }
    }

    public string PanelTitle
    {
        get => _panelTitle;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (SetField(ref _panelTitle, normalizedValue))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(MicroLabel));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(PanelTitle) ? GetDefaultPanelTitle() : PanelTitle;

    /// <summary>
    /// 極小モードのセルに出す短縮ラベル。タイトルの先頭 1〜2 文字を取り、A/B/C/D より
    /// 中身で識別できるようにする。タイトル未設定（既定名）のときはスロット名に戻す。
    /// </summary>
    public string MicroLabel => BuildMicroLabel();

    public string DefaultPanelTitle => GetDefaultPanelTitle();

    public string SavedWorkspacePath
    {
        get => _savedWorkspacePath;
        set
        {
            if (SetField(ref _savedWorkspacePath, NormalizeWorkspacePath(value)))
            {
                OnPropertyChanged(nameof(EffectivePath));
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(ShortPath));
                OnPropertyChanged(nameof(ExplorerWorkspacePath));
                OnPropertyChanged(nameof(CanOpenWorkspaceFolder));
                OnPropertyChanged(nameof(WorkspaceFolderToolTip));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public bool SavedWorkspaceConfirmed
    {
        get => _savedWorkspaceConfirmed;
        set
        {
            if (SetField(ref _savedWorkspaceConfirmed, value))
            {
                OnPropertyChanged(nameof(EffectivePath));
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(ShortPath));
                OnPropertyChanged(nameof(ExplorerWorkspacePath));
                OnPropertyChanged(nameof(CanOpenWorkspaceFolder));
                OnPropertyChanged(nameof(WorkspaceFolderToolTip));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public string CurrentWorkspacePath
    {
        get => _currentWorkspacePath;
        set
        {
            if (SetField(ref _currentWorkspacePath, NormalizeWorkspacePath(value)))
            {
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(ShortPath));
                OnPropertyChanged(nameof(ExplorerWorkspacePath));
                OnPropertyChanged(nameof(CanOpenWorkspaceFolder));
                OnPropertyChanged(nameof(WorkspaceFolderToolTip));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (SetField(ref _isFocused, value))
            {
                OnPropertyChanged(nameof(FocusFrameBrush));
            }
        }
    }

    public SlotWindowLayerMode WindowLayerMode
    {
        get => _windowLayerMode;
        set
        {
            if (SetField(ref _windowLayerMode, value))
            {
                OnPropertyChanged(nameof(IsTopmostLayer));
                OnPropertyChanged(nameof(IsBackmostLayer));
            }
        }
    }

    public bool IsTopmostLayer => WindowLayerMode == SlotWindowLayerMode.Topmost;

    public bool IsBackmostLayer => WindowLayerMode == SlotWindowLayerMode.Backmost;

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (SetField(ref _isHidden, value))
            {
                OnPropertyChanged(nameof(WindowStatusText));
            }
        }
    }

    public VscodeLayoutPreference PreferredLayout
    {
        get => _preferredLayout;
        set => _preferredLayout = value ?? VscodeLayoutPreference.Empty;
    }

    /// <summary>
    /// このスロットだけを単独で配置するディスプレイ。null はベースディスプレイに追従する状態。
    /// フォーカスやレイヤーと同じランタイム状態として扱い、保存はしない。全ディスプレイ移動で
    /// ベースが追いついたら（override == ベース）解除して群れに戻す。
    /// </summary>
    public int? MonitorOverride
    {
        get => _monitorOverride;
        set
        {
            if (SetField(ref _monitorOverride, value))
            {
                OnPropertyChanged(nameof(HasMonitorOverride));
            }
        }
    }

    public bool HasMonitorOverride => _monitorOverride.HasValue;

    /// <summary>
    /// 現在の実効ディスプレイをカードに表示する短いバッジ（例: "D2"）。複数モニタかつ
    /// いずれかのパネルが単独移動しているときだけ MainWindow が設定する。それ以外は空。
    /// </summary>
    public string DisplayBadgeText
    {
        get => _displayBadgeText;
        set
        {
            if (SetField(ref _displayBadgeText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasDisplayBadge));
            }
        }
    }

    public bool HasDisplayBadge => !string.IsNullOrEmpty(_displayBadgeText);

    /// <summary>
    /// このスロットの実効ディスプレイを表す色。ベースディスプレイは緑、単独移動先は青/紫/金…と
    /// MainWindow が割り当てる。バッジ文字色とフォーカス枠の色に使う。
    /// </summary>
    public Brush DisplayBrush
    {
        get => _displayBrush;
        set
        {
            var normalized = value ?? DefaultDisplayBrush;
            if (!ReferenceEquals(_displayBrush, normalized))
            {
                _displayBrush = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FocusFrameBrush));
            }
        }
    }

    /// <summary>フォーカス枠の色。フォーカス中だけ実効ディスプレイ色、それ以外は透明。</summary>
    public Brush FocusFrameBrush => IsFocused ? DisplayBrush : Brushes.Transparent;

    public string WindowStatusText
    {
        get
        {
            if (IsHidden && WindowStatus == SlotWindowStatus.Ready)
            {
                return "非表示";
            }

            return WindowStatus switch
            {
                SlotWindowStatus.Ready => "起動",
                SlotWindowStatus.Launching => "起動中",
                SlotWindowStatus.Missing => "停止中",
                _ => WindowStatus.ToString()
            };
        }
    }

    public bool HasPanelContent => WindowHandle != IntPtr.Zero
        || !string.IsNullOrWhiteSpace(PanelTitle)
        || !string.IsNullOrWhiteSpace(CurrentWorkspacePath)
        || !string.IsNullOrWhiteSpace(SavedWorkspacePath)
        || !string.IsNullOrWhiteSpace(Path);

    public IntPtr WindowHandle
    {
        get => _windowHandle;
        set
        {
            if (_windowHandle == value)
            {
                return;
            }

            _windowHandle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPath));
            OnPropertyChanged(nameof(WindowHandleText));
            OnPropertyChanged(nameof(ShortPath));
            OnPropertyChanged(nameof(ExplorerWorkspacePath));
            OnPropertyChanged(nameof(CanOpenWorkspaceFolder));
            OnPropertyChanged(nameof(WorkspaceFolderToolTip));
            OnPropertyChanged(nameof(HasPanelContent));
        }
    }

    public string WindowHandleText => WindowHandle == IntPtr.Zero ? "-" : $"0x{WindowHandle.ToInt64():X}";

    public string WindowTitle
    {
        get => _windowTitle;
        set => SetField(ref _windowTitle, value);
    }

    public SlotWindowStatus WindowStatus
    {
        get => _windowStatus;
        set
        {
            if (SetField(ref _windowStatus, value))
            {
                OnPropertyChanged(nameof(WindowStatusText));
            }
        }
    }

    public void ClearWindow()
    {
        WindowHandle = IntPtr.Zero;
        CurrentWorkspacePath = string.Empty;
        WindowTitle = string.Empty;
        WindowStatus = SlotWindowStatus.Missing;
        IsFocused = false;
        WindowLayerMode = SlotWindowLayerMode.Topmost;
        MonitorOverride = null;
        DisplayBadgeText = string.Empty;
    }

    public void ApplyAssignedPanel(string? panelTitle, string? workspacePath)
    {
        ApplyAssignedPanel(panelTitle, workspacePath, ApplicationId);
    }

    public void ApplyAssignedPanel(string? panelTitle, string? workspacePath, string? applicationId)
    {
        var normalizedPath = NormalizeWorkspacePath(workspacePath);
        PanelTitle = panelTitle ?? string.Empty;
        Path = normalizedPath;
        ApplicationId = applicationId ?? AppConfig.VsCodeApplicationId;
        SavedWorkspacePath = normalizedPath;
        SavedWorkspaceConfirmed = !string.IsNullOrWhiteSpace(normalizedPath);
        CurrentWorkspacePath = string.Empty;
    }

    public void ClearAssignedPanel()
    {
        PanelTitle = string.Empty;
        Path = string.Empty;
        SavedWorkspacePath = string.Empty;
        SavedWorkspaceConfirmed = false;
        CurrentWorkspacePath = string.Empty;
    }

    private string GetDefaultPanelTitle()
    {
        return string.IsNullOrWhiteSpace(Name) ? "未設定" : $"スロット {Name}";
    }

    private string BuildMicroLabel()
    {
        // 既定タイトル("スロット A" / 新規時の "スロットA"、重複時は "スロットA 2" など)は
        // どれも先頭 2 文字が "スロ" になり識別に使えない。実質未設定として扱い、
        // 従来どおりスロット名(A/B/C/D)を出す。
        if (string.IsNullOrWhiteSpace(PanelTitle)
            || PanelTitle.TrimStart().StartsWith(DefaultTitlePrefix, StringComparison.Ordinal))
        {
            return Name;
        }

        var title = PanelTitle.Trim();

        // 記号始まり（"[wip] foo" など）は中身に届かないので、最初の英数字/文字まで送る。
        var start = 0;
        while (start < title.Length && !char.IsLetterOrDigit(title[start]))
        {
            start++;
        }

        if (start >= title.Length)
        {
            return Name;
        }

        // サロゲートペア（絵文字など）は 1 文字として扱い、途中で割らない。
        var label = string.Empty;
        var index = start;
        var taken = 0;
        while (index < title.Length && taken < 2)
        {
            var length = char.IsHighSurrogate(title[index]) && index + 1 < title.Length ? 2 : 1;
            label += title.Substring(index, length);
            index += length;
            taken++;
        }

        return label.Length == 0 ? Name : label;
    }

    private static string NormalizeWorkspacePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var path = value.Trim();
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
        {
            path = path[1..];
        }

        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            path = path.Replace('/', System.IO.Path.DirectorySeparatorChar);
        }

        return path;
    }

    private static string GetExplorerWorkspacePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var path = value.Trim();
        if (IsRemoteWorkspacePath(path))
        {
            return string.Empty;
        }

        path = Environment.ExpandEnvironmentVariables(GetLocalPathFromFileUriOrPath(path));

        try
        {
            if (Directory.Exists(path))
            {
                return System.IO.Path.GetFullPath(path);
            }

            if (File.Exists(path))
            {
                return System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }

        return string.Empty;
    }

    private static string GetLocalPathFromFileUriOrPath(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri is null || !uri.IsFile)
        {
            return value;
        }

        var localPath = uri.LocalPath;
        if (localPath.Length >= 3 && localPath[0] == '/' && char.IsLetter(localPath[1]) && localPath[2] == ':')
        {
            localPath = localPath[1..];
        }

        return localPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
    }

    private static bool IsRemoteWorkspacePath(string value)
    {
        var path = value.Trim();
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || IsWindowsPath(path))
        {
            return false;
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out var uri)
            && uri is not null
            && !string.IsNullOrWhiteSpace(uri.Scheme))
        {
            return !uri.IsFile;
        }

        return TryReadUriScheme(path, out var scheme)
            && !string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadUriScheme(string value, out string scheme)
    {
        scheme = string.Empty;
        var schemeSeparatorIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparatorIndex <= 0)
        {
            return false;
        }

        var candidate = value[..schemeSeparatorIndex];
        if (!IsValidUriScheme(candidate))
        {
            return false;
        }

        scheme = candidate;
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

    private static bool IsWindowsPath(string value)
    {
        return value.Length >= 3
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/');
    }

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static string NormalizeRuntimeSlotName(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "slot" : normalized;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
