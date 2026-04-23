using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurtleAIQuartetHub.Panel.Models;

public sealed class StoredPanelSlot : INotifyPropertyChanged
{
    private string _panelTitle = string.Empty;
    private string _workspacePath = string.Empty;

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

            var directoryName = System.IO.Path.GetFileName(WorkspacePath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(directoryName) ? WorkspacePath : directoryName;
        }
    }

    public void LoadFrom(string? panelTitle, string? workspacePath)
    {
        PanelTitle = panelTitle ?? string.Empty;
        WorkspacePath = workspacePath ?? string.Empty;
    }

    public void Clear()
    {
        PanelTitle = string.Empty;
        WorkspacePath = string.Empty;
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

public sealed class StoredPanelPage
{
    public StoredPanelPage(int index, IEnumerable<StoredPanelSlot> slots)
    {
        Index = index;
        Slots = new System.Collections.ObjectModel.ObservableCollection<StoredPanelSlot>(slots);
    }

    public int Index { get; }

    public string Header => $"{Slots.First().Index}-{Slots.Last().Index}";

    public System.Collections.ObjectModel.ObservableCollection<StoredPanelSlot> Slots { get; }
}
