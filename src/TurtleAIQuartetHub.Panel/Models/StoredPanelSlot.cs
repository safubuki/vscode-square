using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurtleAIQuartetHub.Panel.Models;

public sealed class StoredPanelSlot : INotifyPropertyChanged
{
    private string _panelTitle = string.Empty;
    private string _workspacePath = string.Empty;
    private string _applicationId = AppConfig.VsCodeApplicationId;
    private string _applicationShortName = "VS Code";

    public StoredPanelSlot(int index)
    {
        Index = index;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    public string Label => Index.ToString();

    public string PanelTitle
    {
        get => _panelTitle;
        set
        {
            if (SetField(ref _panelTitle, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(HasContent));
            }
        }
    }

    public string WorkspacePath
    {
        get => _workspacePath;
        set
        {
            if (SetField(ref _workspacePath, NormalizeWorkspacePath(value)))
            {
                OnPropertyChanged(nameof(ShortPath));
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(HasContent));
            }
        }
    }

    public bool HasContent => !string.IsNullOrWhiteSpace(PanelTitle) || !string.IsNullOrWhiteSpace(WorkspacePath);

    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(PanelTitle))
            {
                return PanelTitle;
            }

            return HasContent ? ShortPath : "空き";
        }
    }

    public string ShortPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WorkspacePath))
            {
                return "-";
            }

            return WorkspacePathDisplay.GetShortPath(WorkspacePath);
        }
    }

    public string ApplicationId
    {
        get => _applicationId;
        set
        {
            if (SetField(ref _applicationId, AppConfig.NormalizeApplicationId(value)))
            {
                OnPropertyChanged(nameof(ApplicationShortName));
            }
        }
    }

    public string ApplicationShortName
    {
        get => _applicationShortName;
        set => SetField(ref _applicationShortName, string.IsNullOrWhiteSpace(value) ? ApplicationId : value.Trim());
    }

    public void LoadFrom(string? panelTitle, string? workspacePath)
    {
        LoadFrom(panelTitle, workspacePath, ApplicationId);
    }

    public void LoadFrom(string? panelTitle, string? workspacePath, string? applicationId)
    {
        PanelTitle = panelTitle ?? string.Empty;
        WorkspacePath = workspacePath ?? string.Empty;
        ApplicationId = applicationId ?? AppConfig.VsCodeApplicationId;
    }

    public void Clear()
    {
        PanelTitle = string.Empty;
        WorkspacePath = string.Empty;
        ApplicationId = AppConfig.VsCodeApplicationId;
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

public sealed class StoredPanelPage : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _customHeader = string.Empty;

    public StoredPanelPage(int index, IEnumerable<StoredPanelSlot> slots)
    {
        Index = index;
        Slots = new System.Collections.ObjectModel.ObservableCollection<StoredPanelSlot>(slots);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    /// <summary>タブに出す既定の名前。ユーザーが未設定なら、この名前がそのまま使われる。</summary>
    public string DefaultHeader => $"控え{Index}";

    /// <summary>
    /// ユーザーが付けた名前。空文字なら既定名に戻る（＝「デフォルトに戻す」は空文字の代入で済む）。
    /// </summary>
    public string CustomHeader
    {
        get => _customHeader;
        set
        {
            var normalized = (value ?? string.Empty).Trim();

            // 既定名そのものを入力した場合は「未設定」と同じ扱いにして、
            // 既定名の変更（例: 表記ゆれの修正）に自動で追従できるようにする。
            if (string.Equals(normalized, DefaultHeader, StringComparison.Ordinal))
            {
                normalized = string.Empty;
            }

            if (_customHeader == normalized)
            {
                return;
            }

            _customHeader = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Header));
            OnPropertyChanged(nameof(HasCustomHeader));
        }
    }

    /// <summary>既定名から変更されているか。「デフォルトに戻す」ボタンの活性判定に使う。</summary>
    public bool HasCustomHeader => !string.IsNullOrEmpty(_customHeader);

    public string Header => HasCustomHeader ? _customHeader : DefaultHeader;

    /// <summary>この控えの名前を既定に戻す。</summary>
    public void ResetHeader()
    {
        CustomHeader = string.Empty;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<StoredPanelSlot> Slots { get; }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
