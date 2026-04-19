using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly CancellationTokenSource _refreshCancellation = new();
    private WindowSlot.SlotWindowLayerMode _managedWindowLayerMode = WindowSlot.SlotWindowLayerMode.Topmost;
    private int? _activeMonitorIndex;
    private bool _isBusy;
    private bool _isRefreshInFlight;
    private double _collapsedWindowHeight;
    private double _collapsedWindowMinHeight;
    private StoredPanelSlot? _pendingStoredPanelDeletion;

    public MainWindow()
    {
        InitializeComponent();

        var config = AppConfig.Load();
        _statusStore = new StatusStore(config);
        _vscodeLauncher = new VscodeLauncher(_windowEnumerator);
        DataContext = _statusStore;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();

        Loaded += async (_, _) =>
        {
            Topmost = true;
            _collapsedWindowHeight = Height;
            _collapsedWindowMinHeight = MinHeight;
            await RefreshSlotsAsync(allowDuringBusy: true);
            ApplyManagedWindowLayers();
            UpdateWindowHeightForStoredPanels(StoredPanelsExpander.IsExpanded, true);
        };
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            _statusStore.LoadSavedSettings();
            await RefreshSlotsAsync(allowDuringBusy: true);

            if (!_vscodeLauncher.IsCodeCommandAvailable(_statusStore.Config.CodeCommand))
            {
                _statusStore.Message = $"`{_statusStore.Config.CodeCommand}` が見つかりません。VS Codeのコマンドまたは設定を確認してください。";
                return;
            }

            _statusStore.Message = "未起動のVS Codeを起動しています...";
            var assignments = await _vscodeLauncher.LaunchMissingAsync(
                _statusStore.Slots,
                _statusStore.Config,
                CancellationToken.None);

            foreach (var assignment in assignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            await RefreshSlotsAsync(allowDuringBusy: true);

            if (assignments.Count > 0)
            {
                ArrangeSlotsOnActiveMonitor();
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = $"{assignments.Count}個のVS Codeを起動して2x2に配置しました。";
            }
            else
            {
                var arranged = ArrangeSlotsOnActiveMonitor();
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = arranged > 0
                    ? $"{arranged}個のVS Codeを2x2に配置しました。"
                    : "新しいVS Codeウィンドウは見つかりませんでした。";
            }
        });
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.SaveCurrentSettings();
        _statusStore.Message = "設定を保存しました。";
    }

    private async void LoadSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.LoadSavedSettings();
        await RefreshSlotsAsync(allowDuringBusy: true);
        _statusStore.Message = "設定を読み込みました。";
    }

    private void CloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        var closedSlots = new List<WindowSlot>();
        var closed = 0;
        foreach (var slot in _statusStore.Slots)
        {
            if (!_windowArranger.Close(slot.WindowHandle))
            {
                continue;
            }

            closedSlots.Add(slot);
            closed++;
        }

        if (closedSlots.Count > 0)
        {
            _statusStore.SaveCurrentSettings();
            foreach (var slot in closedSlots)
            {
                _statusStore.ClearWindow(slot);
            }
        }

        _statusStore.Message = closed == 0
            ? "閉じるVS Codeウィンドウがありません。"
            : $"{closed}個のVS Codeを閉じて設定を保存しました。";
    }

    private void SlotCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        if (IsInteractiveCardChild(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _statusStore.AcknowledgeAiStatus(slot);
        ToggleSlotFocus(slot);
    }

    private void ToggleSlotFocus(WindowSlot slot)
    {
        var previouslyFocusedSlot = _statusStore.Slots.FirstOrDefault(item => item.IsFocused);

        if (slot.IsFocused)
        {
            var arranged = ArrangeSlotsOnActiveMonitor(false);
            _statusStore.ClearFocusedSlot();
            BringPanelToFront();
            _statusStore.Message = arranged == 0
                ? "4分割表示に戻せるVS Codeウィンドウがありません。"
                : $"{arranged}個のVS Codeを4分割表示に戻しました。";
            return;
        }

        if (previouslyFocusedSlot is not null)
        {
            ArrangeSlotsOnActiveMonitor(false);
            _statusStore.ClearFocusedSlot();
            _windowArranger.SetBackmost(previouslyFocusedSlot.WindowHandle);
        }

        SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Topmost);
        if (_windowArranger.FocusMaximized(slot.WindowHandle))
        {
            _statusStore.SetFocusedSlot(slot);
            _statusStore.Message = $"スロット{slot.Name}をフォーカス表示しました。";
            return;
        }

        _statusStore.Message = $"スロット{slot.Name}のVS Codeウィンドウが見つかりません。";
    }

    private void PinAllTopButton_Click(object sender, RoutedEventArgs e)
    {
        if (SetManagedWindowLayer(WindowSlot.SlotWindowLayerMode.Topmost))
        {
            _statusStore.Message = "管理中のVS Codeを最前面にしました。";
            return;
        }

        _statusStore.Message = "最前面にできるVS Codeウィンドウがありません。";
    }

    private void SendAllBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_statusStore.Slots.Any(slot => slot.IsFocused))
        {
            _statusStore.ClearFocusedSlot();
            ArrangeSlotsOnActiveMonitor(false);
        }

        if (SetManagedWindowLayer(WindowSlot.SlotWindowLayerMode.Backmost))
        {
            _statusStore.Message = "管理中のVS Codeを最背面にしました。";
            return;
        }

        _statusStore.Message = "最背面にできるVS Codeウィンドウがありません。";
    }

    private void ToggleMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        var monitorCount = _windowArranger.GetMonitorCount();
        if (monitorCount <= 1)
        {
            _statusStore.Message = "利用可能なディスプレイが1枚のため切り替えできません。";
            return;
        }

        var nextMonitorIndex = (GetActiveMonitorIndex() + 1) % monitorCount;
        _activeMonitorIndex = nextMonitorIndex;

        var arranged = ArrangeSlotsOnActiveMonitor();
        _statusStore.ClearFocusedSlot();
        _statusStore.Message = arranged > 0
            ? $"{arranged}個のVS Codeを{_windowArranger.GetMonitorLabel(nextMonitorIndex)}に移動しました。"
            : $"次回の配置先を{_windowArranger.GetMonitorLabel(nextMonitorIndex)}に切り替えました。";
    }

    private void CloseSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        _statusStore.AcknowledgeAiStatus(slot);
        if (_windowArranger.Close(slot.WindowHandle))
        {
            _statusStore.CaptureWorkspacePath(slot);
            _statusStore.ClearWindow(slot);
            _statusStore.Message = $"スロット{slot.Name}を閉じました。";
            return;
        }

        _statusStore.Message = $"スロット{slot.Name}のVS Codeウィンドウが見つかりません。";
    }

    private void StoreSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        _statusStore.AcknowledgeAiStatus(slot);

        if (slot.WindowHandle != IntPtr.Zero && !_windowArranger.Close(slot.WindowHandle))
        {
            _statusStore.Message = $"スロット{slot.Name}を裏保存に移す前に VS Code を閉じられませんでした。";
            return;
        }

        _statusStore.CaptureWorkspacePath(slot);
        _statusStore.ClearFocusedSlot();
        if (!_statusStore.TryStoreSlotInBack(slot, out var storedPanel))
        {
            _statusStore.Message = _statusStore.StoredPanels.All(item => item.HasContent)
                ? "裏保存 Quartet が満杯のため保存できません。"
                : $"スロット{slot.Name}に裏保存できるワークスペースがありません。";
            return;
        }

        ArrangeSlotsOnActiveMonitor();
        _statusStore.Message = $"スロット{slot.Name}を{storedPanel!.Label}へ裏保存しました。";
    }

    private async void ShowStoredPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (sender is not Button { Tag: StoredPanelSlot storedPanel, CommandParameter: string targetSlotName })
        {
            return;
        }

        if (!storedPanel.HasContent)
        {
            _statusStore.Message = $"{storedPanel.Label} は空です。";
            return;
        }

        var targetSlot = _statusStore.FindSlot(targetSlotName);
        if (targetSlot is null)
        {
            return;
        }

        _statusStore.AcknowledgeAiStatus(targetSlot);
        await RunBusyAsync(async () =>
        {
            if (targetSlot.WindowHandle != IntPtr.Zero && !_windowArranger.Close(targetSlot.WindowHandle))
            {
                _statusStore.Message = $"スロット{targetSlot.Name}の現在の VS Code を閉じられないため入れ替えできません。";
                return;
            }

            _statusStore.CaptureWorkspacePath(targetSlot);
            _statusStore.ClearFocusedSlot();
            if (!_statusStore.TryShowStoredPanel(storedPanel, targetSlot, out var swappedVisiblePanel))
            {
                _statusStore.Message = $"{storedPanel.Label} をスロット{targetSlot.Name}へ表示できませんでした。";
                return;
            }

            var assignments = await _vscodeLauncher.LaunchMissingAsync(
                new[] { targetSlot },
                _statusStore.Config,
                CancellationToken.None);

            foreach (var assignment in assignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            await RefreshSlotsAsync(allowDuringBusy: true);
            ArrangeSlotsOnActiveMonitor();

            // VS Code が起動直後にウィンドウ位置を自己復元するケースに備えて再配置する
            await Task.Delay(500);
            ArrangeSlotsOnActiveMonitor();

            _statusStore.Message = assignments.Count > 0
                ? swappedVisiblePanel
                    ? $"{storedPanel.Label}をスロット{targetSlot.Name}へ表示し、元の内容は裏保存に戻しました。"
                    : $"{storedPanel.Label}をスロット{targetSlot.Name}へ表示しました。"
                : $"{storedPanel.Label}の設定をスロット{targetSlot.Name}へ移しましたが、VS Code ウィンドウの起動は確認できませんでした。";
        });
    }

    private void DeleteStoredPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (sender is not Button { Tag: StoredPanelSlot storedPanel } || !storedPanel.HasContent)
        {
            return;
        }

        _pendingStoredPanelDeletion = storedPanel;
        DeleteStoredPanelMessageText.Text = $"{storedPanel.Label} の保存内容を削除して空きスロットに戻します。";

        var detail = string.IsNullOrWhiteSpace(storedPanel.PanelTitle)
            ? storedPanel.ShortPath
            : storedPanel.PanelTitle;
        DeleteStoredPanelDetailText.Text = string.IsNullOrWhiteSpace(detail) || detail == "-"
            ? "この操作は取り消せません。"
            : detail;
        DeleteStoredPanelPathText.Text = string.IsNullOrWhiteSpace(storedPanel.WorkspacePath)
            ? "保存済みパスはありません。"
            : storedPanel.WorkspacePath;

        DeleteStoredPanelOverlay.Visibility = Visibility.Visible;
        CancelDeleteStoredPanelButton.Focus();
    }

    private void ConfirmDeleteStoredPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingStoredPanelDeletion is null)
        {
            HideDeleteStoredPanelDialog();
            return;
        }

        var label = _pendingStoredPanelDeletion.Label;
        _statusStore.ClearStoredPanel(_pendingStoredPanelDeletion);
        _statusStore.Message = $"{label} を空きスロットに戻しました。";
        HideDeleteStoredPanelDialog();
    }

    private void CancelDeleteStoredPanelButton_Click(object sender, RoutedEventArgs e)
    {
        HideDeleteStoredPanelDialog();
    }

    private void StoredPanelsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        UpdateWindowHeightForStoredPanels(true);
    }

    private void StoredPanelsExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        UpdateWindowHeightForStoredPanels(false);
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshSlotsAsync();
    }

    private async Task RefreshSlotsAsync(bool allowDuringBusy = false)
    {
        if (_refreshCancellation.IsCancellationRequested
            || _isRefreshInFlight
            || _isBusy && !allowDuringBusy)
        {
            return;
        }

        _isRefreshInFlight = true;
        try
        {
            await _statusStore.RefreshWindowStatusesAsync(_windowEnumerator, _refreshCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _isRefreshInFlight = false;
        }
    }

    private int ArrangeSlotsOnActiveMonitor(bool bringPanelAfterArrange = true)
    {
        var arranged = _windowArranger.Arrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex());
        ApplyManagedWindowLayers(bringPanelAfterArrange);
        return arranged;
    }

    private void ApplyManagedWindowLayers(bool bringPanelAfterChange = true)
    {
        SetManagedWindowLayer(_managedWindowLayerMode, bringPanelAfterChange);
    }

    private void SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode layerMode)
    {
        _managedWindowLayerMode = layerMode;
        foreach (var slot in _statusStore.Slots)
        {
            slot.WindowLayerMode = layerMode;
        }
    }

    private bool SetManagedWindowLayer(WindowSlot.SlotWindowLayerMode layerMode, bool bringPanelAfterChange = true)
    {
        SetManagedWindowLayerState(layerMode);
        var appliedAny = false;

        foreach (var slot in _statusStore.Slots)
        {
            appliedAny |= ApplyLayerToSlot(slot, layerMode, false);
        }

        if (bringPanelAfterChange)
        {
            BringPanelToFront();
        }

        return appliedAny;
    }

    private bool ApplyLayerToSlot(WindowSlot slot, WindowSlot.SlotWindowLayerMode layerMode, bool bringPanelAfterChange = true)
    {
        if (slot.WindowHandle == IntPtr.Zero)
        {
            slot.WindowLayerMode = layerMode;
            return false;
        }

        var applied = layerMode switch
        {
            WindowSlot.SlotWindowLayerMode.Topmost => _windowArranger.BringToFrontOnce(slot.WindowHandle),
            WindowSlot.SlotWindowLayerMode.Backmost => _windowArranger.SetBackmost(slot.WindowHandle),
            _ => false
        };

        if (bringPanelAfterChange)
        {
            BringPanelToFront();
        }

        return applied;
    }

    private void BringPanelToFront()
    {
        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle == IntPtr.Zero)
        {
            return;
        }

        if (!Topmost)
        {
            Topmost = true;
        }

        _windowArranger.BringToFront(panelHandle);
    }

    private void HideDeleteStoredPanelDialog()
    {
        _pendingStoredPanelDeletion = null;
        DeleteStoredPanelOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateWindowHeightForStoredPanels(bool isExpanded, bool force = false)
    {
        if (_collapsedWindowHeight <= 0)
        {
            _collapsedWindowHeight = Height;
        }

        if (_collapsedWindowMinHeight <= 0)
        {
            _collapsedWindowMinHeight = MinHeight;
        }

        if (!isExpanded)
        {
            MinHeight = _collapsedWindowMinHeight;
            if (force || Height > _collapsedWindowHeight)
            {
                Height = _collapsedWindowHeight;
            }

            return;
        }

        StoredPanelsExpanderContent.UpdateLayout();
        var extraHeight = StoredPanelsExpanderContent.ActualHeight;
        if (extraHeight <= 0)
        {
            extraHeight = StoredPanelsExpanderContent.DesiredSize.Height;
        }

        extraHeight = Math.Max(0, extraHeight + 12);
        var targetHeight = _collapsedWindowHeight + extraHeight;
        var targetMinHeight = _collapsedWindowMinHeight + extraHeight;

        Height = targetHeight;
        MinHeight = targetMinHeight;
    }

    private void InlineTitleTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || e.Key is not (Key.Return or Key.Enter))
        {
            return;
        }

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private static bool IsInteractiveCardChild(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBox)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private int GetActiveMonitorIndex()
    {
        if (_activeMonitorIndex.HasValue)
        {
            return _activeMonitorIndex.Value;
        }

        foreach (var slot in _statusStore.Slots)
        {
            var detectedMonitorIndex = _windowArranger.GetMonitorIndexForWindow(slot.WindowHandle);
            if (detectedMonitorIndex >= 0)
            {
                _activeMonitorIndex = detectedMonitorIndex;
                return detectedMonitorIndex;
            }
        }

        _activeMonitorIndex = _windowArranger.GetDefaultMonitorIndex(_statusStore.Config.Monitor);
        return _activeMonitorIndex.Value;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (DeleteStoredPanelOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideDeleteStoredPanelDialog();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshCancellation.Cancel();
        base.OnClosed(e);
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
        TopmostAllButton.IsEnabled = !busy;
        BackmostAllButton.IsEnabled = !busy;
        ToggleMonitorButton.IsEnabled = !busy;
        SaveSettingsButton.IsEnabled = !busy;
        LoadSettingsButton.IsEnabled = !busy;
        CloseAllButton.IsEnabled = !busy;
    }
}
