using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VscodeSquare.Panel.Models;

namespace VscodeSquare.Panel.Services;

public sealed class StatusStore : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string _message;

    public StatusStore(AppConfig config)
    {
        Config = config;
        Slots = new ObservableCollection<WindowSlot>(config.Slots.Select(slot => new WindowSlot(slot)));
        LoadSavedSlotStates();
        foreach (var slot in Slots)
        {
            slot.PropertyChanged += Slot_PropertyChanged;
        }

        _message = $"設定を読み込みました: {config.ConfigSource}";
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
        SaveSlotStates();
    }

    public void ClearWindow(WindowSlot slot)
    {
        slot.ClearWindow();
        SaveSlotStates();
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
                ClearWindow(slot);
                continue;
            }

            slot.WindowTitle = window.Title;
            slot.WindowStatus = SlotWindowStatus.Ready;
        }
    }

    private void LoadSavedSlotStates()
    {
        var statePath = GetStatePath();
        if (!File.Exists(statePath))
        {
            return;
        }

        try
        {
            var states = JsonSerializer.Deserialize<List<SavedSlotState>>(File.ReadAllText(statePath)) ?? [];
            foreach (var state in states)
            {
                var slot = Slots.FirstOrDefault(item => string.Equals(item.Name, state.Name, StringComparison.OrdinalIgnoreCase));
                if (slot is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(state.PanelTitle))
                {
                    slot.PanelTitle = state.PanelTitle;
                }

                if (state.WindowHandle != 0)
                {
                    slot.WindowHandle = new IntPtr(state.WindowHandle);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private void SaveSlotStates()
    {
        try
        {
            Directory.CreateDirectory(Config.StateDirectory);
            var states = Slots
                .Select(slot => new SavedSlotState
                {
                    Name = slot.Name,
                    PanelTitle = slot.PanelTitle,
                    WindowHandle = slot.WindowHandle.ToInt64()
                })
                .ToList();

            File.WriteAllText(GetStatePath(), JsonSerializer.Serialize(states, JsonOptions));
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

    private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WindowSlot.PanelTitle))
        {
            SaveSlotStates();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
