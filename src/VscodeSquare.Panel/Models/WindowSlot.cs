using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VscodeSquare.Panel.Models;

public sealed class WindowSlot : INotifyPropertyChanged
{
    private IntPtr _windowHandle;
    private string _windowTitle = string.Empty;
    private SlotWindowStatus _windowStatus = SlotWindowStatus.Missing;
    private AiStatus _aiStatus = AiStatus.Unknown;
    private DateTimeOffset? _lastEventAt;

    public WindowSlot(SlotConfig config)
    {
        Name = config.Name;
        Path = config.Path;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string Path { get; }

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
            OnPropertyChanged(nameof(WindowHandleText));
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
        set => SetField(ref _windowStatus, value);
    }

    public AiStatus AiStatus
    {
        get => _aiStatus;
        set => SetField(ref _aiStatus, value);
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
        WindowTitle = string.Empty;
        WindowStatus = SlotWindowStatus.Missing;
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

