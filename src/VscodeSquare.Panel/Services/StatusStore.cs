using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VscodeSquare.Panel.Models;

namespace VscodeSquare.Panel.Services;

public sealed class StatusStore : INotifyPropertyChanged
{
    private string _message;

    public StatusStore(AppConfig config)
    {
        Config = config;
        Slots = new ObservableCollection<WindowSlot>(config.Slots.Select(slot => new WindowSlot(slot)));
        _message = $"Loaded config: {config.ConfigSource}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppConfig Config { get; }

    public ObservableCollection<WindowSlot> Slots { get; }

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
        slot.WindowStatus = SlotWindowStatus.Ready;
        slot.LastEventAt = DateTimeOffset.Now;
    }

    public void RefreshWindowStatuses(WindowEnumerator windowEnumerator)
    {
        foreach (var slot in Slots)
        {
            if (slot.WindowHandle == IntPtr.Zero)
            {
                slot.WindowStatus = SlotWindowStatus.Missing;
                continue;
            }

            var window = windowEnumerator.TryGetWindow(slot.WindowHandle);
            if (window is null)
            {
                slot.ClearWindow();
                continue;
            }

            slot.WindowTitle = window.Title;
            slot.WindowStatus = SlotWindowStatus.Ready;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
