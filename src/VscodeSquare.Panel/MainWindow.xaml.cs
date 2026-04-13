using System.Windows;
using System.Windows.Threading;
using VscodeSquare.Panel.Models;
using VscodeSquare.Panel.Services;

namespace VscodeSquare.Panel;

public partial class MainWindow : Window
{
    private readonly WindowEnumerator _windowEnumerator = new();
    private readonly WindowArranger _windowArranger = new();
    private readonly VscodeLauncher _vscodeLauncher;
    private readonly StatusStore _statusStore;
    private readonly DispatcherTimer _refreshTimer;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        var config = AppConfig.Load();
        _statusStore = new StatusStore(config);
        _vscodeLauncher = new VscodeLauncher(_windowEnumerator);
        DataContext = _statusStore;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += (_, _) => RefreshSlots();
        _refreshTimer.Start();

        Loaded += (_, _) => RefreshSlots();
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (!_vscodeLauncher.IsCodeCommandAvailable(_statusStore.Config.CodeCommand))
            {
                _statusStore.Message = $"`{_statusStore.Config.CodeCommand}` was not found. Install VS Code shell command or update config.";
                return;
            }

            _statusStore.Message = "Launching missing VS Code windows...";
            var assignments = await _vscodeLauncher.LaunchMissingAsync(
                _statusStore.Slots,
                _statusStore.Config,
                CancellationToken.None);

            foreach (var assignment in assignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            RefreshSlots();

            if (assignments.Count > 0)
            {
                _windowArranger.Arrange(_statusStore.Slots, _statusStore.Config.Gap);
                _statusStore.Message = $"Launched and assigned {assignments.Count} VS Code window(s).";
            }
            else
            {
                _statusStore.Message = "No new VS Code windows were detected. Existing assigned windows were kept.";
            }
        });
    }

    private void ArrangeButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSlots();
        var arranged = _windowArranger.Arrange(_statusStore.Slots, _statusStore.Config.Gap);
        _statusStore.Message = arranged == 0
            ? "No assigned VS Code windows are available to arrange."
            : $"Arranged {arranged} VS Code window(s).";
    }

    private void FocusAllButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSlots();
        var focused = 0;
        foreach (var slot in _statusStore.Slots)
        {
            if (_windowArranger.Focus(slot.WindowHandle))
            {
                focused++;
            }
        }

        _statusStore.Message = focused == 0
            ? "No assigned VS Code windows are available to focus."
            : $"Restored {focused} VS Code window(s).";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSlots();
        _statusStore.Message = "Window status refreshed.";
    }

    private void FocusSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        RefreshSlots();
        _statusStore.Message = _windowArranger.Focus(slot.WindowHandle)
            ? $"Focused slot {slot.Name}."
            : $"Slot {slot.Name} has no live VS Code window.";
    }

    private void RefreshSlots()
    {
        _statusStore.RefreshWindowStatuses(_windowEnumerator);
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _isBusy = true;
        SetBusyState(true);

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            _statusStore.Message = ex.Message;
        }
        finally
        {
            _isBusy = false;
            SetBusyState(false);
        }
    }

    private void SetBusyState(bool busy)
    {
        LaunchButton.IsEnabled = !busy;
        ArrangeButton.IsEnabled = !busy;
        FocusAllButton.IsEnabled = !busy;
    }
}
