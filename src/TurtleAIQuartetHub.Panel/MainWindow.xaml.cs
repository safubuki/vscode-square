using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

public partial class MainWindow : Window
{
    private const double CompactWindowMinHeight = 72;
    private const double CompactWindowMinWidth = 320;
    private const double CompactWindowWidthScale = 0.58;
    private const string CompactModeGlyph = "\uE73F";
    private const string StandardModeGlyph = "\uE740";
    private static readonly TimeSpan PanelFrontRestoreDelay = TimeSpan.FromMilliseconds(80);
    private readonly WindowEnumerator _windowEnumerator = new();
    private readonly WindowArranger _windowArranger = new();
    private readonly WindowFrameOverlayManager _overlayManager;
    private readonly VscodeLauncher _vscodeLauncher;
    private readonly StatusStore _statusStore;
    private readonly DispatcherTimer _refreshTimer;
    private readonly CancellationTokenSource _refreshCancellation = new();
    private WindowSlot.SlotWindowLayerMode _managedWindowLayerMode = WindowSlot.SlotWindowLayerMode.Topmost;
    private int? _activeMonitorIndex;
    private bool _isBusy;
    private bool _isRefreshInFlight;
    private bool _areWindowsHidden;
    private bool _isCompactMode;
    private double _collapsedWindowHeight;
    private double _collapsedWindowMinHeight;
    private double _standardWindowWidth;
    private double _standardWindowHeight;
    private double _standardWindowMinWidth;
    private double _standardWindowMinHeight;
    private StoredPanelSlot? _pendingStoredPanelDeletion;
    private Point _dragStartPoint;
    private CancellationTokenSource? _panelFrontRestoreCancellation;
    private CancellationTokenSource? _panelLocateCancellation;
    private CompactRoundTripState? _compactRoundTripState;
    private bool _compactRoundTripEligible;
    private bool _suppressPanelLocationTracking;

    public MainWindow()
    {
        InitializeComponent();

        var config = AppConfig.Load();
        _statusStore = new StatusStore(config);
        _overlayManager = new WindowFrameOverlayManager(_windowArranger);
        _vscodeLauncher = new VscodeLauncher(_windowEnumerator);
        DataContext = _statusStore;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
        LocationChanged += MainWindow_LocationChanged;

        Loaded += async (_, _) =>
        {
            Topmost = true;
            _collapsedWindowHeight = Height;
            _collapsedWindowMinHeight = MinHeight;
            RememberStandardWindowMetrics();
            UpdateDisplayModeChrome();
            await RefreshSlotsAsync(allowDuringBusy: true);
            ApplyManagedWindowLayers();
            UpdateWindowHeightForStoredPanels(StoredPanelsExpander.IsExpanded, true);
            UpdateCompactPanelFrame();
            RefreshAuxiliaryUi();
        };
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await LaunchAllMissingAsync();
    }

    private async Task LaunchAllMissingAsync()
    {
        if (_isBusy)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            // 非表示中にLaunchが押された場合は非表示状態を解除
            if (_areWindowsHidden)
            {
                _areWindowsHidden = false;
                ToggleVisibilityButton.Content = "非表示";
                foreach (var slot in _statusStore.Slots)
                {
                    slot.IsHidden = false;
                }
            }

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

    public void ExecuteExternalCommand(string[]? args)
    {
        _ = ExecuteExternalCommandAsync(args ?? []);
    }

    private async Task ExecuteExternalCommandAsync(string[] args)
    {
        ActivatePanelWindow();
        await RefreshSlotsAsync(allowDuringBusy: true);

        if (args.Length == 0)
        {
            RefreshAuxiliaryUi();
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "--activate":
                break;

            case "--locate":
                await LocatePanelAsync();
                break;

            case "--slot-toggle" when args.Length >= 2:
            {
                var slot = _statusStore.FindSlot(args[1]);
                if (slot is not null)
                {
                    HandleCompactSlotToggle(slot);
                }

                break;
            }

            case "--mode" when args.Length >= 2:
                SetCompactMode(string.Equals(args[1], "compact", StringComparison.OrdinalIgnoreCase));
                break;

            case "--launch-all":
                await LaunchAllMissingAsync();
                break;

            case "--layer" when args.Length >= 2 && string.Equals(args[1], "top", StringComparison.OrdinalIgnoreCase):
                PinAllTopButton_Click(this, new RoutedEventArgs());
                break;

            case "--layer" when args.Length >= 2 && string.Equals(args[1], "back", StringComparison.OrdinalIgnoreCase):
                SendAllBackButton_Click(this, new RoutedEventArgs());
                break;
        }

        RefreshAuxiliaryUi();
    }

    private void DisplayModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetCompactMode(!_isCompactMode);
        ActivatePanelWindow();
    }

    private void CompactSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        HandleCompactSlotToggle(slot);
    }

    private void HandleCompactSlotToggle(WindowSlot slot)
    {
        _statusStore.AcknowledgeAiStatus(slot);
        ActivatePanelWindow();

        if (slot.IsFocused)
        {
            ToggleSlotFocus(slot);
            return;
        }

        ToggleSlotFocus(slot);
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
        RefreshAuxiliaryUi();
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

    private void SlotCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void SlotCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (IsInteractiveCardChild(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) <= SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(diff.Y) <= SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        var dragData = new DataObject("WindowSlot", slot);
        DragDrop.DoDragDrop((DependencyObject)sender, dragData, DragDropEffects.Move);
    }

    private void SlotCard_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not Border border || !e.Data.GetDataPresent("WindowSlot"))
        {
            return;
        }

        var sourceSlot = e.Data.GetData("WindowSlot") as WindowSlot;
        var targetSlot = border.Tag as WindowSlot;
        if (sourceSlot is not null && targetSlot is not null && !ReferenceEquals(sourceSlot, targetSlot))
        {
            border.BorderBrush = (SolidColorBrush)FindResource("AccentBrush");
        }
    }

    private void SlotCard_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
        }
    }

    private void SlotCard_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("WindowSlot") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SlotCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
        }

        if (!e.Data.GetDataPresent("WindowSlot"))
        {
            return;
        }

        var sourceSlot = e.Data.GetData("WindowSlot") as WindowSlot;
        var targetSlot = (sender as FrameworkElement)?.Tag as WindowSlot;

        if (sourceSlot is null || targetSlot is null || ReferenceEquals(sourceSlot, targetSlot))
        {
            return;
        }

        _statusStore.SwapSlotContents(sourceSlot, targetSlot);
        if (!_areWindowsHidden)
        {
            ArrangeSlotsOnActiveMonitor();
        }

        _statusStore.Message = $"スロット{sourceSlot.Name}とスロット{targetSlot.Name}のカードを入れ替えました。";
        RefreshAuxiliaryUi();
        e.Handled = true;
    }

    private void ToggleSlotFocus(WindowSlot slot)
    {
        var previouslyFocusedSlot = _statusStore.Slots.FirstOrDefault(item => item.IsFocused);
        _overlayManager.HideAll();

        if (slot.IsFocused)
        {
            if (!_areWindowsHidden)
            {
                CapturePreferredLayout(slot);
                var arranged = ArrangeSlotsOnActiveMonitor(false);
                _statusStore.ClearFocusedSlot();
                SchedulePanelToFront();
                _statusStore.Message = arranged == 0
                    ? "4分割表示に戻せるVS Codeウィンドウがありません。"
                    : $"{arranged}個のVS Codeを4分割表示に戻しました。";
            }
            else
            {
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = "フォーカスを解除しました。";
            }

            RefreshAuxiliaryUi();
            return;
        }

        if (_areWindowsHidden)
        {
            foreach (var s in _statusStore.Slots)
            {
                _windowArranger.Restore(s.WindowHandle);
            }

            RestoreHiddenWindowState();
        }

        if (previouslyFocusedSlot is not null)
        {
            CapturePreferredLayout(previouslyFocusedSlot);
            ArrangeSlotsOnActiveMonitor(false);
            _statusStore.ClearFocusedSlot();
            _windowArranger.SetBackmost(previouslyFocusedSlot.WindowHandle);
        }

        EnsurePreferredLayout(slot);
        VscodeLayoutState.TryApplyPreferredLayout(slot, _statusStore.Config, slot.PreferredLayout);
        SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Topmost);
        if (_windowArranger.FocusMaximized(slot.WindowHandle))
        {
            _statusStore.SetFocusedSlot(slot);
            SchedulePanelToFront();
            _statusStore.Message = $"スロット{slot.Name}をフォーカス表示しました。";
            RefreshAuxiliaryUi();
            return;
        }

        _statusStore.Message = $"スロット{slot.Name}のVS Codeウィンドウが見つかりません。";
        RefreshAuxiliaryUi();
    }

    private void PinAllTopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_areWindowsHidden)
        {
            SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Topmost);
            _statusStore.Message = "最前面に設定しました（表示時に反映されます）。";
            return;
        }

        if (SetManagedWindowLayer(WindowSlot.SlotWindowLayerMode.Topmost))
        {
            _statusStore.Message = "管理中のVS Codeを最前面にしました。";
            return;
        }

        _statusStore.Message = "最前面にできるVS Codeウィンドウがありません。";
    }

    private void SendAllBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_areWindowsHidden)
        {
            SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Backmost);
            _statusStore.Message = "最背面に設定しました（表示時に反映されます）。";
            return;
        }

        if (_statusStore.Slots.Any(slot => slot.IsFocused))
        {
            CaptureFocusedLayout();
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

    private void ToggleVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_areWindowsHidden)
        {
            _areWindowsHidden = false;
            ToggleVisibilityButton.Content = "非表示";
            foreach (var slot in _statusStore.Slots)
            {
                slot.IsHidden = false;
            }

            var arranged = ArrangeSlotsOnActiveMonitor();

            _statusStore.Message = arranged > 0
                ? $"{arranged}個のVS Codeを表示しました。"
                : "表示できるVS Codeウィンドウがありません。";
        }
        else
        {
            CaptureFocusedLayout();
            var minimized = 0;
            foreach (var slot in _statusStore.Slots)
            {
                if (_windowArranger.Minimize(slot.WindowHandle))
                {
                    minimized++;
                    slot.IsHidden = true;
                }
            }

            if (minimized > 0)
            {
                _areWindowsHidden = true;
                ToggleVisibilityButton.Content = "表示";
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = $"{minimized}個のVS Codeを非表示にしました。";
            }
            else
            {
                _statusStore.Message = "非表示にできるVS Codeウィンドウがありません。";
            }
        }

        RefreshAuxiliaryUi();
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

        if (_areWindowsHidden)
        {
            _statusStore.Message = $"配置先を{_windowArranger.GetMonitorLabel(nextMonitorIndex)}に切り替えました（表示時に反映されます）。";
            return;
        }

        CaptureFocusedLayout();
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
            RefreshAuxiliaryUi();
            return;
        }

        _statusStore.Message = $"スロット{slot.Name}のVS Codeウィンドウが見つかりません。";
        RefreshAuxiliaryUi();
    }

    private async void SlotMainActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        if (slot.WindowStatus == SlotWindowStatus.Ready)
        {
            // 停止: 既存の CloseSlotButton_Click と同じ
            CloseSlotButton_Click(sender, e);
            return;
        }

        if (slot.WindowStatus != SlotWindowStatus.Missing)
        {
            return;
        }

        // 新規: 空きスロットにデフォルト名を設定
        if (!slot.HasPanelContent)
        {
            var defaultTitle = $"スロット{slot.Name}";
            slot.PanelTitle = _statusStore.MakeUniqueTitle(defaultTitle, slot);
        }

        // 起動 / 新規: 個別に VS Code を起動
        await RunBusyAsync(async () =>
        {
            if (_areWindowsHidden)
            {
                _areWindowsHidden = false;
                ToggleVisibilityButton.Content = "非表示";
                foreach (var s in _statusStore.Slots)
                {
                    s.IsHidden = false;
                }
            }

            _statusStore.Message = $"スロット{slot.Name}のVS Codeを起動しています...";
            var assignments = await _vscodeLauncher.LaunchMissingAsync(
                new[] { slot },
                _statusStore.Config,
                CancellationToken.None);

            foreach (var assignment in assignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            await RefreshSlotsAsync(allowDuringBusy: true);
            ArrangeSlotsOnActiveMonitor();

            await Task.Delay(500);
            ArrangeSlotsOnActiveMonitor();

            _statusStore.Message = assignments.Count > 0
                ? $"スロット{slot.Name}のVS Codeを起動しました。"
                : $"スロット{slot.Name}のVS Codeウィンドウの起動を確認できませんでした。";
        });
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
            _statusStore.Message = $"スロット{slot.Name}を控えに移す前に VS Code を閉じられませんでした。";
            return;
        }

        _statusStore.CaptureWorkspacePath(slot);
        _statusStore.ClearFocusedSlot();
        if (!_statusStore.TryStoreSlotInBack(slot, out var storedPanel))
        {
            _statusStore.Message = _statusStore.StoredPanels.All(item => item.HasContent)
                ? "控え Quartet が満杯のため保存できません。"
                : $"スロット{slot.Name}に控え保存できるワークスペースがありません。";
            return;
        }

        if (!_areWindowsHidden)
        {
            ArrangeSlotsOnActiveMonitor();
        }

        _statusStore.Message = $"スロット{slot.Name}を{storedPanel!.Label}へ控え保存しました。";
        RefreshAuxiliaryUi();
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

            if (_areWindowsHidden)
            {
                // 非表示中は起動したウィンドウも最小化して非表示を維持
                foreach (var assignment in assignments)
                {
                    _windowArranger.Minimize(assignment.Slot.WindowHandle);
                    assignment.Slot.IsHidden = true;
                }
            }
            else
            {
                ArrangeSlotsOnActiveMonitor();

                // VS Code が起動直後にウィンドウ位置を自己復元するケースに備えて再配置する
                await Task.Delay(500);
                ArrangeSlotsOnActiveMonitor();
            }

            _statusStore.Message = assignments.Count > 0
                ? swappedVisiblePanel
                    ? $"{storedPanel.Label}をスロット{targetSlot.Name}へ表示し、元の内容は控えに戻しました。"
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
        RefreshAuxiliaryUi();
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
            RefreshAuxiliaryUi();
        }
    }

    private int ArrangeSlotsOnActiveMonitor(bool bringPanelAfterArrange = true)
    {
        var arranged = _windowArranger.Arrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex());
        ApplyManagedWindowLayers(bringPanelAfterArrange, refreshOverlayAfterChange: false);
        RefreshAuxiliaryUi();
        return arranged;
    }

    private void ApplyManagedWindowLayers(bool bringPanelAfterChange = true, bool refreshOverlayAfterChange = true)
    {
        SetManagedWindowLayer(_managedWindowLayerMode, bringPanelAfterChange, refreshOverlayAfterChange);
    }

    private void SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode layerMode)
    {
        _managedWindowLayerMode = layerMode;
        foreach (var slot in _statusStore.Slots)
        {
            slot.WindowLayerMode = layerMode;
        }
    }

    private bool SetManagedWindowLayer(
        WindowSlot.SlotWindowLayerMode layerMode,
        bool bringPanelAfterChange = true,
        bool refreshOverlayAfterChange = true)
    {
        SetManagedWindowLayerState(layerMode);
        var appliedAny = false;

        foreach (var slot in _statusStore.Slots)
        {
            appliedAny |= ApplyLayerToSlot(slot, layerMode, false);
        }

        if (bringPanelAfterChange)
        {
            SchedulePanelToFront();
        }

        if (refreshOverlayAfterChange)
        {
            RefreshOverlayUi();
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
            SchedulePanelToFront();
        }

        return applied;
    }

    private void BringPanelToFront()
    {
        BringPanelToFrontImmediate();
    }

    private void ActivatePanelWindow()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        BringPanelToFront();
    }

    private void BringPanelToFrontImmediate()
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

    private void SetCompactMode(bool compact, bool updateMessage = true)
    {
        if (_isCompactMode == compact)
        {
            UpdateDisplayModeChrome();
            RefreshAuxiliaryUi();
            return;
        }

        if (!compact)
        {
            StopPanelLocateBlink();
            ClearCompactPanelFrame();
            Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
        }

        if (compact)
        {
            RememberStandardWindowMetrics();
        }

        _isCompactMode = compact;

        StandardSlotsGrid.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactBarPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        StoredPanelsExpander.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        FooterControlsGrid.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LaunchButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        if (compact)
        {
            MinWidth = CompactWindowMinWidth;
            var baseWidth = _standardWindowWidth > 0 ? _standardWindowWidth : Width;
            var compactWidth = Math.Max(CompactWindowMinWidth, Math.Round(baseWidth * CompactWindowWidthScale));
            Width = compactWidth;
            UpdateLayout();
            var compactHeight = GetCompactModeHeight();
            MinHeight = compactHeight;
            Height = compactHeight;

            if (TryGetReusableCompactBounds(out var compactBounds))
            {
                SetWindowBounds(compactBounds.Left, compactBounds.Top, Width, Height);
            }
            else
            {
                _compactRoundTripState = null;
                _compactRoundTripEligible = false;
            }
        }
        else
        {
            var compactBounds = GetCurrentWindowBounds();
            MinWidth = _standardWindowMinWidth > 0 ? _standardWindowMinWidth : MinWidth;
            MinHeight = _standardWindowMinHeight > 0 ? _standardWindowMinHeight : MinHeight;
            var targetWidth = _standardWindowWidth > 0
                ? Math.Max(MinWidth, _standardWindowWidth)
                : Width;
            var targetHeight = _standardWindowHeight > 0
                ? Math.Max(MinHeight, _standardWindowHeight)
                : Height;
            var targetBounds = GetStandardModeRestoreBounds(targetWidth, targetHeight);

            SetWindowBounds(targetBounds.Left, targetBounds.Top, targetBounds.Width, targetBounds.Height);
            _compactRoundTripState = new CompactRoundTripState(compactBounds, targetBounds);
            _compactRoundTripEligible = true;
        }

        UpdateDisplayModeChrome();
        UpdateCompactPanelFrame();
        RefreshAuxiliaryUi();

        if (updateMessage)
        {
            _statusStore.Message = compact
                ? "縮小表示に切り替えました。"
                : "標準表示に戻しました。";
        }
    }

    private void UpdateDisplayModeChrome()
    {
        DisplayModeButton.Content = _isCompactMode ? StandardModeGlyph : CompactModeGlyph;
        DisplayModeButton.ToolTip = _isCompactMode
            ? "標準表示へ戻す"
            : "縮小表示へ切り替え";
    }

    private void RememberStandardWindowMetrics()
    {
        if (_isCompactMode)
        {
            return;
        }

        _standardWindowWidth = Width;
        _standardWindowHeight = Height;
        _standardWindowMinWidth = MinWidth;
        _standardWindowMinHeight = MinHeight;
    }

    private double GetCompactModeHeight()
    {
        const double titleRowHeight = 26;
        const double edgePadding = 2;
        var compactPanelHeight = CompactBarPanel.ActualHeight + CompactBarPanel.Margin.Top + CompactBarPanel.Margin.Bottom;
        var rootMarginHeight = RootLayoutGrid.Margin.Top + RootLayoutGrid.Margin.Bottom;
        return Math.Max(CompactWindowMinHeight, Math.Ceiling(titleRowHeight + compactPanelHeight + rootMarginHeight + edgePadding));
    }

    private WindowArranger.WindowBounds GetCurrentWindowBounds()
    {
        return new WindowArranger.WindowBounds(
            (int)Math.Round(Left),
            (int)Math.Round(Top),
            (int)Math.Round(Width),
            (int)Math.Round(Height));
    }

    private bool TryGetReusableCompactBounds(out WindowArranger.WindowBounds compactBounds)
    {
        compactBounds = default;
        if (!_compactRoundTripEligible || _compactRoundTripState is not { } state)
        {
            return false;
        }

        if (!IsSameTopLeft(GetCurrentWindowBounds(), state.StandardBounds))
        {
            _compactRoundTripEligible = false;
            return false;
        }

        compactBounds = state.CompactBounds;
        return true;
    }

    private void SetWindowBounds(double left, double top, double width, double height)
    {
        _suppressPanelLocationTracking = true;
        try
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }
        finally
        {
            _suppressPanelLocationTracking = false;
        }
    }

    private static bool IsSameTopLeft(WindowArranger.WindowBounds current, WindowArranger.WindowBounds expected)
    {
        return Math.Abs(current.Left - expected.Left) <= 1
            && Math.Abs(current.Top - expected.Top) <= 1;
    }

    private async Task LocatePanelAsync()
    {
        if (!_isCompactMode)
        {
            return;
        }

        ActivatePanelWindow();
        StopPanelLocateBlink();

        var cancellation = new CancellationTokenSource();
        _panelLocateCancellation = cancellation;

        try
        {
            var endAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            var emphasis = true;
            while (DateTimeOffset.UtcNow < endAt && !cancellation.IsCancellationRequested)
            {
                UpdateCompactPanelFrame(emphasis ? PanelFrameVisual.Emphasis : PanelFrameVisual.Dimmed);
                emphasis = !emphasis;
                await Task.Delay(260, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_panelLocateCancellation, cancellation))
            {
                _panelLocateCancellation = null;
            }

            cancellation.Dispose();
            UpdateCompactPanelFrame();
        }
    }

    private void StopPanelLocateBlink()
    {
        _panelLocateCancellation?.Cancel();
        _panelLocateCancellation?.Dispose();
        _panelLocateCancellation = null;
    }

    private void UpdateCompactPanelFrame(PanelFrameVisual visual = PanelFrameVisual.Normal)
    {
        if (!_isCompactMode)
        {
            ClearCompactPanelFrame();
            return;
        }

        var color = visual switch
        {
            PanelFrameVisual.Emphasis => (Color)ColorConverter.ConvertFromString("#8AFCB7"),
            PanelFrameVisual.Dimmed => (Color)ColorConverter.ConvertFromString("#1F8E54"),
            _ => (Color)ColorConverter.ConvertFromString("#45D483")
        };

        var borderOpacity = visual switch
        {
            PanelFrameVisual.Emphasis => 1.0,
            PanelFrameVisual.Dimmed => 0.36,
            _ => 0.82
        };

        PanelFrameBorder.BorderBrush = new SolidColorBrush(color) { Opacity = borderOpacity };
        PanelFrameBorder.BorderThickness = new Thickness(1.5);
        PanelFrameBorder.Effect = null;
    }

    private void ClearCompactPanelFrame()
    {
        PanelFrameBorder.BorderBrush = Brushes.Transparent;
        PanelFrameBorder.BorderThickness = new Thickness(0);
        PanelFrameBorder.Effect = null;
    }

    private WindowArranger.WindowBounds GetStandardModeRestoreBounds(double targetWidth, double targetHeight)
    {
        var defaultBounds = new WindowArranger.WindowBounds(
            (int)Math.Round(Left),
            (int)Math.Round(Top),
            (int)Math.Round(targetWidth),
            (int)Math.Round(targetHeight));

        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle == IntPtr.Zero
            || !_windowArranger.TryGetMonitorWorkAreaForWindow(panelHandle, out var workArea)
            || !WouldClip(workArea, defaultBounds))
        {
            return defaultBounds;
        }

        var compactRight = Left + Width;
        var compactBottom = Top + Height;
        var anchorRight = Left + Width / 2 >= workArea.Left + workArea.Width / 2.0;
        var anchorBottom = Top + Height / 2 >= workArea.Top + workArea.Height / 2.0;

        var adjustedLeft = anchorRight ? compactRight - targetWidth : Left;
        var adjustedTop = anchorBottom ? compactBottom - targetHeight : Top;
        adjustedLeft = ClampToWorkArea(adjustedLeft, workArea.Left, workArea.Width, targetWidth);
        adjustedTop = ClampToWorkArea(adjustedTop, workArea.Top, workArea.Height, targetHeight);

        return new WindowArranger.WindowBounds(
            (int)Math.Round(adjustedLeft),
            (int)Math.Round(adjustedTop),
            (int)Math.Round(targetWidth),
            (int)Math.Round(targetHeight));
    }

    private static bool WouldClip(WindowArranger.WindowBounds workArea, WindowArranger.WindowBounds targetBounds)
    {
        return targetBounds.Left < workArea.Left
            || targetBounds.Top < workArea.Top
            || targetBounds.Left + targetBounds.Width > workArea.Left + workArea.Width
            || targetBounds.Top + targetBounds.Height > workArea.Top + workArea.Height;
    }

    private static double ClampToWorkArea(double value, int workAreaStart, int workAreaLength, double targetLength)
    {
        var min = workAreaStart;
        var max = workAreaStart + Math.Max(0, workAreaLength - targetLength);
        if (max < min)
        {
            return min;
        }

        return Math.Min(Math.Max(value, min), max);
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e)
    {
        if (_suppressPanelLocationTracking
            || _isCompactMode
            || !_compactRoundTripEligible)
        {
            return;
        }

        _compactRoundTripEligible = false;
    }

    private void RefreshAuxiliaryUi()
    {
        TaskbarJumpListService.Update(_statusStore.Slots, _isCompactMode);
        RefreshOverlayUi();
    }

    private void RefreshOverlayUi()
    {
        _overlayManager.Update(_statusStore.Slots, !_areWindowsHidden);
    }

    private void EnsurePreferredLayout(WindowSlot slot)
    {
        if (slot.PreferredLayout.HasAnyValue)
        {
            return;
        }

        if (VscodeLayoutState.TryReadLayoutPreference(slot, _statusStore.Config, out var preference))
        {
            _statusStore.UpdatePreferredLayout(slot, preference);
        }
    }

    private void CapturePreferredLayout(WindowSlot slot)
    {
        if (VscodeLayoutState.TryCapturePreferredLayout(slot, _statusStore.Config, _windowArranger, out var preference))
        {
            _statusStore.UpdatePreferredLayout(slot, preference);
        }
    }

    private void CaptureFocusedLayout()
    {
        var focusedSlot = _statusStore.Slots.FirstOrDefault(item => item.IsFocused);
        if (focusedSlot is not null)
        {
            CapturePreferredLayout(focusedSlot);
        }
    }

    private void SchedulePanelToFront(TimeSpan? delay = null)
    {
        _panelFrontRestoreCancellation?.Cancel();
        _panelFrontRestoreCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        _panelFrontRestoreCancellation = cancellation;
        _ = BringPanelToFrontAfterDelayAsync(delay ?? PanelFrontRestoreDelay, cancellation.Token);
    }

    private async Task BringPanelToFrontAfterDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    BringPanelToFrontImmediate();
                }
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HideDeleteStoredPanelDialog()
    {
        _pendingStoredPanelDeletion = null;
        DeleteStoredPanelOverlay.Visibility = Visibility.Collapsed;
    }

    private void RestoreHiddenWindowState()
    {
        if (!_areWindowsHidden)
        {
            return;
        }

        _areWindowsHidden = false;
        ToggleVisibilityButton.Content = "非表示";
        foreach (var slot in _statusStore.Slots)
        {
            slot.IsHidden = false;
        }

        RefreshAuxiliaryUi();
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

            RememberStandardWindowMetrics();
            return;
        }

        StoredPanelsExpanderContent.UpdateLayout();
        var extraHeight = StoredPanelsExpanderContent.ActualHeight;
        if (extraHeight <= 0)
        {
            extraHeight = StoredPanelsExpanderContent.DesiredSize.Height;
        }

        if (extraHeight <= 0)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => UpdateWindowHeightForStoredPanels(true));
            return;
        }

        extraHeight = Math.Max(0, extraHeight + 12);
        var targetHeight = _collapsedWindowHeight + extraHeight;
        var targetMinHeight = _collapsedWindowMinHeight + extraHeight;

        Height = targetHeight;
        MinHeight = targetMinHeight;
        RememberStandardWindowMetrics();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox textBox || textBox.IsReadOnly)
        {
            return;
        }

        if (IsSelfOrChild(e.OriginalSource as DependencyObject, textBox))
        {
            return;
        }

        FinishInlineTitleTextBoxEdit(textBox, commit: true, clearKeyboardFocus: true);
    }

    private void InlineTitleTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || e.Key is not (Key.Return or Key.Enter or Key.Escape))
        {
            return;
        }

        FinishInlineTitleTextBoxEdit(textBox, commit: e.Key != Key.Escape, clearKeyboardFocus: true);
        e.Handled = true;
    }

    private void InlineTitleTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || !textBox.IsReadOnly)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            textBox.IsReadOnly = false;
            textBox.Focusable = true;
            textBox.Cursor = Cursors.IBeam;
            textBox.Focus();
            textBox.SelectAll();
            e.Handled = true;
        }
    }

    private void InlineTitleTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        FinishInlineTitleTextBoxEdit(textBox, commit: true, clearKeyboardFocus: false);
    }

    private static void FinishInlineTitleTextBoxEdit(TextBox textBox, bool commit, bool clearKeyboardFocus)
    {
        if (commit)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
        else
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }

        textBox.IsReadOnly = true;
        textBox.Focusable = false;
        textBox.Cursor = Cursors.Arrow;

        if (clearKeyboardFocus)
        {
            Keyboard.ClearFocus();
        }
    }

    private static bool IsSelfOrChild(DependencyObject? source, DependencyObject target)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, target))
            {
                return true;
            }

            source = GetUiParent(source);
        }

        return false;
    }

    private static bool IsInteractiveCardChild(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase)
            {
                return true;
            }

            if (source is TextBox textBox && !textBox.IsReadOnly)
            {
                return true;
            }

            source = GetUiParent(source);
        }

        return false;
    }

    private static DependencyObject? GetUiParent(DependencyObject source)
    {
        if (source is FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }

        try
        {
            return VisualTreeHelper.GetParent(source);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 非表示中にアプリが終了される場合、VS Codeウィンドウを復元してから閉じる
        if (_areWindowsHidden)
        {
            foreach (var slot in _statusStore.Slots)
            {
                _windowArranger.Restore(slot.WindowHandle);
                slot.IsHidden = false;
            }

            _areWindowsHidden = false;
            ArrangeSlotsOnActiveMonitor(false);
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshCancellation.Cancel();
        _panelFrontRestoreCancellation?.Cancel();
        _panelFrontRestoreCancellation?.Dispose();
        StopPanelLocateBlink();
        _overlayManager.Dispose();
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
        ToggleVisibilityButton.IsEnabled = !busy;
        SaveSettingsButton.IsEnabled = !busy;
        LoadSettingsButton.IsEnabled = !busy;
        CloseAllButton.IsEnabled = !busy;
        DisplayModeButton.IsEnabled = !busy;
        CompactBarPanel.IsEnabled = !busy;
    }

    private readonly record struct CompactRoundTripState(
        WindowArranger.WindowBounds CompactBounds,
        WindowArranger.WindowBounds StandardBounds);

    private enum PanelFrameVisual
    {
        Normal,
        Dimmed,
        Emphasis
    }
}
