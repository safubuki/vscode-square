using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VscodeSquare.Panel.Models;

public sealed class WindowSlot : INotifyPropertyChanged
{
    public enum SlotWindowLayerMode
    {
        Topmost,
        Backmost
    }

    private IntPtr _windowHandle;
    private string _path = string.Empty;
    private string _panelTitle;
    private string _savedWorkspacePath = string.Empty;
    private bool _savedWorkspaceConfirmed;
    private string _currentWorkspacePath = string.Empty;
    private string _windowTitle = string.Empty;
    private SlotWindowStatus _windowStatus = SlotWindowStatus.Missing;
    private AiStatus _aiStatus = AiStatus.Unknown;
    private DateTimeOffset? _lastEventAt;
    private bool _isFocused;
    private SlotWindowLayerMode _windowLayerMode = SlotWindowLayerMode.Topmost;

    public WindowSlot(SlotConfig config)
    {
        Name = config.Name;
        _path = NormalizeWorkspacePath(config.Path);
        _panelTitle = GetDefaultPanelTitle();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

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
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public string EffectivePath => SavedWorkspaceConfirmed && !string.IsNullOrWhiteSpace(SavedWorkspacePath) ? SavedWorkspacePath : Path;

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

            var directoryName = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(directoryName) ? path : directoryName;
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
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(PanelTitle) ? GetDefaultPanelTitle() : PanelTitle;

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
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            SetField(ref _isFocused, value);
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

    public string WindowStatusText => WindowStatus switch
    {
        SlotWindowStatus.Ready => "起動",
        SlotWindowStatus.Launching => "起動中",
        SlotWindowStatus.Missing => "停止",
        _ => WindowStatus.ToString()
    };

    public string AiStatusText => AiStatus switch
    {
        AiStatus.Unknown => "AI 未取得",
        AiStatus.Idle => "AI 待機",
        AiStatus.Running => "AI 実行中",
        AiStatus.Completed => "AI 完了",
        AiStatus.Error => "AI エラー",
        AiStatus.NeedsAttention => "AI 要確認",
        AiStatus.WaitingForConfirmation => "AI 確認待ち",
        _ => $"AI {AiStatus}"
    };

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

    public AiStatus AiStatus
    {
        get => _aiStatus;
        set
        {
            if (SetField(ref _aiStatus, value))
            {
                OnPropertyChanged(nameof(AiStatusText));
            }
        }
    }

    public DateTimeOffset? LastEventAt
    {
        get => _lastEventAt;
        set
        {
            if (SetField(ref _lastEventAt, value))
            {
                OnPropertyChanged(nameof(LastEventText));
            }
        }
    }

    public string LastEventText => LastEventAt?.ToLocalTime().ToString("HH:mm:ss") ?? "-";

    public void ClearWindow()
    {
        WindowHandle = IntPtr.Zero;
        CurrentWorkspacePath = string.Empty;
        WindowTitle = string.Empty;
        WindowStatus = SlotWindowStatus.Missing;
        IsFocused = false;
        WindowLayerMode = SlotWindowLayerMode.Topmost;
    }

    public void ApplyAssignedPanel(string? panelTitle, string? workspacePath)
    {
        var normalizedPath = NormalizeWorkspacePath(workspacePath);
        PanelTitle = panelTitle ?? string.Empty;
        Path = normalizedPath;
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
