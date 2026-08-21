using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

public partial class MainWindow : Window
{
    private const double CompactWindowMinHeight = 146;
    private const double CompactWindowMinWidth = 430;
    private const double CompactWindowWidthScale = 0.64;
    private const double MicroWindowSize = 116;
    private const double ExitConfirmViewportWidth = 430;
    private const double ExitConfirmViewportHeight = 250;
    // \u7E2E\u5C0F=Tile, \u6975\u5C0F=AppIconDefault(2x2 \u30C9\u30C3\u30C8), \u6A19\u6E96=ShowResults\u3002
    // \u30DC\u30BF\u30F3\u306B\u306F\u300C\u6B21\u306B\u9077\u79FB\u3059\u308B\u30E2\u30FC\u30C9\u300D\u306E\u30A2\u30A4\u30B3\u30F3\u3092\u8868\u793A\u3059\u308B\u3002
    private const string CompactModeGlyph = "\uE73F";
    private const string MicroModeGlyph = "\uF158";
    private const string StandardModeGlyph = "\uE740";
    private static readonly TimeSpan PanelFrontRestoreDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan FocusSwitchArrangeDelay = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan FocusedSlotReassertDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FocusedSlotReassertInputSuppressWindow = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan DragDropFocusSuppressWindow = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan InteractiveRefreshSuppressWindow = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan LaunchAllSlotPaceDelay = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan LaunchRevealDelay = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan[] ImmediateArrangeSettleDelays =
    [
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(110),
        TimeSpan.FromMilliseconds(220),
        TimeSpan.FromMilliseconds(420),
        TimeSpan.FromMilliseconds(720)
    ];
    // 最大化アニメーションと背面整列が完了した後だけ Z 順を読み取り確認する。
    // 正常時は一切 SetWindowPos せず、外部ウィンドウごとの非同期処理が遅れて
    // 4 面を前へ出した場合だけ補正する。
    private static readonly TimeSpan[] FocusedZOrderVerificationDelays =
    [
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(220)
    ];
    private static readonly TimeSpan[] PostLaunchArrangeRetryDelays =
    [
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromMilliseconds(3000),
        TimeSpan.FromMilliseconds(5000),
        TimeSpan.FromMilliseconds(8000),
        TimeSpan.FromMilliseconds(12000),
        TimeSpan.FromMilliseconds(20000),
        TimeSpan.FromMilliseconds(30000)
    ];
    private readonly WindowEnumerator _windowEnumerator = new();
    private readonly WindowArranger _windowArranger = new();
    private readonly FocusNameOverlay _focusNameOverlay;
    private readonly VscodeLauncher _vscodeLauncher;
    private readonly ApplicationLauncher _applicationLauncher;
    private readonly StatusStore _statusStore;
    private readonly DispatcherTimer _refreshTimer;
    private readonly CancellationTokenSource _refreshCancellation = new();
    private WindowSlot.SlotWindowLayerMode _managedWindowLayerMode = WindowSlot.SlotWindowLayerMode.Topmost;
    private int? _activeMonitorIndex;
    private bool _isBusy;
    private bool _isRefreshInFlight;
    private bool _areWindowsHidden;
    private DisplayMode _displayMode = DisplayMode.Standard;
    private double _collapsedWindowHeight;
    private double _collapsedWindowMinHeight;
    private double _standardWindowWidth;
    private double _standardWindowHeight;
    private double _standardWindowMinWidth;
    private double _standardWindowMinHeight;
    private WindowSlot? _overlayShownForSlot;
    private WindowSlot? _pendingSlotInfoClear;
    private StoredPanelSlot? _pendingStoredPanelDeletion;
    // 非表示（一括最小化）に入る直前のフォーカス（ディスプレイごとに 1 つ）を覚え、表示時に復帰する。
    private readonly List<WindowSlot> _hiddenFocusedSlots = [];
    private Point _dragStartPoint;
    private bool _isCardDragDropInProgress;
    private CancellationTokenSource? _panelFrontRestoreCancellation;
    private CancellationTokenSource? _focusSwitchArrangeCancellation;
    private long _focusTransitionVersion;
    private CancellationTokenSource? _focusedZOrderVerificationCancellation;
    private CancellationTokenSource? _focusedSlotReassertCancellation;
    private CancellationTokenSource? _panelLocateCancellation;
    private CancellationTokenSource? _postLaunchArrangeCancellation;
    private bool _isReassertingFocusedSlot;
    private bool _isWindowMoveOrResizeActive;
    private bool _wasMinimized;
    private DateTimeOffset _suppressFocusedSlotReassertUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _suppressSlotFocusFromDragUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _suppressPeriodicRefreshUntil = DateTimeOffset.MinValue;
    private bool _closeConfirmed;
    private string _closeRequestSource = "Alt+F4 またはシステムメニュー";
    private WindowArranger.WindowBounds? _boundsBeforeExitConfirm;
    private double _minWidthBeforeExitConfirm;
    private double _minHeightBeforeExitConfirm;

    public MainWindow()
    {
        InitializeComponent();

        var config = AppConfig.Load();
        _statusStore = new StatusStore(config);
        _vscodeLauncher = new VscodeLauncher(_windowEnumerator);
        _applicationLauncher = new ApplicationLauncher(_windowEnumerator, _vscodeLauncher);
        DataContext = _statusStore;

        // フォーカスしたスロット名を対象ディスプレイ中央へ数秒だけ出すオーバーレイ。
        // 各スロットの IsFocused 変化を購読し、true で表示・false で即消しを一元化する
        //（個々のフォーカス制御経路に手を入れずに済む）。
        _focusNameOverlay = new FocusNameOverlay(_windowArranger);
        foreach (var slot in _statusStore.Slots)
        {
            slot.PropertyChanged += Slot_PropertyChanged;
        }

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(config.StatusRefreshIntervalMilliseconds)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
        Activated += MainWindow_Activated;
        StateChanged += MainWindow_StateChanged;
        SizeChanged += MainWindow_SizeChanged;
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
            RefreshLaunchButtonAvailability();
            ScheduleUnusedUserDataReclaim();
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

    private async void AuxiliaryApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: LauncherApplication application })
        {
            return;
        }

        await OpenAuxiliaryApplicationAsync(application);
    }

    private async Task OpenAuxiliaryApplicationAsync(LauncherApplication application)
    {
        if (!application.IsAvailable)
        {
            _statusStore.Message = application.ToolTip;
            return;
        }

        await RunBusyAsync(async () =>
        {
            _statusStore.Message = $"{application.DisplayName} を起動しています...";
            var result = await _applicationLauncher.LaunchSingleWindowApplicationAsync(
                application,
                _statusStore.Config,
                CancellationToken.None);
            _statusStore.Message = result.Message;
        });
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
                _hiddenFocusedSlots.Clear();
                UpdateVisibilityButtonVisual();
                foreach (var slot in _statusStore.Slots)
                {
                    slot.IsHidden = false;
                }
            }

            _statusStore.LoadSavedSettings();
            await RefreshSlotsAsync(allowDuringBusy: true);

            var missingSlots = _statusStore.Slots
                .Where(slot => slot.WindowStatus == SlotWindowStatus.Missing)
                .ToList();
            if (missingSlots.Count == 0)
            {
                var arrangedOnly = ArrangeSlotsOnActiveMonitor();
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = arrangedOnly > 0
                    ? $"{arrangedOnly}個の管理中ウィンドウを2x2に配置しました。"
                    : "一括起動の対象スロットがありません。";
                return;
            }

            var launchTargets = new List<(WindowSlot Slot, LauncherApplication Application)>();
            var unavailableSlots = new List<string>();
            foreach (var slot in missingSlots)
            {
                var application = _statusStore.FindApplication(slot.ApplicationId);
                if (application is null
                    || !application.IsWorkspaceApplication
                    || !_applicationLauncher.CanLaunchWorkspaceApplication(application, _statusStore.Config))
                {
                    unavailableSlots.Add($"{slot.Name}:{application?.DisplayName ?? slot.ApplicationId}");
                    continue;
                }

                if (!slot.HasPanelContent)
                {
                    slot.PanelTitle = _statusStore.MakeUniqueTitle($"スロット{slot.Name}", slot);
                }

                launchTargets.Add((slot, application));
            }

            if (launchTargets.Count == 0)
            {
                _statusStore.Message = unavailableSlots.Count > 0
                    ? $"選択アプリが未検出のため起動できません: {string.Join(", ", unavailableSlots)}"
                    : "一括起動できるスロットがありません。";
                return;
            }

            _statusStore.Message = $"未起動スロットを選択アプリで起動しています... {BuildLaunchPlanSummary(launchTargets)}";
            var config = _statusStore.Config;
            var launchedAssignments = await LaunchTargetsSequentiallyAsync(launchTargets, config);

            await RefreshSlotsAsync(allowDuringBusy: true);

            var skippedMessage = unavailableSlots.Count > 0
                ? $" 未検出でスキップ: {string.Join(", ", unavailableSlots)}"
                : string.Empty;
            var readyLaunchTargetCount = launchTargets.Count(target => target.Slot.WindowStatus == SlotWindowStatus.Ready);
            var revealTask = RevealLaunchedWindowsAsync(launchedAssignments);
            if (readyLaunchTargetCount > 0)
            {
                await ArrangeSlotsOnActiveMonitorWithSettlingAsync();
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = $"{readyLaunchTargetCount}個のスロットを選択アプリで起動して2x2に配置しました。{skippedMessage}";
            }
            else
            {
                var arranged = ArrangeSlotsOnActiveMonitor();
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = arranged > 0
                    ? $"{arranged}個の管理中ウィンドウを2x2に配置しました。{skippedMessage}"
                    : $"新しい管理中ウィンドウは見つかりませんでした。{skippedMessage}";
            }

            await revealTask;
        });
    }

    private async Task<IReadOnlyList<WindowAssignment>> LaunchTargetsSequentiallyAsync(
        IReadOnlyList<(WindowSlot Slot, LauncherApplication Application)> launchTargets,
        AppConfig config)
    {
        var assignments = new List<WindowAssignment>();
        for (var index = 0; index < launchTargets.Count; index++)
        {
            var (slot, application) = launchTargets[index];
            _statusStore.Message = $"スロット{slot.Name}の{application.DisplayName}を順番に起動しています... ({index + 1}/{launchTargets.Count})";
            DiagnosticLog.Write($"Launch Quartet starting slot {slot.Name} sequentially with {application.DisplayName} ({index + 1}/{launchTargets.Count}).");

            var slotAssignments = await _applicationLauncher.LaunchMissingAsync(
                new[] { slot },
                config,
                application,
                CancellationToken.None);
            foreach (var assignment in slotAssignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
                assignments.Add(assignment);
            }

            if (index < launchTargets.Count - 1)
            {
                await Task.Delay(LaunchAllSlotPaceDelay);
            }
        }

        return assignments;
    }

    private async Task RevealLaunchedWindowsAsync(IReadOnlyList<WindowAssignment> assignments)
    {
        var cloakedAssignments = assignments
            .Where(assignment => assignment.WasCloakedForLaunch)
            .ToList();
        if (cloakedAssignments.Count == 0)
        {
            return;
        }

        // SetWindowPos の非同期配置と Electron の初回描画が DWM へ反映されるまで待ち、
        // 白い初期サーフェスではなく完成した象限を一度だけ表示する。
        await Task.Delay(LaunchRevealDelay);
        foreach (var assignment in cloakedAssignments)
        {
            WindowArranger.SetCloaked(assignment.Window.Handle, false);
        }

        BringPanelToFrontImmediate();
    }

    private static string BuildLaunchPlanSummary(IEnumerable<(WindowSlot Slot, LauncherApplication Application)> launchTargets)
    {
        return string.Join(
            " / ",
            launchTargets
                .GroupBy(item => item.Application.DisplayName)
                .Select(group => $"{group.Key} {group.Count()}個"));
    }

    private static string QuoteExplorerArgument(string path)
    {
        return $"\"{path.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private bool EnsureWorkspaceApplicationAvailable(LauncherApplication? application)
    {
        if (application is not null && _applicationLauncher.CanLaunchWorkspaceApplication(application, _statusStore.Config))
        {
            return true;
        }

        var appName = application?.DisplayName ?? "アプリケーション";
        var message = application?.ToolTip
            ?? $"{appName} が見つかりません。設定で実行ファイルまたはコマンドを指定してください。";
        _statusStore.Message = message;
        return false;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        CancelScheduledPanelFrontRestore();
        CancelScheduledFocusedSlotReassert();
        CancelScheduledPostLaunchArrange();
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CancelScheduledPanelFrontRestore();
        CancelScheduledFocusedSlotReassert();
        CancelScheduledPostLaunchArrange();
        _closeRequestSource = "タイトルバーの閉じるボタン";
        ShowAppExitConfirmDialog();
    }

    private void ShowAppExitConfirmDialog()
    {
        DiagnosticLog.Write(LogLevel.Info, $"Panel close requested via {_closeRequestSource}; awaiting confirmation.");
        EnsureExitConfirmViewport();
        AppExitConfirmOverlay.Visibility = Visibility.Visible;
        BringPanelToFrontImmediate();
        CancelAppExitButton.Focus();
    }

    private void HideAppExitConfirmDialog()
    {
        AppExitConfirmOverlay.Visibility = Visibility.Collapsed;
        RestoreBoundsAfterExitConfirm();
        _closeRequestSource = "Alt+F4 またはシステムメニュー";
    }

    private void EnsureExitConfirmViewport()
    {
        if (_boundsBeforeExitConfirm.HasValue)
        {
            return;
        }

        var panelHandle = new WindowInteropHelper(this).Handle;
        var scale = GetCurrentDpiScale(panelHandle);
        var currentBounds = GetCurrentWindowBounds();
        var requiredWidth = ExitConfirmViewportWidth * scale;
        var requiredHeight = ExitConfirmViewportHeight * scale;
        if (currentBounds.Width >= requiredWidth && currentBounds.Height >= requiredHeight)
        {
            return;
        }

        _boundsBeforeExitConfirm = currentBounds;
        _minWidthBeforeExitConfirm = MinWidth;
        _minHeightBeforeExitConfirm = MinHeight;

        var targetWidth = Math.Max(currentBounds.Width, requiredWidth);
        var targetHeight = Math.Max(currentBounds.Height, requiredHeight);
        var targetLeft = currentBounds.Left + (currentBounds.Width - targetWidth) / 2;
        var targetTop = currentBounds.Top + (currentBounds.Height - targetHeight) / 2;

        if (panelHandle != IntPtr.Zero
            && _windowArranger.TryGetMonitorWorkAreaForWindow(panelHandle, out var workArea))
        {
            targetWidth = Math.Min(targetWidth, workArea.Width);
            targetHeight = Math.Min(targetHeight, workArea.Height);
            targetLeft = ClampToWorkArea(targetLeft, workArea.Left, workArea.Width, targetWidth);
            targetTop = ClampToWorkArea(targetTop, workArea.Top, workArea.Height, targetHeight);
        }

        MinWidth = Math.Min(ExitConfirmViewportWidth, targetWidth / scale);
        MinHeight = Math.Min(ExitConfirmViewportHeight, targetHeight / scale);
        SetWindowBounds(targetLeft, targetTop, targetWidth, targetHeight);
    }

    private void RestoreBoundsAfterExitConfirm()
    {
        if (_boundsBeforeExitConfirm is not { } originalBounds)
        {
            return;
        }

        MinWidth = _minWidthBeforeExitConfirm;
        MinHeight = _minHeightBeforeExitConfirm;
        _boundsBeforeExitConfirm = null;
        SetWindowBounds(
            originalBounds.Left,
            originalBounds.Top,
            originalBounds.Width,
            originalBounds.Height);
    }

    private void CancelAppExitButton_Click(object sender, RoutedEventArgs e)
    {
        HideAppExitConfirmDialog();
        DiagnosticLog.Write(LogLevel.Info, "Panel close canceled by user.");
        ScheduleFocusedSlotReassert();
    }

    private void ConfirmAppExitButton_Click(object sender, RoutedEventArgs e)
    {
        _closeConfirmed = true;
        AppExitConfirmOverlay.Visibility = Visibility.Collapsed;
        DiagnosticLog.Write(LogLevel.Info, "Panel close confirmed by user.");
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

        if (!string.Equals(args[0], "--activate", StringComparison.OrdinalIgnoreCase))
        {
            SuppressFocusedSlotReassertForPanelInput();
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
            {
                var targetMode = args[1].ToLowerInvariant() switch
                {
                    "compact" => DisplayMode.Compact,
                    "micro" => DisplayMode.Micro,
                    _ => DisplayMode.Standard
                };
                SetDisplayMode(targetMode);
                break;
            }

            case "--launch-all":
                await LaunchAllMissingAsync();
                break;

            case "--launch-app" when args.Length >= 2:
            {
                var application = _statusStore.FindApplication(args[1]);
                if (application is not null)
                {
                    await OpenAuxiliaryApplicationAsync(application);
                }

                break;
            }

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
        SuppressFocusedSlotReassertForPanelInput();
        SetDisplayMode(GetNextDisplayMode(_displayMode));
        ActivatePanelWindow();
    }

    private void StandardJumpButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        SetDisplayMode(DisplayMode.Standard);
        ActivatePanelWindow();
    }

    private static DisplayMode GetNextDisplayMode(DisplayMode current)
    {
        return current switch
        {
            DisplayMode.Standard => DisplayMode.Compact,
            DisplayMode.Compact => DisplayMode.Micro,
            DisplayMode.Micro => DisplayMode.Standard,
            _ => DisplayMode.Standard
        };
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        HelpOverlay.Visibility = Visibility.Visible;
        CloseHelpDialogButton.Focus();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.ResetApplicationPathSettings();
        SettingsConfigPathText.Text = AppConfig.GetUserConfigPath();
        SettingsOverlay.Visibility = Visibility.Visible;
        CloseSettingsDialogButton.Focus();
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsDialog();
    }

    private void SaveApplicationPathSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.SaveApplicationPathSettings();
        SettingsConfigPathText.Text = AppConfig.GetUserConfigPath();
        _statusStore.Message = $"アプリケーションパス設定を保存しました: {AppConfig.GetUserConfigPath()}";
        RefreshAuxiliaryUi();
    }

    private void ReloadApplicationDetectionButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.ReloadApplicationsFromConfig();
        RefreshAuxiliaryUi();

        var available = _statusStore.Applications.Where(app => app.IsAvailable).ToList();
        var unavailable = _statusStore.Applications.Where(app => !app.IsAvailable).ToList();

        _statusStore.Message = $"再検出しました（起動可 {available.Count} / 未検出 {unavailable.Count}）。";

        string detail;
        if (_statusStore.Applications.Count == 0)
        {
            detail = "検出対象のアプリケーションがありません。設定でアプリやパスを登録してください。";
        }
        else
        {
            var lines = new List<string>
            {
                $"起動可 {available.Count} 件 / 未検出 {unavailable.Count} 件"
            };
            if (available.Count > 0)
            {
                lines.Add("");
                lines.Add("◯ 起動可:");
                lines.AddRange(available.Select(app => $"　・{app.DisplayName}"));
            }

            if (unavailable.Count > 0)
            {
                lines.Add("");
                lines.Add("× 未検出（設定でパス／コマンドを指定してください）:");
                lines.AddRange(unavailable.Select(app => $"　・{app.DisplayName}（{app.StatusText}）"));
            }

            detail = string.Join(Environment.NewLine, lines);
        }

        ShowMaintenanceResult("再検出が完了しました", detail);
    }

    private void RepairPanelStateButton_Click(object sender, RoutedEventArgs e)
    {
        var result = _statusStore.RepairPanelState();
        _statusStore.Message = result.Summary;
        RefreshAuxiliaryUi();

        string title;
        string detail;
        if (!result.HasChanges)
        {
            title = "不整合は見つかりませんでした";
            detail = "スロットと保存パネルの保存情報を点検しましたが、修正が必要な不整合はありませんでした。";
        }
        else
        {
            title = $"{result.TotalChanges} 件の不整合を修正しました";
            detail = string.Join(
                Environment.NewLine,
                $"・スロット表示の補正: {result.NormalizedVisible} 件",
                $"・保存パネルの補正: {result.NormalizedStored} 件",
                $"・不完全な保存パネルの削除: {result.ClearedIncomplete} 件",
                $"・重複した保存パネルの削除: {result.ClearedDuplicates} 件");
        }

        ShowMaintenanceResult(title, detail);
    }

    private void ShowMaintenanceResult(string title, string detail)
    {
        MaintenanceResultTitleText.Text = title;
        MaintenanceResultDetailText.Text = detail;
        MaintenanceResultOverlay.Visibility = Visibility.Visible;
    }

    private void CloseMaintenanceResultButton_Click(object sender, RoutedEventArgs e)
    {
        MaintenanceResultOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowTodayLogButton_Click(object sender, RoutedEventArgs e)
    {
        LoadTodayLogIntoView();
        LogOverlay.Visibility = Visibility.Visible;
    }

    private void ReloadLogButton_Click(object sender, RoutedEventArgs e)
    {
        LoadTodayLogIntoView();
    }

    // 表示中の本日分ログをクリップボードへコピーする。リモートではクリップボード操作が
    // 一時的に失敗することがあるため、例外はログして握り、アプリは落とさない。
    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        var text = LogContentTextBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            _statusStore.Message = "コピーするログがありません。";
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _statusStore.Message = "本日分のログをコピーしました。";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            DiagnosticLog.Write(ex);
            _statusStore.Message = "クリップボードへコピーできませんでした。";
        }
    }

    private void CloseLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogOverlay.Visibility = Visibility.Collapsed;
    }

    // panel.log を OS の関連付けで開く。関連付けが無い／リモートで開けない環境では
    // 保存フォルダをエクスプローラーで開く（さらに失敗してもメッセージだけ出してアプリは継続）。
    private void OpenLogFileButton_Click(object sender, RoutedEventArgs e)
    {
        var logPath = DiagnosticLog.FilePath;
        try
        {
            if (System.IO.File.Exists(logPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = logPath,
                    UseShellExecute = true
                });
                _statusStore.Message = $"ログを開きました: {logPath}";
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Write(ex);
        }

        TryOpenLogFolder(logPath);
    }

    private void TryOpenLogFolder(string logPath)
    {
        var folder = System.IO.Path.GetDirectoryName(logPath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            _statusStore.Message = "ログの保存場所を特定できませんでした。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = QuoteExplorerArgument(folder),
                UseShellExecute = false
            });
            _statusStore.Message = $"ログの保存フォルダを開きました: {folder}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Write(ex);
            _statusStore.Message = $"ログを開けませんでした。場所: {logPath}";
        }
    }

    // 本日分のログをオーバーレイの TextBox へ読み込む。末尾（最新）が見えるようキャレットを末尾へ。
    // 行数が多いと TextBox が重くなるうえ縦に伸びやすいので、直近 MaxDisplayedLogLines 行だけ表示する。
    private const int MaxDisplayedLogLines = 500;

    private void LoadTodayLogIntoView()
    {
        LogFilePathText.Text = DiagnosticLog.FilePath;
        var lines = DiagnosticLog.ReadTodayLines();
        if (lines.Count == 0)
        {
            LogContentTextBox.Text = "本日のログはまだありません。";
            LogContentTextBox.CaretIndex = 0;
            return;
        }

        var truncated = lines.Count > MaxDisplayedLogLines;
        var shown = truncated ? lines.Skip(lines.Count - MaxDisplayedLogLines).ToList() : lines;
        var body = string.Join(Environment.NewLine, shown);
        LogContentTextBox.Text = truncated
            ? $"（古い {lines.Count - MaxDisplayedLogLines} 行は省略。直近 {MaxDisplayedLogLines} 行を表示。全文は「ファイルを開く」から確認できます）"
                + Environment.NewLine + Environment.NewLine + body
            : body;
        LogContentTextBox.CaretIndex = LogContentTextBox.Text.Length;
        LogContentTextBox.ScrollToEnd();
    }

    private void CloseHelpButton_Click(object sender, RoutedEventArgs e)
    {
        HideHelpDialog();
    }

    private void HideHelpDialog()
    {
        HelpOverlay.Visibility = Visibility.Collapsed;
    }

    private void HideSettingsDialog()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void CompactSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        HandleCompactSlotToggle(slot);
    }

    /// <summary>
    /// 縮小モードのカード内にある起動/終了トグル。カード本体の Button に Click が
    /// バブリングしてフォーカス切替まで走るのを防ぐため、ここで Handled にしてから
    /// 標準モードと同じ SlotMainActionButton_Click に委譲する。
    /// </summary>
    private void CompactSlotPowerButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot })
        {
            return;
        }

        SlotMainActionButton_Click(sender, e);
    }

    private void MicroSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        HandleCompactSlotToggle(slot);
    }

    private void MicroVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleVisibilityButton_Click(sender, e);
    }

    private void CompactVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleVisibilityButton_Click(sender, e);
    }

    private void HandleCompactSlotToggle(WindowSlot slot)
    {
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
        if (_isBusy)
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();

        var closeTargets = _statusStore.Slots.Count(slot => slot.WindowHandle != IntPtr.Zero);
        if (closeTargets == 0)
        {
            _statusStore.Message = "閉じる管理中ウィンドウがありません。";
            RefreshAuxiliaryUi();
            return;
        }

        CloseAllConfirmCountText.Text = $"{closeTargets}個の管理中ウィンドウを閉じます。";
        CloseAllConfirmOverlay.Visibility = Visibility.Visible;
        CancelCloseAllButton.Focus();
    }

    private void ConfirmCloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        HideCloseAllConfirmDialog();
        CloseAllManagedWindows();
    }

    private void CancelCloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        HideCloseAllConfirmDialog();
    }

    private void CloseAllManagedWindows()
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
                slot.IsHidden = false;
                _statusStore.ClearWindow(slot);
            }
        }

        _areWindowsHidden = _statusStore.Slots.Any(slot => slot.WindowHandle != IntPtr.Zero && slot.IsHidden);
        if (!_areWindowsHidden)
        {
            _hiddenFocusedSlots.Clear();
        }

        UpdateVisibilityButtonVisual();

        _statusStore.Message = closed == 0
            ? "閉じる管理中ウィンドウがありません。"
            : $"{closed}個の管理中ウィンドウを閉じて設定を保存しました。";
        RefreshAuxiliaryUi();
    }

    private void SlotCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        if (IsSlotFocusSuppressedByDrag())
        {
            e.Handled = true;
            return;
        }

        if (IsInteractiveCardChild(e.OriginalSource as DependencyObject))
        {
            return;
        }

        ToggleSlotFocus(slot);
    }

    private void SlotCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void SlotCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsSlotFocusSuppressedByDrag())
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
        BeginCardDragDrop();
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, dragData, DragDropEffects.Move);
        }
        finally
        {
            EndCardDragDrop();
        }

        e.Handled = true;
    }

    private void SlotCard_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not Border border || !e.Data.GetDataPresent("WindowSlot"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
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

    private async void SlotCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
        }

        if (!e.Data.GetDataPresent("WindowSlot"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourceSlot = e.Data.GetData("WindowSlot") as WindowSlot;
        var targetSlot = (sender as FrameworkElement)?.Tag as WindowSlot;

        if (sourceSlot is null || targetSlot is null || ReferenceEquals(sourceSlot, targetSlot))
        {
            return;
        }

        _statusStore.SwapSlotContents(sourceSlot, targetSlot);
        UpdateDisplayBadges();
        _statusStore.Message = $"スロット{sourceSlot.Name}とスロット{targetSlot.Name}のカードを入れ替えました。";
        RefreshAuxiliaryUi();
        e.Handled = true;

        if (_areWindowsHidden)
        {
            return;
        }

        try
        {
            await ArrangeSlotsAfterPanelStateChangeWithSettlingAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private void StoredPanelCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void StoredPanelCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsSlotFocusSuppressedByDrag())
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

        if (sender is not FrameworkElement { Tag: StoredPanelSlot storedPanel } || !storedPanel.HasContent)
        {
            return;
        }

        var dragData = new DataObject("StoredPanelSlot", storedPanel);
        BeginCardDragDrop();
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, dragData, DragDropEffects.Move);
        }
        finally
        {
            EndCardDragDrop();
        }

        e.Handled = true;
    }

    private void BeginCardDragDrop()
    {
        _isCardDragDropInProgress = true;
        _suppressSlotFocusFromDragUntil = DateTimeOffset.UtcNow + DragDropFocusSuppressWindow;
        SuppressFocusedSlotReassertForPanelInput();
    }

    private void EndCardDragDrop()
    {
        _isCardDragDropInProgress = false;
        _suppressSlotFocusFromDragUntil = DateTimeOffset.UtcNow + DragDropFocusSuppressWindow;
    }

    private bool IsSlotFocusSuppressedByDrag()
    {
        return _isCardDragDropInProgress
            || DateTimeOffset.UtcNow < _suppressSlotFocusFromDragUntil;
    }

    private void StoredPanelCard_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not Border border || !e.Data.GetDataPresent("StoredPanelSlot"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPanel = border.Tag as StoredPanelSlot;
        if (sourcePanel is not null && targetPanel is not null && !ReferenceEquals(sourcePanel, targetPanel))
        {
            border.BorderBrush = (SolidColorBrush)FindResource("AccentBrush");
        }
    }

    private void StoredPanelCard_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
        }
    }

    private void StoredPanelCard_DragOver(object sender, DragEventArgs e)
    {
        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPanel = (sender as FrameworkElement)?.Tag as StoredPanelSlot;
        e.Effects = sourcePanel is not null
            && targetPanel is not null
            && !ReferenceEquals(sourcePanel, targetPanel)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void StoredPanelCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
        }

        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPanel = (sender as FrameworkElement)?.Tag as StoredPanelSlot;
        if (sourcePanel is null || targetPanel is null || ReferenceEquals(sourcePanel, targetPanel))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourceLabel = sourcePanel.Label;
        var targetLabel = targetPanel.Label;
        var targetHadContent = targetPanel.HasContent;
        _statusStore.SwapStoredPanels(sourcePanel, targetPanel);
        _statusStore.Message = targetHadContent
            ? $"{sourceLabel}と{targetLabel}の控えカードを入れ替えました。"
            : $"{sourceLabel}を{targetLabel}へ移動しました。";
        RefreshAuxiliaryUi();
        e.Handled = true;
    }

    private void StoredPanelTab_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element || !e.Data.GetDataPresent("StoredPanelSlot"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPage = element.DataContext as StoredPanelPage;
        if (sourcePanel is null || targetPage is null || targetPage.Slots.Contains(sourcePanel))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _statusStore.SelectStoredPanelPage(targetPage);
        if (sender is Control control)
        {
            control.BorderBrush = (SolidColorBrush)FindResource("AccentBrush");
            control.Background = (SolidColorBrush)FindResource("AccentDarkBrush");
        }

        e.Handled = true;
    }

    private void StoredPanelTab_DragLeave(object sender, DragEventArgs e)
    {
        ClearStoredPanelTabDragHighlight(sender);
    }

    private void StoredPanelTab_DragOver(object sender, DragEventArgs e)
    {
        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPage = (sender as FrameworkElement)?.DataContext as StoredPanelPage;
        e.Effects = sourcePanel is not null
            && targetPage is not null
            && !targetPage.Slots.Contains(sourcePanel)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void StoredPanelTab_Drop(object sender, DragEventArgs e)
    {
        ClearStoredPanelTabDragHighlight(sender);

        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPage = (sender as FrameworkElement)?.DataContext as StoredPanelPage;
        if (sourcePanel is null || targetPage is null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _statusStore.SelectStoredPanelPage(targetPage);
        var sourceLabel = sourcePanel.Label;
        if (_statusStore.TryMoveStoredPanelToPage(sourcePanel, targetPage, out var targetPanel, out var swapped)
            && targetPanel is not null)
        {
            _statusStore.Message = swapped
                ? $"{sourceLabel}を{targetPage.Header}へ移動し、{targetPanel.Label}と入れ替えました。"
                : $"{sourceLabel}を{targetPage.Header}の{targetPanel.Label}へ移動しました。";
            RefreshAuxiliaryUi();
        }

        e.Handled = true;
    }

    private void StoredPanelPageTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StoredPanelPage page)
        {
            _statusStore.SelectStoredPanelPage(page);
        }
    }

    /// <summary>
    /// タブをダブルクリックしたら、その場で名前を編集できるようにする。
    /// ボタンの上に重ねてある TextBox を前に出し、全選択した状態でフォーカスを移す。
    /// </summary>
    private void StoredPanelPageTab_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not StoredPanelPage page)
        {
            return;
        }

        var editBox = FindStoredPanelPageHeaderEditBox(button);
        if (editBox is null)
        {
            return;
        }

        _statusStore.SelectStoredPanelPage(page);

        editBox.Text = page.Header;
        editBox.Visibility = Visibility.Visible;

        // テンプレート適用直後はフォーカスを受け取れないことがあるので、レイアウト後に確定させる。
        editBox.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                editBox.Focus();
                editBox.SelectAll();
            }),
            DispatcherPriority.Input);

        e.Handled = true;
    }

    private void StoredPanelPageHeaderEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitStoredPanelPageHeaderEdit(editBox);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            // 取り消し。LostFocus 側で確定させないよう、先に編集欄を閉じる。
            editBox.Visibility = Visibility.Collapsed;
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void StoredPanelPageHeaderEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox editBox && editBox.Visibility == Visibility.Visible)
        {
            CommitStoredPanelPageHeaderEdit(editBox);
        }
    }

    private void CommitStoredPanelPageHeaderEdit(TextBox editBox)
    {
        editBox.Visibility = Visibility.Collapsed;

        if (editBox.DataContext is not StoredPanelPage page)
        {
            return;
        }

        var previousHeader = page.Header;
        _statusStore.RenameStoredPanelPage(page, editBox.Text);

        if (!string.Equals(previousHeader, page.Header, StringComparison.Ordinal))
        {
            _statusStore.Message = page.HasCustomHeader
                ? $"控えタブの名前を「{page.Header}」に変更しました。"
                : $"控えタブの名前を既定の「{page.Header}」に戻しました。";
        }

        if (editBox.IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
        }
    }

    /// <summary>タブのボタンと同じ DataTemplate 内に置かれた、名前編集用の TextBox を取り出す。</summary>
    private static TextBox? FindStoredPanelPageHeaderEditBox(Button tabButton)
    {
        return (VisualTreeHelper.GetParent(tabButton) as Grid)?
            .Children
            .OfType<TextBox>()
            .FirstOrDefault();
    }

    private static void ClearStoredPanelTabDragHighlight(object sender)
    {
        if (sender is not Control control)
        {
            return;
        }

        control.ClearValue(Control.BackgroundProperty);
        control.ClearValue(Control.BorderBrushProperty);
    }

    private async void ToggleSlotFocus(WindowSlot slot)
    {
        // 直前のフォーカス切替が予約した遅延整列を破棄してから新しい操作を始める。
        // 破棄しないと、古い整列が新フォーカスの 1 面表示を 4 面へ戻し、別スロットの 1 面だけが
        // 背面に残る不整合表示（1 面が背面・4 面が前面）になる。
        CancelFocusSwitchArrange();
        var focusTransitionVersion = ++_focusTransitionVersion;

        // フォーカスはディスプレイごとに 1 つ。対象スロットの実効ディスプレイに絞って操作する。
        var monitor = GetSlotMonitorIndex(slot);
        var previouslyFocusedOnDisplay = GetFocusedSlotOnMonitor(monitor);

        if (slot.IsFocused)
        {
            // フォーカス中スロットが管理外アプリ（チャットツール等）の背面に隠れているときの
            // クリックは「解除」ではなく「前面に呼び戻したい」意図とみなし、フォーカスを
            // 保ったまま前面化する（タスクバーのボタンと同じ状態依存動作）。
            // 前面に見えている状態でのクリックは従来どおりフォーカス解除。
            if (!_areWindowsHidden
                && slot.WindowHandle != IntPtr.Zero
                && _windowArranger.IsObscuredByExternalWindow(slot.WindowHandle, GetManagedWindowHandles()))
            {
                PrepareFocusTransitionBackdrop(slot, arrangeOtherSlotsFirst: false);
                await _windowArranger.FocusMaximizedOnMonitorAsync(slot.WindowHandle, monitor);
                if (focusTransitionVersion != _focusTransitionVersion)
                {
                    return;
                }

                _windowArranger.BringToFrontWithoutTopmost(slot.WindowHandle);
                BringPanelToFrontImmediate();
                SchedulePanelToFront();
                RefreshAuxiliaryUi();
                return;
            }

            // このディスプレイのフォーカスだけ解除し、その面を 4 面へ戻す。他ディスプレイは維持。
            if (!_areWindowsHidden)
            {
                CapturePreferredLayout(slot);
                PrepareFocusTransitionBackdrop(slot, arrangeOtherSlotsFirst: true);
                ClearFocusedSlotForDisplay(monitor);
                var arranged = ArrangeSlotsOnActiveMonitor(false, applyLayersAfterArrange: false);
                // 他ディスプレイのフォーカスは Arrange でタイル対象外だが、レイヤー再適用で前後が
                // 乱れ得るので前面に立て直す。
                if (FocusedSlots().Any())
                {
                    ReassertAllFocusedSlots();
                }

                SchedulePanelToFront();
                ScheduleFocusTransitionLayerFinalize();
                _statusStore.Message = arranged == 0
                    ? "4分割表示に戻せる管理中ウィンドウがありません。"
                    : $"{arranged}個の管理中ウィンドウを4分割表示に戻しました。";
            }
            else
            {
                ClearFocusedSlotForDisplay(monitor);
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

        if (previouslyFocusedOnDisplay is not null)
        {
            CapturePreferredLayout(previouslyFocusedOnDisplay);
        }

        EnsurePreferredLayout(slot);
        SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Topmost);
        PrepareFocusTransitionBackdrop(slot, arrangeOtherSlotsFirst: false);
        // 単独移動中ならその実効ディスプレイで最大化する。未移動なら従来どおりベース面で最大化。
        var focusActivated = await _windowArranger.FocusMaximizedOnMonitorAsync(slot.WindowHandle, monitor);
        if (focusTransitionVersion != _focusTransitionVersion)
        {
            return;
        }

        if (focusActivated)
        {
            // 新フォーカスを最大化する間、他スロットのタイルと前フォーカスの最大化はそのまま残して
            // 画面を覆わせておく。ここで他スロットを背面（HWND_BOTTOM）へ送ると、最大化アニメが
            // 画面を覆い切るまでの間、タイルの位置に管理外ウィンドウ（ブラウザ等）が透けて見える。
            // 背面送り・前フォーカスの復元・整列はすべてアニメ完了後に覆いの下で行う。
            SetFocusedSlotForDisplay(slot);
            SchedulePanelToFront();
            _statusStore.Message = $"スロット{slot.Name}をフォーカス表示しました。";
            RefreshAuxiliaryUi();

            // 遅延中に別スロットへフォーカスが切り替わったら、この後片付けは古い要求なので破棄する。
            var cancellation = new CancellationTokenSource();
            _focusSwitchArrangeCancellation = cancellation;
            try
            {
                await Task.Delay(FocusSwitchArrangeDelay, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(_focusSwitchArrangeCancellation, cancellation))
                {
                    _focusSwitchArrangeCancellation = null;
                }

                cancellation.Dispose();
            }

            // 遅延後に対象スロットがフォーカスから外れていれば、別操作が後勝ちしているので何もしない。
            if (slot.WindowHandle != IntPtr.Zero && slot.IsFocused)
            {
                // 最大化が完全に落ち着いてから、フォーカス対象を通常 Z 順の先頭に保ったまま
                // 覆いの下で旧フォーカスを quadrant へ静かに戻す。ここで他スロットを HWND_BOTTOM
                // へ送ると、隣接セルや 3 枚起動時に管理外ウィンドウの白い背景が露出しやすい。
                try
                {
                    ArrangeSlotsExceptOnActiveMonitor(slot, false);
                }
                finally
                {
                    EnsureFocusedSlotAboveTiles(slot);
                }

                ScheduleFocusedZOrderVerification(slot);
            }
            return;
        }

        _statusStore.Message = $"スロット{slot.Name}の{slot.ApplicationDisplayName}ウィンドウが見つかりません。";
        RefreshAuxiliaryUi();
    }

    private void PinAllTopButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        if (_areWindowsHidden)
        {
            SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Topmost);
            _statusStore.Message = "最前面に設定しました（表示時に反映されます）。";
            return;
        }

        // フォーカスモード中: フォーカスを維持したまま全スロットのレイヤーだけ変更。
        // FocusMaximized (SetForegroundWindow) は呼ばない。呼ぶとパネルと管理中ウィンドウの
        // アクティベーション争奪で無限ループしてハングする。
        ApplyLayerPreservingFocusMode(WindowSlot.SlotWindowLayerMode.Topmost);
        _statusStore.Message = "管理中ウィンドウを最前面にしました。";
    }

    private void SendAllBackButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        if (_areWindowsHidden)
        {
            SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Backmost);
            _statusStore.Message = "最背面に設定しました（表示時に反映されます）。";
            return;
        }

        // フォーカスモード中: フォーカスを維持したまま全スロットのレイヤーだけ変更。
        ApplyLayerPreservingFocusMode(WindowSlot.SlotWindowLayerMode.Backmost);
        _statusStore.Message = "管理中ウィンドウを最背面にしました。";
    }

    private void ExitFocusedModeForGlobalLayerChange()
    {
        if (!_statusStore.Slots.Any(slot => slot.IsFocused))
        {
            return;
        }

        CaptureFocusedLayout();
        _statusStore.ClearFocusedSlot();
        ArrangeSlotsOnActiveMonitor(false);
    }

    /// <summary>
    /// フォーカスモード中でも安全にレイヤーを変更する。
    /// フォーカス中は対象スロットを最後に前面化し、非フォーカススロットを背面に保つ。
    /// FocusMaximized (SetForegroundWindow) を呼ばないため、パネルと管理中ウィンドウ間の
    /// アクティベーション争奪による無限ループが発生しない。
    /// 4面表示・1面フォーカス表示のどちらでも動作する。
    /// </summary>
    private void ApplyLayerPreservingFocusMode(WindowSlot.SlotWindowLayerMode layerMode)
    {
        SetManagedWindowLayerState(layerMode);

        // まず非フォーカスの全スロットへレイヤーを適用する。これでフォーカスの無いディスプレイの
        // ウィンドウも確実に前面/背面化され、複数ディスプレイで最前面/最背面が機能する。
        foreach (var slot in _statusStore.Slots)
        {
            if (slot.IsFocused)
            {
                continue;
            }

            ApplyLayerToSlot(slot, layerMode, false);
        }

        // 次に、フォーカスを持つ各ディスプレイで、フォーカスと同面の前後関係を保ち直す。
        foreach (var focusedSlot in FocusedSlots().ToList())
        {
            ApplyLayerPreservingFocusedSlotOrder(focusedSlot);
        }

    }

    private void ApplyLayerPreservingFocusedSlotOrder(WindowSlot focusedSlot)
    {
        EnsureFocusedSlotAboveTiles(focusedSlot);
    }

    private async void ToggleVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        if (_areWindowsHidden)
        {
            RestoreHiddenWindows();

            if (TryRestoreHiddenFocusedSlot(out var restoredFocusedSlot))
            {
                _statusStore.Message = $"スロット{restoredFocusedSlot.Name}をフォーカス表示に戻しました。";
                RefreshAuxiliaryUi();
                return;
            }

            // 非表示中にディスプレイ切替した場合はここで別ディスプレイへ移動するため、
            // 移動先 DPI/解像度に合わせて遅延付きで再補正し、サイズくずれを防ぐ。
            var arranged = await ArrangeSlotsOnActiveMonitorWithSettlingAsync();
            _statusStore.Message = arranged > 0
                ? $"{arranged}個の管理中ウィンドウを表示しました。"
                : "表示できる管理中ウィンドウがありません。";
        }
        else
        {
            // フォーカスモード中の場合、先にフォーカスを解除してから最小化する。
            // ClearFocusedSlot を最小化の後に呼ぶと、最小化中にパネルがアクティブになり
            // ReassertFocusedSlotIfNeeded が走って FocusMaximized でウィンドウが復元され、無限ループになる。
            _hiddenFocusedSlots.Clear();
            _hiddenFocusedSlots.AddRange(_statusStore.Slots.Where(slot => slot.IsFocused && slot.WindowHandle != IntPtr.Zero));
            CaptureFocusedLayout();
            _statusStore.ClearFocusedSlot();

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
                UpdateVisibilityButtonVisual();
                _statusStore.Message = $"{minimized}個の管理中ウィンドウを非表示にしました。";
            }
            else
            {
                _hiddenFocusedSlots.Clear();
                _statusStore.Message = "非表示にできる管理中ウィンドウがありません。";
            }
        }

        RefreshAuxiliaryUi();
    }

    private async void ToggleMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        var monitorCount = _windowArranger.GetMonitorCount();
        if (monitorCount <= 1)
        {
            _statusStore.Message = "利用可能なディスプレイが1枚のため切り替えできません。";
            return;
        }

        var nextMonitorIndex = (GetActiveMonitorIndex() + 1) % monitorCount;
        _activeMonitorIndex = nextMonitorIndex;
        // ベースが進んだことで、その面へ単独移動していたスロットは「単独」ではなくなる。
        // override == 新ベースのスロットは解除し、以降は群れ（全移動の対象）として扱う。
        // これにより「2 に単独移動 → 全移動でベースが 1→2 → そのスロットは 2 に留まりつつ合流」
        // が成立する。単独移動が 2→1 に押し戻されるスワップは起こさない。
        CollapseMonitorOverridesMatchingBase();

        if (_areWindowsHidden)
        {
            UpdateDisplayBadges();
            _statusStore.Message = $"配置先を{_windowArranger.GetMonitorLabel(nextMonitorIndex)}に切り替えました（表示時に反映されます）。";
            return;
        }

        // フォーカスモード中の場合、先にフォーカスを解除してからArrangeする。
        // ClearFocusedSlot を後に呼ぶと、Arrange 中にパネルがアクティブ化して
        // ReassertFocusedSlotIfNeeded が走り、無限ループでハングする。
        // ただし単独移動中（override あり）のスロットはベース移動の影響を受けず
        // ディスプレイが変わらないため、その面のフォーカス（1 面表示）は維持し、
        // 配置後に立て直す。解除するのはベースに追従して移動するスロットだけ。
        foreach (var focusedSlot in FocusedSlots().ToList())
        {
            if (focusedSlot.MonitorOverride.HasValue)
            {
                continue;
            }

            CapturePreferredLayout(focusedSlot);
            focusedSlot.IsFocused = false;
        }

        // ディスプレイ間で DPI や解像度が異なる場合、単発の配置では移動直後に
        // WM_DPICHANGED が届いて各ウィンドウのサイズが上書きされ 2x2 がくずれる。
        // 起動時と同じ遅延付き再配置を使い、移動先ディスプレイの作業領域に合わせて
        // NeedsArrange でズレを検出したときだけ静かに再補正する。
        int arranged;
        if (FocusedSlots().Any())
        {
            // フォーカスが残る場合は settling 中も CanReapplyPostLaunchArrangement に
            // ブロックされないフォーカス対応版で配置し、各面の 1 面表示を立て直す。
            arranged = ArrangeSlotsOnActiveMonitor(false);
            ReassertAllFocusedSlots();
            SchedulePanelToFront();
            await SettleArrangementPreservingFocusAsync();
        }
        else
        {
            arranged = await ArrangeSlotsOnActiveMonitorWithSettlingAsync();
        }

        UpdateDisplayBadges();
        // 単独移動中のスロットはその面に留まるため「全部が移動した」とは限らない。
        _statusStore.Message = arranged > 0
            ? $"管理中ウィンドウを{_windowArranger.GetMonitorLabel(nextMonitorIndex)}基準に移動しました。"
            : $"次回の配置先を{_windowArranger.GetMonitorLabel(nextMonitorIndex)}に切り替えました。";
    }

    private async void MoveSlotToNextDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();

        var monitorCount = _windowArranger.GetMonitorCount();
        if (monitorCount <= 1)
        {
            _statusStore.Message = "利用可能なディスプレイが1枚のため切り替えできません。";
            return;
        }

        if (slot.WindowHandle == IntPtr.Zero)
        {
            _statusStore.Message = $"スロット{slot.Name}に移動できる管理中ウィンドウがありません。";
            return;
        }

        // 実効ディスプレイ（override ?? ベース）を次へ巡回。次がベースに一致したら override を
        // 解除して群れに戻す。2 枚なら「ベース ⇄ もう一方」のトグルになる。
        var baseIndex = NormalizeMonitorIndex(GetActiveMonitorIndex(), monitorCount);
        var current = NormalizeMonitorIndex(slot.MonitorOverride ?? baseIndex, monitorCount);
        var next = (current + 1) % monitorCount;
        slot.MonitorOverride = next == baseIndex ? null : next;
        UpdateDisplayBadges();

        if (_areWindowsHidden)
        {
            _statusStore.Message = $"スロット{slot.Name}の配置先を{_windowArranger.GetMonitorLabel(next)}に切り替えました（表示時に反映されます）。";
            return;
        }

        var destMonitor = GetSlotMonitorIndex(slot);

        if (slot.IsFocused)
        {
            // 移動するパネルは移動元でフォーカス（1 面）中。移動元の 1 面はこのパネルが去るので解ける。
            var destExistingFocus = GetFocusedSlotOnMonitor(destMonitor, except: slot);
            if (destExistingFocus is not null)
            {
                // 移動先が既にフォーカスを持つ → 移動先優先。移動したパネルはフォーカス解除し、
                // 移動先では非フォーカス（最大化中フォーカスの背面）になる。移動先のフォーカスは維持。
                slot.IsFocused = false;
                _statusStore.Message = $"スロット{slot.Name}を{_windowArranger.GetMonitorLabel(next)}へ移動しました（移動先のフォーカスを優先）。";
            }
            else
            {
                // 移動先にフォーカスが無い → フォーカスを引き継いで移動先で最大化する。
                SetFocusedSlotForDisplay(slot);
                _statusStore.Message = $"フォーカス中のスロット{slot.Name}を{_windowArranger.GetMonitorLabel(next)}へ移動しました。";
            }
        }
        else
        {
            _statusStore.Message = $"スロット{slot.Name}を{_windowArranger.GetMonitorLabel(next)}に移動しました。";
        }

        // まず移動先のフォーカスを先行して最大化する。await（settle 遅延）を挟む前に確定させることで、
        // 「移動先で 4 面のまま少し残ってから 1 面へ遷移」する見た目のラグを抑える。
        ReassertAllFocusedSlots();

        // 現在のフォーカス状態に合わせて全体を整える:
        //   非フォーカスは各実効ディスプレイの象限へタイル（Arrange は IsFocused をスキップ。
        //   フォーカスが去った移動元が 4 面へ戻るのもここで反映される）。
        //   DPI/解像度差は settling 付き再配置で吸収する（フォーカスがある面は最大化が吸収）。
        if (FocusedSlots().Any())
        {
            // フォーカスを保ったまま配置し、フォーカス対応の settling で DPI 差を補正する
            // （ArrangeSlotsOnActiveMonitorWithSettlingAsync の settling はフォーカス中に走らない）。
            ArrangeSlotsOnActiveMonitor(false);
            ReassertAllFocusedSlots();
            await SettleArrangementPreservingFocusAsync();
        }
        else
        {
            await ArrangeSlotsOnActiveMonitorWithSettlingAsync(false);
        }

        BringPanelToFrontImmediate();
        SchedulePanelToFront();
        UpdateDisplayBadges();
        RefreshAuxiliaryUi();
    }

    private void CloseSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        if (_windowArranger.Close(slot.WindowHandle))
        {
            _statusStore.CaptureWorkspacePath(slot);
            _statusStore.ClearWindow(slot);
            _statusStore.Message = $"スロット{slot.Name}を閉じました。";
            RefreshAuxiliaryUi();
            return;
        }

        _statusStore.Message = $"スロット{slot.Name}の{slot.ApplicationDisplayName}ウィンドウが見つかりません。";
        RefreshAuxiliaryUi();
    }

    private void OpenSlotWorkspaceFolderButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        var explorerPath = slot.ExplorerWorkspacePath;
        if (string.IsNullOrWhiteSpace(explorerPath))
        {
            _statusStore.Message = slot.WorkspaceFolderToolTip;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = QuoteExplorerArgument(explorerPath),
                UseShellExecute = false
            });
            _statusStore.Message = $"スロット {slot.Name} のフォルダを開きました: {explorerPath}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Write(ex);
            _statusStore.Message = $"フォルダを開けませんでした: {explorerPath}";
        }
    }

    private void ClearSlotPanelInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot slot } || !slot.HasPanelContent)
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();
        _pendingSlotInfoClear = slot;
        ClearSlotInfoMessageText.Text = $"スロット{slot.Name}の保存情報を削除して空のパネルに戻します。";
        var detail = string.IsNullOrWhiteSpace(slot.PanelTitle)
            ? slot.ShortPath
            : slot.PanelTitle;
        ClearSlotInfoDetailText.Text = string.IsNullOrWhiteSpace(detail) || detail == "-"
            ? "この操作は取り消せません。"
            : detail;
        ClearSlotInfoPathText.Text = string.IsNullOrWhiteSpace(slot.Path)
            ? "保存済みパスはありません。"
            : slot.Path;

        ClearSlotInfoOverlay.Visibility = Visibility.Visible;
        CancelClearSlotInfoButton.Focus();
    }

    private async void ConfirmClearSlotInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSlotInfoClear is null)
        {
            HideClearSlotInfoDialog();
            return;
        }

        var slot = _pendingSlotInfoClear;
        HideClearSlotInfoDialog();
        await ClearSlotPanelInfoAsync(slot);
    }

    private void CancelClearSlotInfoButton_Click(object sender, RoutedEventArgs e)
    {
        HideClearSlotInfoDialog();
    }

    private async Task ClearSlotPanelInfoAsync(WindowSlot slot)
    {
        if (_isBusy)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
        var hadWindow = slot.WindowHandle != IntPtr.Zero;
        if (hadWindow)
        {
            var windowHandle = slot.WindowHandle;
            if (_windowEnumerator.IsLiveWindow(windowHandle))
            {
                _statusStore.ClearFocusedSlot();
                _windowArranger.ReleaseTopmost(windowHandle);
                if (!_windowArranger.Close(windowHandle))
                {
                    _statusStore.Message = $"スロット{slot.Name}のウィンドウを閉じられなかったため、パネル情報は削除していません。";
                    return;
                }

                for (var attempt = 0; attempt < 12 && _windowEnumerator.IsLiveWindow(windowHandle); attempt++)
                {
                    await Task.Delay(100);
                }
            }
        }

        var slotName = slot.Name;
        _statusStore.ClearFocusedSlot();
        _statusStore.ClearSlotPanelInfo(slot);
        _areWindowsHidden = _statusStore.Slots.Any(item => item.WindowHandle != IntPtr.Zero && item.IsHidden);
        if (!_areWindowsHidden)
        {
            _hiddenFocusedSlots.Clear();
            ArrangeSlotsAfterPanelStateChange();
        }

        _statusStore.Message = hadWindow
            ? $"スロット{slotName}のウィンドウを閉じ、パネル情報をクリアしました。"
            : $"スロット{slotName}のパネル情報をクリアしました。";
        RefreshAuxiliaryUi();
        });
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

        var application = _statusStore.FindApplication(slot.ApplicationId);
        if (application is null || !application.IsWorkspaceApplication || !EnsureWorkspaceApplicationAvailable(application))
        {
            return;
        }

        // 新規: 空きスロットにデフォルト名を設定
        if (!slot.HasPanelContent)
        {
            var defaultTitle = $"スロット{slot.Name}";
            slot.PanelTitle = _statusStore.MakeUniqueTitle(defaultTitle, slot);
        }

        if (application is not null)
        {
            _statusStore.SetSlotApplication(slot, application);
        }

        // 起動 / 新規: 個別にワークスペースアプリを起動
        await LaunchSlotApplicationAsync(slot, application!);
    }

    private async Task LaunchSlotApplicationAsync(WindowSlot slot, LauncherApplication application)
    {
        await RunBusyAsync(async () =>
        {
            if (_areWindowsHidden)
            {
                _areWindowsHidden = false;
                _hiddenFocusedSlots.Clear();
                UpdateVisibilityButtonVisual();
                foreach (var s in _statusStore.Slots)
                {
                    s.IsHidden = false;
                }
            }

            _statusStore.Message = $"スロット{slot.Name}の{application.DisplayName}を起動しています...";
            var assignments = await _applicationLauncher.LaunchMissingAsync(
                new[] { slot },
                _statusStore.Config,
                application,
                CancellationToken.None);

            foreach (var assignment in assignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            await RefreshSlotsAsync(allowDuringBusy: true);
            var revealTask = RevealLaunchedWindowsAsync(assignments);
            await ArrangeSlotsOnActiveMonitorWithSettlingAsync();
            await revealTask;

            _statusStore.Message = assignments.Count > 0
                ? $"スロット{slot.Name}の{application.DisplayName}を起動しました。"
                : $"スロット{slot.Name}の{application.DisplayName}ウィンドウの起動を確認できませんでした。";
        });
    }

    private async void SlotApplicationSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (sender is not Button { Tag: SlotApplicationOption option })
        {
            return;
        }

        var slot = option.Slot;
        var application = _statusStore.FindApplication(option.ApplicationId);
        if (application is null || !application.IsWorkspaceApplication)
        {
            return;
        }

        if (!EnsureWorkspaceApplicationAvailable(application))
        {
            return;
        }

        if (slot.WindowStatus == SlotWindowStatus.Missing)
        {
            _statusStore.SetSlotApplication(slot, application);
            _statusStore.Message = $"スロット{slot.Name}の起動対象を {application.DisplayName} にしました。";
            RefreshLaunchButtonAvailability();
            RefreshAuxiliaryUi();
            return;
        }

        await SwitchSlotWorkspaceApplicationAsync(slot, application);
    }

    private async Task SwitchSlotWorkspaceApplicationAsync(WindowSlot slot, LauncherApplication application)
    {
        await RunBusyAsync(async () =>
        {
            var wasSameApplication = string.Equals(slot.ApplicationId, application.Id, StringComparison.OrdinalIgnoreCase);
            if (slot.WindowStatus == SlotWindowStatus.Ready && wasSameApplication)
            {
                ToggleSlotFocus(slot);
                return;
            }

            if (!await CloseSlotWindowForReplacementAsync(slot))
            {
                return;
            }

            if (slot.WindowHandle != IntPtr.Zero)
            {
                if (_statusStore.IsVsCodeSlot(slot))
                {
                    _statusStore.CaptureWorkspacePath(slot);
                }

                if (!_windowArranger.Close(slot.WindowHandle))
                {
                    _statusStore.Message = $"スロット{slot.Name}の現在のアプリを閉じられませんでした。";
                    return;
                }

                await Task.Delay(450);
                _statusStore.ClearWindow(slot);
            }

            if (!slot.HasPanelContent)
            {
                slot.PanelTitle = _statusStore.MakeUniqueTitle($"スロット{slot.Name}", slot);
            }

            _statusStore.SetSlotApplication(slot, application);
            _statusStore.Message = $"スロット{slot.Name}を{application.DisplayName}で開いています...";
            var assignments = await _applicationLauncher.LaunchMissingAsync(
                new[] { slot },
                _statusStore.Config,
                application,
                CancellationToken.None);

            foreach (var assignment in assignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            await RefreshSlotsAsync(allowDuringBusy: true);
            var revealTask = RevealLaunchedWindowsAsync(assignments);
            await ArrangeSlotsOnActiveMonitorWithSettlingAsync();
            await revealTask;

            _statusStore.Message = assignments.Count > 0
                ? $"スロット{slot.Name}を{application.DisplayName}で開きました。"
                : $"スロット{slot.Name}の{application.DisplayName}ウィンドウの起動を確認できませんでした。";
        });
    }

    private async Task<bool> CloseSlotWindowForReplacementAsync(WindowSlot slot)
    {
        var currentHandle = slot.WindowHandle;
        if (currentHandle == IntPtr.Zero)
        {
            return true;
        }

        _statusStore.CaptureWorkspacePath(slot);

        // 置き換え対象スロットがあるディスプレイのフォーカスだけ解除し、
        // 他ディスプレイの 1 面表示（フォーカス）は保つ。
        ClearFocusedSlotForDisplay(GetSlotMonitorIndex(slot));
        _windowArranger.ReleaseTopmost(currentHandle);

        if (_windowEnumerator.IsLiveWindow(currentHandle) && !_windowArranger.Close(currentHandle))
        {
            _statusStore.Message = $"スロット{slot.Name}の現在のウィンドウを閉じられませんでした。";
            return false;
        }

        for (var attempt = 0; attempt < 12 && _windowEnumerator.IsLiveWindow(currentHandle); attempt++)
        {
            await Task.Delay(100);
        }

        _statusStore.ClearWindow(slot);
        _areWindowsHidden = _statusStore.Slots.Any(item => item.WindowHandle != IntPtr.Zero && item.IsHidden);
        if (!_areWindowsHidden)
        {
            _hiddenFocusedSlots.Clear();
        }

        return true;
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

        if (slot.WindowHandle != IntPtr.Zero && !_windowArranger.Close(slot.WindowHandle))
        {
            _statusStore.Message = $"スロット{slot.Name}を控えに移す前に {slot.ApplicationDisplayName} を閉じられませんでした。";
            return;
        }

        _statusStore.CaptureWorkspacePath(slot);
        // 控えへ移すスロットがあるディスプレイのフォーカスだけ解除し、他ディスプレイは保つ。
        ClearFocusedSlotForDisplay(GetSlotMonitorIndex(slot));
        if (!_statusStore.TryStoreSlotInBack(slot, out var storedPanel))
        {
            _statusStore.Message = _statusStore.StoredPanels.All(item => item.HasContent)
                ? "控え Quartet が満杯のため保存できません。"
                : $"スロット{slot.Name}に控え保存できるワークスペースがありません。";
            return;
        }

        if (!_areWindowsHidden)
        {
            // 他ディスプレイのフォーカス（1 面表示）を保ったまま再配置する。
            ArrangeSlotsAfterPanelStateChange();
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

        var application = _statusStore.FindApplication(storedPanel.ApplicationId);
        if (application is null || !application.IsWorkspaceApplication || !EnsureWorkspaceApplicationAvailable(application))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (!await CloseSlotWindowForReplacementAsync(targetSlot))
            {
                return;
            }

            ClearFocusedSlotForDisplay(GetSlotMonitorIndex(targetSlot));
            if (!_statusStore.TryShowStoredPanel(storedPanel, targetSlot, out var swappedVisiblePanel))
            {
                _statusStore.Message = $"{storedPanel.Label} をスロット{targetSlot.Name}へ表示できませんでした。";
                return;
            }

            if (application is not null)
            {
                _statusStore.SetSlotApplication(targetSlot, application);
            }

            var assignments = await _applicationLauncher.LaunchMissingAsync(
                new[] { targetSlot },
                _statusStore.Config,
                application!,
                CancellationToken.None);

            foreach (var assignment in assignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            await RefreshSlotsAsync(allowDuringBusy: true);
            var revealTask = RevealLaunchedWindowsAsync(assignments);

            if (_areWindowsHidden)
            {
                // 非表示中は起動したウィンドウも最小化して非表示を維持
                foreach (var assignment in assignments)
                {
                    _windowArranger.Minimize(assignment.Slot.WindowHandle);
                    assignment.Slot.IsHidden = true;
                }
            }
            else if (FocusedSlots().Any())
            {
                // 他ディスプレイにフォーカス（1 面表示）が残っているときは、それを保ったまま
                // 非フォーカスのみ象限へ整列し、フォーカス対応の settling で DPI 差を補正する
                // （ArrangeSlotsOnActiveMonitorWithSettlingAsync の settling はフォーカス中に走らない）。
                ArrangeSlotsOnActiveMonitor(false);
                ReassertAllFocusedSlots();
                await SettleArrangementPreservingFocusAsync();
                SchedulePanelToFront();
            }
            else
            {
                // 起動直後にウィンドウ位置を自己復元するケースに備えて再配置する
                await ArrangeSlotsOnActiveMonitorWithSettlingAsync();
            }

            await revealTask;

            _statusStore.Message = assignments.Count > 0
                ? swappedVisiblePanel
                    ? $"{storedPanel.Label}をスロット{targetSlot.Name}へ表示し、元の内容は控えに戻しました。"
                    : $"{storedPanel.Label}をスロット{targetSlot.Name}へ表示しました。"
                : $"{storedPanel.Label}の設定をスロット{targetSlot.Name}へ移しましたが、{application!.DisplayName}ウィンドウの起動は確認できませんでした。";
        });
    }

    private void RegisterStoredPanelFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { Tag: StoredPanelSlot storedPanel })
        {
            return;
        }

        if (storedPanel.HasContent)
        {
            return;
        }

        // エクスプローラのフォルダ選択ダイアログ。ローカルフォルダのみ選択でき、リモート URI は指定できない。
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"{storedPanel.Label} に登録するワークスペースフォルダを選択",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folderPath = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(folderPath) || !System.IO.Directory.Exists(folderPath))
        {
            _statusStore.Message = "選択したフォルダを控えに登録できませんでした。";
            return;
        }

        if (!_statusStore.RegisterStoredPanelWorkspace(storedPanel, folderPath))
        {
            _statusStore.Message = "選択したフォルダを控えに登録できませんでした。";
            return;
        }

        _statusStore.Message = $"{storedPanel.Label} に「{storedPanel.DisplayTitle}」を登録しました。";
        RefreshAuxiliaryUi();
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

    private async void SettingsClearVisibleSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot slot } || !slot.HasPanelContent)
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();
        await ClearSlotPanelInfoAsync(slot);
    }

    private void SettingsClearStoredPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: StoredPanelSlot storedPanel } || !storedPanel.HasContent)
        {
            return;
        }

        _statusStore.ClearStoredPanel(storedPanel);
        _statusStore.Message = $"{storedPanel.Label} を空きスロットに戻しました。";
        RefreshAuxiliaryUi();
    }

    /// <summary>設定画面で名前欄を編集し終えたときに、その内容を保存する。</summary>
    private void SettingsStoredPageHeaderTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: StoredPanelPage page } textBox)
        {
            return;
        }

        // TextBox のバインディングが CustomHeader へ書き戻した後なので、保存だけ行う。
        _statusStore.RenameStoredPanelPage(page, page.CustomHeader);

        // 前後の空白や既定名そのものの入力は CustomHeader 側で空文字に正規化される。
        // その結果を入力欄へ映し直さないと、保存済みの値と表示がずれたままになる。
        if (!string.Equals(textBox.Text, page.CustomHeader, StringComparison.Ordinal))
        {
            textBox.Text = page.CustomHeader;
        }
    }

    /// <summary>この控えの名前だけを既定に戻す（他の控えの名前は変えない）。</summary>
    private void SettingsResetStoredPageHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StoredPanelPage page } || !page.HasCustomHeader)
        {
            return;
        }

        _statusStore.ResetStoredPanelPageHeader(page);
        _statusStore.Message = $"控えタブの名前を既定の「{page.Header}」に戻しました。";
    }

    private void CompactStoredPanelsButton_Click(object sender, RoutedEventArgs e)
    {
        var movedCount = _statusStore.CompactStoredPanels();

        _statusStore.Message = movedCount > 0
            ? $"控えの空きを詰めて整列しました（{movedCount} 件移動）。"
            : "控えはすでに整列済みです。詰める空きはありませんでした。";

        if (movedCount > 0 && _statusStore.StoredPanelPages.Count > 0)
        {
            _statusStore.SelectStoredPanelPage(_statusStore.StoredPanelPages[0]);
        }

        RefreshAuxiliaryUi();
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
        try
        {
            await RefreshSlotsAsync();
        }
        catch (Exception ex)
        {
            // async void のためここで捕捉しないと未処理例外でアプリごと落ちる。
            // 周期更新の 1 回失敗は致命的ではないので記録して次周回に任せる。
            DiagnosticLog.Write(ex);
        }
    }

    private async Task RefreshSlotsAsync(bool allowDuringBusy = false)
    {
        if (_refreshCancellation.IsCancellationRequested
            || _isRefreshInFlight
            || _isBusy && !allowDuringBusy)
        {
            return;
        }

        if (!allowDuringBusy && DateTimeOffset.UtcNow < _suppressPeriodicRefreshUntil)
        {
            return;
        }

        _isRefreshInFlight = true;
        try
        {
            await _statusStore.RefreshWindowStatusesAsync(_windowEnumerator, _refreshCancellation.Token);

            // フォーカス中（1 面）ウィンドウをアプリ外で直接最小化したケースを実体に合わせて回復する。
            // 回復したらこの周回の再接続・再配置は行わない（4 面へ戻した直後なのでズレ判定が不要）。
            if (ReconcileExternallyMinimizedFocusedSlot())
            {
                return;
            }

            var reattachedCount = ReattachExistingVsCodeWindowsToMissingSlots();
            if (reattachedCount > 0
                && CanReapplyPostLaunchArrangement()
                && _windowArranger.NeedsArrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex()))
            {
                await ArrangeSlotsOnActiveMonitorWithSettlingAsync();
            }

            // 遷移中は既存の背景保持を優先し、Z 順へ触れない。安定後の周期更新では
            // 実際に 4 面がフォーカス 1 面より上へ出た場合だけ、該当窓を背後へ戻す。
            if (_focusSwitchArrangeCancellation is null
                && _focusedZOrderVerificationCancellation is null)
            {
                RepairAllFocusedZOrdersIfNeeded();
            }
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

    private int ReattachExistingVsCodeWindowsToMissingSlots()
    {
        if (!_statusStore.Config.UseDedicatedUserDataDirs)
        {
            return 0;
        }

        var assignedHandles = _statusStore.Slots
            .Where(slot => slot.WindowHandle != IntPtr.Zero && _windowEnumerator.IsLiveWindow(slot.WindowHandle))
            .Select(slot => slot.WindowHandle)
            .ToHashSet();
        var reattachedCount = 0;

        foreach (var slot in _statusStore.Slots.Where(slot =>
                     slot.IsVsCodeApplication
                     && slot.WindowStatus == SlotWindowStatus.Missing))
        {
            var window = _vscodeLauncher.TryFindExistingSlotWindow(slot, _statusStore.Config);
            if (window is null || assignedHandles.Contains(window.Handle))
            {
                continue;
            }

            _statusStore.AssignWindow(slot, window);
            assignedHandles.Add(window.Handle);
            reattachedCount++;
        }

        if (reattachedCount > 0)
        {
            _statusStore.Message = $"{reattachedCount}個の既に開いていた VS Code ウィンドウをスロットへ再接続しました。";
        }

        return reattachedCount;
    }

    private int ArrangeSlotsOnActiveMonitor(bool bringPanelAfterArrange = true, bool applyLayersAfterArrange = true)
    {
        var arranged = _windowArranger.Arrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex());
        if (applyLayersAfterArrange)
        {
            ApplyManagedWindowLayers(bringPanelAfterArrange);
        }

        RefreshAuxiliaryUi();
        return arranged;
    }

    private int ArrangeSlotsOnActiveMonitorQuietly()
    {
        // settling 補正は演出ではないので、復元が必要でもアニメーションさせず無音で整える。
        return _windowArranger.Arrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex(), animateRestore: false);
    }

    private void ArrangeSlotsAfterPanelStateChange()
    {
        if (!FocusedSlots().Any())
        {
            ArrangeSlotsOnActiveMonitor();
            return;
        }

        // 非フォーカスを各ディスプレイの象限へ並べ（Arrange は IsFocused をスキップ）、
        // 各ディスプレイのフォーカスを最大化・前面に立て直す。
        ArrangeSlotsOnActiveMonitor(false);
        ReassertAllFocusedSlots();
        SchedulePanelToFront();
        RefreshAuxiliaryUi();
    }

    // パネル入替など、フォーカス（各ディスプレイ 1 面）を保ったまま行う再配置の settling 版。
    private async Task ArrangeSlotsAfterPanelStateChangeWithSettlingAsync()
    {
        ArrangeSlotsAfterPanelStateChange();
        await SettleArrangementPreservingFocusAsync();
    }

    // ディスプレイ間で DPI/解像度が異なると、配置直後の WM_DPICHANGED でサイズが上書きされ
    // 4 面セルがくずれるため、ズレを検出したときだけ静かに再補正する。NeedsArrange は
    // フォーカス中スロットを対象外にするので、フォーカスがあっても安全に走る。
    private async Task SettleArrangementPreservingFocusAsync()
    {
        foreach (var delay in ImmediateArrangeSettleDelays)
        {
            await Task.Delay(delay);
            if (_areWindowsHidden || WindowState == WindowState.Minimized)
            {
                return;
            }

            if (!_windowArranger.NeedsArrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex()))
            {
                continue;
            }

            ArrangeSlotsOnActiveMonitorQuietly();
        }
    }

    private async Task<int> ArrangeSlotsOnActiveMonitorWithSettlingAsync(bool bringPanelAfterArrange = true)
    {
        var arranged = ArrangeSlotsOnActiveMonitor(bringPanelAfterArrange);
        foreach (var delay in ImmediateArrangeSettleDelays)
        {
            await Task.Delay(delay);
            if (!CanReapplyPostLaunchArrangement()
                || !_windowArranger.NeedsArrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex()))
            {
                continue;
            }

            arranged = ArrangeSlotsOnActiveMonitorQuietly();
        }

        SchedulePostLaunchArrangeRetries(bringPanelAfterArrange);
        return arranged;
    }

    private void SchedulePostLaunchArrangeRetries(bool bringPanelAfterArrange)
    {
        CancelScheduledPostLaunchArrange();
        if (_areWindowsHidden || WindowState == WindowState.Minimized)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _postLaunchArrangeCancellation = cancellation;
        _ = ReapplyArrangementAfterLaunchAsync(bringPanelAfterArrange, cancellation.Token);
    }

    private void CancelScheduledPostLaunchArrange()
    {
        _postLaunchArrangeCancellation?.Cancel();
        _postLaunchArrangeCancellation?.Dispose();
        _postLaunchArrangeCancellation = null;
    }

    private async Task ReapplyArrangementAfterLaunchAsync(bool bringPanelAfterArrange, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var delay in PostLaunchArrangeRetryDelays)
            {
                await Task.Delay(delay, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested
                        || !CanReapplyPostLaunchArrangement()
                        || !_windowArranger.NeedsArrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex()))
                    {
                        return;
                    }

                    ArrangeSlotsOnActiveMonitorQuietly();
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool CanReapplyPostLaunchArrangement()
    {
        return !_areWindowsHidden
            && WindowState != WindowState.Minimized
            && !_statusStore.Slots.Any(slot => slot.IsFocused);
    }

    private int ArrangeSlotsExceptOnActiveMonitor(
        WindowSlot excludedSlot,
        bool refreshAuxiliaryUiAfterArrange = true)
    {
        // フォーカスイン（ズームイン演出）に随伴する背面整列。前面では新フォーカスの最大化アニメ
        // だけを見せたいので、旧フォーカスの復元などはアニメーションさせず背面で速やかに済ませる。
        var arranged = _windowArranger.ArrangeExcept(
            _statusStore.Slots,
            excludedSlot,
            _statusStore.Config.Gap,
            GetActiveMonitorIndex(),
            animateRestore: false,
            keepAboveHandle: excludedSlot.WindowHandle);
        if (refreshAuxiliaryUiAfterArrange)
        {
            RefreshAuxiliaryUi();
        }

        return arranged;
    }

    private void PrepareFocusTransitionBackdrop(WindowSlot focusSlot, bool arrangeOtherSlotsFirst)
    {
        if (_areWindowsHidden || focusSlot.WindowHandle == IntPtr.Zero)
        {
            return;
        }

        if (arrangeOtherSlotsFirst)
        {
            ArrangeSlotsExceptOnActiveMonitor(focusSlot, false);
        }

        var monitor = GetSlotMonitorIndex(focusSlot);
        var preserveFocusedCover = _statusStore.Slots.Any(slot =>
            slot.IsFocused
            && slot.WindowHandle != IntPtr.Zero
            && GetSlotMonitorIndex(slot) == monitor);

        if (!preserveFocusedCover)
        {
            foreach (var slot in _statusStore.Slots)
            {
                if (ReferenceEquals(slot, focusSlot)
                    || slot.WindowHandle == IntPtr.Zero
                    || GetSlotMonitorIndex(slot) != monitor)
                {
                    continue;
                }

                _windowArranger.BringToFrontWithoutTopmost(slot.WindowHandle);
            }
        }

        // 旧フォーカス自身は、新しい最大化が覆い切るまで全面の下地として残す。
        // 新しい対象だけをその上へ置き、旧窓や他タイルの z-order は動かさない。
        if (!focusSlot.IsFocused)
        {
            _windowArranger.BringToFrontWithoutTopmost(focusSlot.WindowHandle);
        }
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

    private bool SetManagedWindowLayer(
        WindowSlot.SlotWindowLayerMode layerMode,
        bool bringPanelAfterChange = true)
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
            WindowSlot.SlotWindowLayerMode.Topmost => _windowArranger.BringToFrontWithoutTopmost(slot.WindowHandle),
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
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

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

    private void SetDisplayMode(DisplayMode mode, bool updateMessage = true)
    {
        if (_displayMode == mode)
        {
            UpdateDisplayModeChrome();
            UpdateCompactPanelFrame();
            RefreshAuxiliaryUi();
            return;
        }

        var previousMode = _displayMode;

        if (mode == DisplayMode.Standard)
        {
            StopPanelLocateEmphasis();
            ClearCompactPanelFrame();
            try
            {
                Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        }

        if (previousMode == DisplayMode.Standard)
        {
            RememberStandardWindowMetrics();
        }

        var preModeChangeBounds = GetCurrentWindowBounds();

        _displayMode = mode;

        var isStandard = mode == DisplayMode.Standard;
        var isCompact = mode == DisplayMode.Compact;
        var isMicro = mode == DisplayMode.Micro;

        StandardSlotsGrid.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        CompactBarPanel.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        MicroPanel.Visibility = isMicro ? Visibility.Visible : Visibility.Collapsed;
        StoredPanelsExpander.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        FooterControlsGrid.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        ApplicationLauncherPanel.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        HelpButton.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        StandardJumpButton.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        WindowTitleText.Visibility = isMicro ? Visibility.Collapsed : Visibility.Visible;

        // モードのサイズ計算はすべて論理サイズ(DIP)で行い（定数や記憶値・WPF Measure はみな DIP）、
        // 位置計算と SetWindowPos は物理pxで行うため、ここで現在ディスプレイの DPI を一度だけ取り、
        // 目標サイズを DIP→物理px に変換してから位置計算・適用へ渡す。これにより、別 DPI の
        // ディスプレイへ移ってモードを切り替えても、サイズはその場の DPI に正しく変換され、
        // 物理pxの使い回しによる巨大化や下部余白が起きない。
        var scale = GetCurrentDpiScale(new WindowInteropHelper(this).Handle);

        switch (mode)
        {
            case DisplayMode.Compact:
            {
                var baseWidth = _standardWindowWidth > 0 ? _standardWindowWidth : preModeChangeBounds.Width / scale;
                var compactWidth = Math.Max(CompactWindowMinWidth, Math.Round(baseWidth * CompactWindowWidthScale));
                var compactHeight = GetCompactModeHeight(compactWidth);

                MinWidth = CompactWindowMinWidth;
                MinHeight = compactHeight;
                var targetBounds = GetCompactModeBounds(preModeChangeBounds, compactWidth * scale, compactHeight * scale);
                SetWindowBounds(targetBounds.Left, targetBounds.Top, targetBounds.Width, targetBounds.Height);
                break;
            }
            case DisplayMode.Micro:
            {
                MinWidth = MicroWindowSize;
                MinHeight = MicroWindowSize;
                var targetBounds = GetMicroModeCenteredBounds(MicroWindowSize * scale, MicroWindowSize * scale);
                SetWindowBounds(targetBounds.Left, targetBounds.Top, targetBounds.Width, targetBounds.Height);
                break;
            }
            case DisplayMode.Standard:
            default:
            {
                MinWidth = _standardWindowMinWidth > 0 ? _standardWindowMinWidth : MinWidth;
                MinHeight = _standardWindowMinHeight > 0 ? _standardWindowMinHeight : MinHeight;
                var targetWidth = _standardWindowWidth > 0
                    ? Math.Max(MinWidth, _standardWindowWidth)
                    : Width;
                var targetHeight = _standardWindowHeight > 0
                    ? Math.Max(MinHeight, _standardWindowHeight)
                    : Height;
                var targetBounds = GetStandardModeRestoreBounds(preModeChangeBounds, targetWidth * scale, targetHeight * scale);
                SetWindowBounds(targetBounds.Left, targetBounds.Top, targetBounds.Width, targetBounds.Height);
                break;
            }
        }

        UpdateDisplayModeChrome();
        UpdateCompactPanelFrame();
        RefreshAuxiliaryUi();

        if (updateMessage)
        {
            _statusStore.Message = mode switch
            {
                DisplayMode.Compact => "縮小表示に切り替えました。",
                DisplayMode.Micro => "極小表示に切り替えました。",
                _ => "標準表示に戻しました。"
            };
        }
    }

    private void UpdateDisplayModeChrome()
    {
        (DisplayModeButton.Content, DisplayModeButton.ToolTip) = _displayMode switch
        {
            DisplayMode.Standard => (CompactModeGlyph, "縮小表示へ切り替え"),
            DisplayMode.Compact => (MicroModeGlyph, "極小表示へ切り替え"),
            DisplayMode.Micro => (StandardModeGlyph, "標準表示へ戻す"),
            _ => (CompactModeGlyph, "縮小表示へ切り替え")
        };
    }

    // 標準サイズは DPI 非依存の論理サイズ(DIP)で記憶する。物理pxで覚えると、別 DPI の
    // ディスプレイへ移って標準へ戻したとき、その物理pxがそのまま適用されてウィンドウが
    // 巨大化（または縮小）してしまう。現在の物理サイズを現在DPIで割って DIP に直して保持する。
    private void RememberStandardWindowMetrics()
    {
        if (!IsStandardMode)
        {
            return;
        }

        var panelHandle = new WindowInteropHelper(this).Handle;
        var scale = GetCurrentDpiScale(panelHandle);
        var currentBounds = GetCurrentWindowBounds();
        _standardWindowWidth = currentBounds.Width / scale;
        _standardWindowHeight = currentBounds.Height / scale;
        _standardWindowMinWidth = MinWidth;
        _standardWindowMinHeight = MinHeight;
    }

    private WindowArranger.WindowBounds GetMicroModeCenteredBounds(double targetWidth, double targetHeight)
    {
        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle == IntPtr.Zero
            || !_windowArranger.TryGetMonitorWorkAreaForWindow(panelHandle, out var workArea))
        {
            return new WindowArranger.WindowBounds(
                (int)Math.Round(Left + Width / 2 - targetWidth / 2),
                (int)Math.Round(Top + Height / 2 - targetHeight / 2),
                (int)Math.Round(targetWidth),
                (int)Math.Round(targetHeight));
        }

        var left = workArea.Left + (workArea.Width - targetWidth) / 2;
        var top = workArea.Top + (workArea.Height - targetHeight) / 2;
        return new WindowArranger.WindowBounds(
            (int)Math.Round(left),
            (int)Math.Round(top),
            (int)Math.Round(targetWidth),
            (int)Math.Round(targetHeight));
    }

    private double GetCompactModeHeight(double targetWindowWidth)
    {
        const double titleRowHeight = 26;
        const double edgePadding = 1;
        var compactContentWidth = Math.Max(
            0,
            targetWindowWidth
            - RootLayoutGrid.Margin.Left
            - RootLayoutGrid.Margin.Right
            - CompactBarPanel.Margin.Left
            - CompactBarPanel.Margin.Right);

        CompactBarPanel.Measure(new Size(compactContentWidth, double.PositiveInfinity));
        var compactPanelHeight = CompactBarPanel.DesiredSize.Height + CompactBarPanel.Margin.Top + CompactBarPanel.Margin.Bottom;
        var auxiliaryContentWidth = Math.Max(
            0,
            targetWindowWidth
            - RootLayoutGrid.Margin.Left
            - RootLayoutGrid.Margin.Right
            - StoredPanelsRowGrid.Margin.Left
            - StoredPanelsRowGrid.Margin.Right);
        StoredPanelsRowGrid.Measure(new Size(auxiliaryContentWidth, double.PositiveInfinity));
        var auxiliaryRowHeight = StoredPanelsRowGrid.DesiredSize.Height
            + StoredPanelsRowGrid.Margin.Top
            + StoredPanelsRowGrid.Margin.Bottom;
        var desiredHeight = titleRowHeight
            + compactPanelHeight
            + auxiliaryRowHeight
            + RootLayoutGrid.Margin.Top
            + RootLayoutGrid.Margin.Bottom
            + edgePadding;
        return Math.Max(CompactWindowMinHeight, Math.Ceiling(desiredHeight));
    }

    private WindowArranger.WindowBounds GetCurrentWindowBounds()
    {
        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle != IntPtr.Zero && _windowArranger.TryGetWindowBounds(panelHandle, out var actualBounds))
        {
            return actualBounds;
        }

        return new WindowArranger.WindowBounds(
            (int)Math.Round(Left),
            (int)Math.Round(Top),
            (int)Math.Round(Width),
            (int)Math.Round(Height));
    }

    private WindowArranger.WindowBounds GetCompactModeBounds(WindowArranger.WindowBounds standardBounds, double targetWidth, double targetHeight)
    {
        var compactRight = standardBounds.Left + standardBounds.Width;
        var defaultBounds = new WindowArranger.WindowBounds(
            (int)Math.Round(compactRight - targetWidth),
            standardBounds.Top,
            (int)Math.Round(targetWidth),
            (int)Math.Round(targetHeight));

        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle == IntPtr.Zero
            || !_windowArranger.TryGetMonitorWorkAreaForWindow(panelHandle, out var workArea))
        {
            return defaultBounds;
        }

        var adjustedLeft = ClampToWorkArea(defaultBounds.Left, workArea.Left, workArea.Width, targetWidth);
        var adjustedTop = ClampToWorkArea(defaultBounds.Top, workArea.Top, workArea.Height, targetHeight);
        return new WindowArranger.WindowBounds(
            (int)Math.Round(adjustedLeft),
            (int)Math.Round(adjustedTop),
            (int)Math.Round(targetWidth),
            (int)Math.Round(targetHeight));
    }

    // left/top/width/height はすべて物理px。位置・サイズとも物理pxで SetWindowPos に渡す。
    // 論理サイズ(DIP)からの変換は呼び出し側（モード切替）で現在DPIを用いて済ませておく。
    private void SetWindowBounds(double left, double top, double width, double height)
    {
        var bounds = new WindowArranger.WindowBounds(
            (int)Math.Round(left),
            (int)Math.Round(top),
            (int)Math.Round(width),
            (int)Math.Round(height));
        var panelHandle = new WindowInteropHelper(this).Handle;

        if (panelHandle != IntPtr.Zero && _windowArranger.SetWindowBounds(panelHandle, bounds))
        {
            return;
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private async Task LocatePanelAsync()
    {
        if (IsStandardMode)
        {
            return;
        }

        ActivatePanelWindow();
        StopPanelLocateEmphasis();

        var cancellation = new CancellationTokenSource();
        _panelLocateCancellation = cancellation;

        try
        {
            UpdateCompactPanelFrame(PanelFrameVisual.Emphasis);
            await Task.Delay(1400, cancellation.Token);
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

    private void StopPanelLocateEmphasis()
    {
        _panelLocateCancellation?.Cancel();
        _panelLocateCancellation?.Dispose();
        _panelLocateCancellation = null;
    }

    private void UpdateCompactPanelFrame(PanelFrameVisual visual = PanelFrameVisual.Normal)
    {
        if (IsStandardMode)
        {
            ClearCompactPanelFrame();
            return;
        }

        var color = visual switch
        {
            PanelFrameVisual.Emphasis => (Color)ColorConverter.ConvertFromString("#8AFCB7"),
            _ => (Color)ColorConverter.ConvertFromString("#45D483")
        };

        var borderOpacity = visual switch
        {
            PanelFrameVisual.Emphasis => 1.0,
            _ => 0.82
        };

        var borderThickness = visual == PanelFrameVisual.Emphasis ? 2.25 : 1.75;
        PanelFrameBorder.BorderBrush = new SolidColorBrush(color) { Opacity = borderOpacity };
        PanelFrameBorder.BorderThickness = new Thickness(borderThickness);
        PanelFrameBorder.Effect = null;
    }

    private void ClearCompactPanelFrame()
    {
        PanelFrameBorder.BorderBrush = Brushes.Transparent;
        PanelFrameBorder.BorderThickness = new Thickness(0);
        PanelFrameBorder.Effect = null;
    }

    private WindowArranger.WindowBounds GetStandardModeRestoreBounds(WindowArranger.WindowBounds compactBounds, double targetWidth, double targetHeight)
    {
        var compactRight = compactBounds.Left + compactBounds.Width;
        var defaultBounds = new WindowArranger.WindowBounds(
            (int)Math.Round(compactRight - targetWidth),
            compactBounds.Top,
            (int)Math.Round(targetWidth),
            (int)Math.Round(targetHeight));

        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle == IntPtr.Zero
            || !_windowArranger.TryGetMonitorWorkAreaForWindow(panelHandle, out var workArea))
        {
            return defaultBounds;
        }

        var adjustedLeft = ClampToWorkArea(defaultBounds.Left, workArea.Left, workArea.Width, targetWidth);
        var adjustedTop = ClampToWorkArea(defaultBounds.Top, workArea.Top, workArea.Height, targetHeight);

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

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _wasMinimized = true;
            CancelScheduledPanelFrontRestore();
            CancelScheduledFocusedSlotReassert();
            CancelScheduledPostLaunchArrange();
            return;
        }

        // 最小化からの復元時は、最小化中にDPIの異なるディスプレイへ移動していると、WPF が
        // 旧ディスプレイのスケールで記憶した物理サイズのまま復元してウィンドウが過大化する
        // （高DPI側で最小化→低DPI側で復元すると巨大化し下部に余白）。最小化中の移動では復元時に
        // WM_DPICHANGED が来ないことがあるため、ここで現在ディスプレイの実DPIに基づき基準サイズを
        // 物理pxで再適用して確実に正す。
        if (_wasMinimized)
        {
            _wasMinimized = false;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(CorrectWindowSizeForCurrentDpi));
        }

        if (IsAnyMouseButtonPressed())
        {
            SuppressFocusedSlotReassertForPanelInput();
            return;
        }

        // マウスクリックで panel がアクティブになった直後に SetForegroundWindow すると、
        // Button の MouseUp/Click が成立しないため、focused 再適用は遅延実行する。
        ScheduleFocusedSlotReassert();
        RefreshAuxiliaryUi();
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        if (IsAnyMouseButtonPressed())
        {
            SuppressFocusedSlotReassertForPanelInput();
            return;
        }

        ScheduleFocusedSlotReassert();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SuppressPeriodicRefreshForInteractiveLayout();
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e)
    {
        SuppressPeriodicRefreshForInteractiveLayout();
    }

    private void SuppressPeriodicRefreshForInteractiveLayout()
    {
        _suppressPeriodicRefreshUntil = DateTimeOffset.UtcNow + InteractiveRefreshSuppressWindow;
    }

    private void RefreshAuxiliaryUi()
    {
        UpdateDisplayBadges();
        RefreshLaunchButtonAvailability();
        TaskbarJumpListService.Update(
            _statusStore.Slots,
            _displayMode,
            _statusStore.WorkspaceApplications,
            _statusStore.AuxiliaryApplications);
    }

    private void ScheduleUnusedUserDataReclaim()
    {
        var config = _statusStore.Config;
        _ = Task.Run(() =>
        {
            try
            {
                var reclaimed = SlotUserDataPaths.ReclaimUnusedUserData(config);
                if (reclaimed > 0)
                {
                    var reclaimedMegabytes = reclaimed / (1024d * 1024d);
                    DiagnosticLog.Write($"Reclaimed {reclaimed} bytes ({reclaimedMegabytes:0.0} MB) of unused VS Code cache.");
                    Dispatcher.BeginInvoke(() =>
                    {
                        _statusStore.Message = $"VS Code の再生成キャッシュを {reclaimedMegabytes:0.0} MB 回収しました。";
                    });
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        });
    }

    private void EnsurePreferredLayout(WindowSlot slot)
    {
        if (!_statusStore.IsVsCodeSlot(slot))
        {
            return;
        }

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
        if (!_statusStore.IsVsCodeSlot(slot))
        {
            return;
        }

        if (VscodeLayoutState.TryCapturePreferredLayout(slot, _statusStore.Config, _windowArranger, out var preference))
        {
            _statusStore.UpdatePreferredLayout(slot, preference);
        }
    }

    private void CaptureFocusedLayout()
    {
        // 複数ディスプレイで同時にフォーカスし得るため、すべてのフォーカス中スロットを対象にする。
        foreach (var focusedSlot in _statusStore.Slots.Where(item => item.IsFocused))
        {
            CapturePreferredLayout(focusedSlot);
        }
    }

    private void SchedulePanelToFront(TimeSpan? delay = null)
    {
        CancelScheduledPanelFrontRestore();
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _panelFrontRestoreCancellation = cancellation;
        _ = BringPanelToFrontAfterDelayAsync(delay ?? PanelFrontRestoreDelay, cancellation.Token);
    }

    private void CancelScheduledPanelFrontRestore()
    {
        _panelFrontRestoreCancellation?.Cancel();
        _panelFrontRestoreCancellation?.Dispose();
        _panelFrontRestoreCancellation = null;
    }

    private void ScheduleFocusedSlotReassert(TimeSpan? delay = null)
    {
        CancelScheduledFocusedSlotReassert();
        if (WindowState == WindowState.Minimized
            || _isWindowMoveOrResizeActive
            || DateTimeOffset.UtcNow < _suppressFocusedSlotReassertUntil)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _focusedSlotReassertCancellation = cancellation;
        _ = ReassertFocusedSlotAfterDelayAsync(delay ?? FocusedSlotReassertDelay, cancellation.Token);
    }

    private void CancelScheduledFocusedSlotReassert()
    {
        _focusedSlotReassertCancellation?.Cancel();
        _focusedSlotReassertCancellation?.Dispose();
        _focusedSlotReassertCancellation = null;
    }

    private void CancelFocusSwitchArrange()
    {
        // 実体の破棄は各所有側の finally に任せ、ここではキャンセルのみ行う。
        // すべて UI スレッド上で逐次実行されるため、二重 Dispose や競合は起きない。
        _focusSwitchArrangeCancellation?.Cancel();
        _focusedZOrderVerificationCancellation?.Cancel();
    }

    private void ScheduleFocusedZOrderVerification(WindowSlot focusedSlot)
    {
        _focusedZOrderVerificationCancellation?.Cancel();
        if (_areWindowsHidden
            || WindowState == WindowState.Minimized
            || focusedSlot.WindowHandle == IntPtr.Zero
            || !focusedSlot.IsFocused)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _focusedZOrderVerificationCancellation = cancellation;
        _ = VerifyFocusedZOrderAfterTransitionAsync(focusedSlot, cancellation);
    }

    private async Task VerifyFocusedZOrderAfterTransitionAsync(
        WindowSlot focusedSlot,
        CancellationTokenSource cancellation)
    {
        try
        {
            foreach (var delay in FocusedZOrderVerificationDelays)
            {
                await Task.Delay(delay, cancellation.Token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!cancellation.IsCancellationRequested
                        && !_areWindowsHidden
                        && WindowState != WindowState.Minimized
                        && !_isWindowMoveOrResizeActive
                        && !_isCardDragDropInProgress
                        && !IsAnyMouseButtonPressed()
                        && focusedSlot.IsFocused
                        && focusedSlot.WindowHandle != IntPtr.Zero)
                    {
                        RepairFocusedZOrderIfNeeded(focusedSlot);
                    }
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_focusedZOrderVerificationCancellation, cancellation))
            {
                _focusedZOrderVerificationCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void ScheduleFocusTransitionLayerFinalize()
    {
        CancelFocusSwitchArrange();
        if (_areWindowsHidden || WindowState == WindowState.Minimized)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _focusSwitchArrangeCancellation = cancellation;
        _ = FinalizeFocusTransitionLayerAfterDelayAsync(cancellation);
    }

    private async Task FinalizeFocusTransitionLayerAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(FocusSwitchArrangeDelay, cancellation.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellation.IsCancellationRequested
                    || _areWindowsHidden
                    || WindowState == WindowState.Minimized)
                {
                    return;
                }

                if (FocusedSlots().Any())
                {
                    ApplyLayerPreservingFocusMode(_managedWindowLayerMode);
                }
                else
                {
                    ApplyManagedWindowLayers(false);
                    BringPanelToFrontImmediate();
                }

                RefreshAuxiliaryUi();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_focusSwitchArrangeCancellation, cancellation))
            {
                _focusSwitchArrangeCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task ReassertFocusedSlotAfterDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
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
                    ReassertFocusedSlotIfNeeded();
                }
            }, DispatcherPriority.ContextIdle);
        }
        catch (OperationCanceledException)
        {
        }
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
                if (!cancellationToken.IsCancellationRequested
                    && WindowState != WindowState.Minimized)
                {
                    BringPanelToFrontImmediate();
                }
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ReassertFocusedSlotIfNeeded()
    {
        if (_isReassertingFocusedSlot
            || _areWindowsHidden
            || _isBusy
            || _isWindowMoveOrResizeActive
            || WindowState == WindowState.Minimized
            || DateTimeOffset.UtcNow < _suppressFocusedSlotReassertUntil
            || IsAnyMouseButtonPressed())
        {
            return;
        }

        var focusedSlots = FocusedSlots().ToList();
        if (focusedSlots.Count == 0)
        {
            return;
        }

        // ユーザーがフォーカス中ウィンドウをアプリ外で直接最小化していたら、再最大化で抵抗せず
        // そのディスプレイのフォーカスだけ解除し、最小化のまま残す。最小化に逆らうと勝手に 1 面へ
        // 戻り不整合表示になる。複数フォーカスのうち最小化された面だけを対象にする。
        if (focusedSlots.Any(slot => _windowArranger.IsMinimized(slot.WindowHandle)))
        {
            ReconcileExternallyMinimizedFocusedSlot();
            focusedSlots = FocusedSlots().Where(slot => !_windowArranger.IsMinimized(slot.WindowHandle)).ToList();
            if (focusedSlots.Count == 0)
            {
                return;
            }
        }

        _isReassertingFocusedSlot = true;
        try
        {
            // 各ディスプレイのフォーカスを、それぞれの実効ディスプレイで再最大化・前面化する。
            // フォアグラウンドは奪わない（MaximizeOnMonitor）。複数フォーカスへ毎回 SetForegroundWindow
            // すると争奪でちらつき・ハングするため、最大化と z-order だけ整え、前面化はパネルに任せる。
            foreach (var focusedSlot in focusedSlots)
            {
                if (_windowArranger.MaximizeOnMonitor(focusedSlot.WindowHandle, GetSlotMonitorIndex(focusedSlot)))
                {
                    EnsureFocusedSlotAboveTiles(focusedSlot);
                }
            }

            SchedulePanelToFront();
        }
        finally
        {
            _isReassertingFocusedSlot = false;
        }
    }

    /// <summary>
    /// フォーカス中（1 面表示）のウィンドウを、アプリを介さず OS 側で直接最小化したケースを実体に合わせる。
    /// アプリのフォーカス状態が実体とずれたまま再アサートや遅延整列が走ると、最小化したはずの
    /// ウィンドウが勝手に 1 面へ戻り、別スロットの 4 面が前面へ重なる不整合表示になる。
    /// ユーザーの手動最小化を尊重し、フォーカスだけ解除する。ウィンドウは最小化のまま、
    /// 他スロットは現在の象限のまま残し、再最大化や再配置で勝手に復帰させない。
    /// アプリ主導の非表示（一括最小化）中やビジー中、パネル最小化中は対象外。
    /// </summary>
    private bool ReconcileExternallyMinimizedFocusedSlot()
    {
        if (_areWindowsHidden
            || _isBusy
            || _isReassertingFocusedSlot
            || WindowState == WindowState.Minimized)
        {
            return false;
        }

        var minimizedFocusedSlots = _statusStore.Slots
            .Where(slot => slot.IsFocused
                && slot.WindowHandle != IntPtr.Zero
                && _windowArranger.IsMinimized(slot.WindowHandle))
            .ToList();
        if (minimizedFocusedSlots.Count == 0)
        {
            return false;
        }

        // 手動最小化された面のフォーカスだけ解除する。最小化中ウィンドウの復元も、他スロットの
        // 再配置も行わない。他ディスプレイのフォーカスはそのまま維持する。
        CancelFocusSwitchArrange();
        CancelScheduledFocusedSlotReassert();
        foreach (var slot in minimizedFocusedSlots)
        {
            slot.IsFocused = false;
        }

        var name = minimizedFocusedSlots[0].Name;
        _statusStore.Message = minimizedFocusedSlots.Count == 1
            ? $"スロット{name}の手動最小化を検出したためフォーカスを解除しました。"
            : $"{minimizedFocusedSlots.Count}個の手動最小化を検出したためフォーカスを解除しました。";
        RefreshAuxiliaryUi();
        return true;
    }

    private void HideCloseAllConfirmDialog()
    {
        CloseAllConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void HideClearSlotInfoDialog()
    {
        _pendingSlotInfoClear = null;
        ClearSlotInfoOverlay.Visibility = Visibility.Collapsed;
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

        _hiddenFocusedSlots.Clear();
        RestoreHiddenWindows();
        RefreshAuxiliaryUi();
    }

    private void RestoreHiddenWindows()
    {
        _areWindowsHidden = false;
        UpdateVisibilityButtonVisual();
        foreach (var slot in _statusStore.Slots)
        {
            _windowArranger.Restore(slot.WindowHandle);
            slot.IsHidden = false;
        }
    }

    private bool TryRestoreHiddenFocusedSlot(out WindowSlot restoredFocusedSlot)
    {
        restoredFocusedSlot = null!;
        var toRestore = _hiddenFocusedSlots.Where(slot => slot.WindowHandle != IntPtr.Zero).ToList();
        _hiddenFocusedSlots.Clear();

        if (toRestore.Count == 0)
        {
            return false;
        }

        SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Topmost);
        var anyRestored = false;
        foreach (var focusedSlot in toRestore)
        {
            _windowArranger.MaximizeOnMonitor(focusedSlot.WindowHandle, GetSlotMonitorIndex(focusedSlot));
            SetFocusedSlotForDisplay(focusedSlot);
            if (EnsureFocusedSlotAboveTiles(focusedSlot))
            {
                anyRestored = true;
                restoredFocusedSlot = focusedSlot;
            }
        }

        if (!anyRestored)
        {
            return false;
        }

        SchedulePanelToFront();
        return true;
    }

    private void UpdateVisibilityButtonVisual()
    {
        ToggleVisibilityButton.Content = _areWindowsHidden ? "表示" : "非表示";
        ToggleVisibilityButton.Style = (Style)FindResource(_areWindowsHidden
            ? "VisibilityRestoreButtonStyle"
            : "BarButton");

        // 縮小/極小モードの円形トグルも、非表示中は標準モードの「表示」ボタンと同じ黄色にする。
        var centerButtonStyle = (Style)FindResource(_areWindowsHidden
            ? "MicroCenterRestoreButtonStyle"
            : "MicroCenterButtonStyle");

        if (MicroVisibilityButton is not null)
        {
            MicroVisibilityButton.Content = _areWindowsHidden ? "表" : "非";
            MicroVisibilityButton.Style = centerButtonStyle;
            MicroVisibilityButton.ToolTip = _areWindowsHidden
                ? "管理中ウィンドウを再表示"
                : "管理中ウィンドウを非表示";
        }

        if (CompactVisibilityButton is not null)
        {
            CompactVisibilityButton.Content = MicroVisibilityButton?.Content ?? (_areWindowsHidden ? "\u8868" : "\u975E");
            CompactVisibilityButton.ToolTip = MicroVisibilityButton?.ToolTip;
            CompactVisibilityButton.Style = centerButtonStyle;
        }
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
        SuppressFocusedSlotReassertForPanelInput();

        if (Keyboard.FocusedElement is not TextBox textBox || textBox.IsReadOnly)
        {
            return;
        }

        // ここでの確定処理はスロットカードのインライン編集 (InlineTitleTextBox) 専用。
        // 設定ダイアログなどの通常 TextBox を対象に含めると IsReadOnly/Focusable が書き換わって
        // 2 回目以降クリックしても入力できなくなる。
        if (!IsInlineTitleTextBox(textBox))
        {
            return;
        }

        if (IsSelfOrChild(e.OriginalSource as DependencyObject, textBox))
        {
            return;
        }

        FinishInlineTitleTextBoxEdit(textBox, commit: true, clearKeyboardFocus: true);
    }

    private bool IsInlineTitleTextBox(TextBox textBox)
    {
        return textBox.Style is { } style
            && ReferenceEquals(style, TryFindResource("InlineTitleTextBox") as Style);
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

    private static int NormalizeMonitorIndex(int monitorIndex, int monitorCount)
    {
        if (monitorCount <= 0)
        {
            return 0;
        }

        var normalized = monitorIndex % monitorCount;
        return normalized < 0 ? normalized + monitorCount : normalized;
    }

    // 全ディスプレイ移動でベースが進んだ後に呼ぶ。override が新ベースに一致するスロットは
    // 「単独」ではなくなったとみなして解除し、以降は群れとして扱う（リベースの肝）。
    private void CollapseMonitorOverridesMatchingBase()
    {
        var monitorCount = _windowArranger.GetMonitorCount();
        if (monitorCount <= 0)
        {
            return;
        }

        var baseIndex = NormalizeMonitorIndex(GetActiveMonitorIndex(), monitorCount);
        foreach (var slot in _statusStore.Slots)
        {
            if (!slot.MonitorOverride.HasValue)
            {
                continue;
            }

            if (NormalizeMonitorIndex(slot.MonitorOverride.Value, monitorCount) == baseIndex)
            {
                slot.MonitorOverride = null;
            }
        }
    }

    // モニタ構成が変わったとき（例: 単独移動先のディスプレイを抜いた）に override を健全化する。
    // 正規化後にベースと一致するものは解除、範囲外のものは正規化済み値へ丸める。1 枚運用では
    // すべてベースに収束するので単独移動は自然に解消される。
    private void NormalizeMonitorOverrides()
    {
        var monitorCount = _windowArranger.GetMonitorCount();
        if (monitorCount <= 0)
        {
            return;
        }

        var baseIndex = NormalizeMonitorIndex(GetActiveMonitorIndex(), monitorCount);
        foreach (var slot in _statusStore.Slots)
        {
            if (!slot.MonitorOverride.HasValue)
            {
                continue;
            }

            var normalized = NormalizeMonitorIndex(slot.MonitorOverride.Value, monitorCount);
            if (normalized == baseIndex)
            {
                slot.MonitorOverride = null;
            }
            else if (normalized != slot.MonitorOverride.Value)
            {
                slot.MonitorOverride = normalized;
            }
        }
    }

    // 各カードに現在の実効ディスプレイ（例: "D2"）と色を反映する。
    // バッジ文字は複数モニタかついずれかのスロットが単独移動しているときだけ表示し、全パネルが
    // 同一面に揃っているときは雑音を出さない。色（DisplayBrush）は常時、実効ディスプレイに合わせて
    // 更新する。フォーカス枠の色はこの DisplayBrush を使うため、バッジ非表示でも正しい色が要る。
    private void UpdateDisplayBadges()
    {
        NormalizeMonitorOverrides();

        var monitorCount = _windowArranger.GetMonitorCount();
        var anyOverride = _statusStore.Slots.Any(slot => slot.MonitorOverride.HasValue);
        var showBadges = monitorCount > 1 && anyOverride;
        var baseIndex = GetActiveMonitorIndex();

        foreach (var slot in _statusStore.Slots)
        {
            var effectiveMonitor = slot.MonitorOverride ?? baseIndex;
            slot.DisplayBrush = GetDisplayBrushForMonitor(effectiveMonitor);

            slot.DisplayBadgeText = showBadges && slot.WindowHandle != IntPtr.Zero
                ? $"D{_windowArranger.ResolveMonitorNumber(effectiveMonitor)}"
                : string.Empty;
        }
    }

    // 実効ディスプレイの色を返す。ベースディスプレイは常に緑（作業ベースの目印）。
    // 非ベースは番号の小さい順に 青→紫→金… を割り当てる（ベースが移動しても緑＝ベースを維持）。
    private Brush GetDisplayBrushForMonitor(int monitorIndex)
    {
        var monitorCount = _windowArranger.GetMonitorCount();
        var normalized = NormalizeMonitorIndex(monitorIndex, monitorCount);
        var baseIndex = NormalizeMonitorIndex(GetActiveMonitorIndex(), monitorCount);

        if (normalized == baseIndex)
        {
            return ResolveBrush("AccentBrush");
        }

        // 非ベースの並び順（ベースを除いた昇順での位置）で色を選ぶ。
        var rank = normalized < baseIndex ? normalized : normalized - 1;
        var keys = NonBaseDisplayBrushKeys;
        return ResolveBrush(keys[rank % keys.Length]);
    }

    // リソースが見つからなくても例外を投げない。毎ティックの再描画から呼ばれるため、
    // ここで例外を出すとアプリ全体が落ちる。フォールバックは緑（ベース色）。
    private Brush ResolveBrush(string key)
    {
        return TryFindResource(key) as Brush ?? Brushes.LimeGreen;
    }

    private static readonly string[] NonBaseDisplayBrushKeys =
    [
        "DisplayAccent2Brush",
        "DisplayAccent3Brush",
        "DisplayAccent4Brush"
    ];

    // スロットの実効ディスプレイ（正規化済み）。override があればそれ、無ければベース。
    private int GetSlotMonitorIndex(WindowSlot slot)
    {
        var monitorCount = _windowArranger.GetMonitorCount();
        return NormalizeMonitorIndex(slot.MonitorOverride ?? GetActiveMonitorIndex(), monitorCount);
    }

    private IEnumerable<WindowSlot> FocusedSlots()
    {
        return _statusStore.Slots.Where(slot => slot.IsFocused && slot.WindowHandle != IntPtr.Zero);
    }

    // 管理中スロットのウィンドウハンドル一式。遮蔽判定で「身内」を除外するために使う。
    private IReadOnlyCollection<IntPtr> GetManagedWindowHandles()
    {
        return _statusStore.Slots
            .Where(slot => slot.WindowHandle != IntPtr.Zero)
            .Select(slot => slot.WindowHandle)
            .ToHashSet();
    }

    // 指定ディスプレイで現在フォーカス中のスロット（無ければ null）。except を渡すとそれを除外する。
    private WindowSlot? GetFocusedSlotOnMonitor(int monitorIndex, WindowSlot? except = null)
    {
        var target = NormalizeMonitorIndex(monitorIndex, _windowArranger.GetMonitorCount());
        return _statusStore.Slots.FirstOrDefault(slot =>
            slot.IsFocused
            && slot.WindowHandle != IntPtr.Zero
            && !ReferenceEquals(slot, except)
            && GetSlotMonitorIndex(slot) == target);
    }

    // 各スロットの IsFocused 変化を捉えてフォーカス名オーバーレイを出し入れする。
    // true になった瞬間に対象ディスプレイ中央へ表示し、今表示中のスロットが false に
    // なったら即座に消す。フォーカス制御の全経路は最終的に IsFocused を切り替えるので、
    // ここ一箇所で表示タイミングを面倒見られる。
    private void Slot_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WindowSlot.IsFocused) || sender is not WindowSlot slot)
        {
            return;
        }

        if (slot.IsFocused)
        {
            // 非表示（一括最小化）中のフォーカス復帰では画面に何も見えないので出さない。
            if (_areWindowsHidden)
            {
                return;
            }

            _overlayShownForSlot = slot;

            // 1 段目: パネル名（パネルD）、2 段目: ユーザーが付けたタイトル、3 段目: アプリ名。
            // PanelTitle が空、または既定タイトル（「スロット D」）のままのときは 1 段目と
            // 内容が重複するので、タイトル行を省いて 2 行にフォールバックする。
            var title = string.IsNullOrWhiteSpace(slot.PanelTitle)
                || string.Equals(slot.PanelTitle, slot.DefaultPanelTitle, StringComparison.Ordinal)
                ? string.Empty
                : slot.PanelTitle;
            _focusNameOverlay.Show(
                $"パネル{slot.Name}",
                title,
                slot.ApplicationDisplayName,
                GetSlotMonitorIndex(slot));
        }
        else if (ReferenceEquals(_overlayShownForSlot, slot))
        {
            _overlayShownForSlot = null;
            _focusNameOverlay.Hide();
        }
    }

    private void SetFocusedSlotForDisplay(WindowSlot slot)
    {
        var monitor = GetSlotMonitorIndex(slot);
        foreach (var other in _statusStore.Slots)
        {
            if (!ReferenceEquals(other, slot) && other.IsFocused && GetSlotMonitorIndex(other) == monitor)
            {
                other.IsFocused = false;
            }
        }

        slot.IsFocused = true;
    }

    // 指定ディスプレイのフォーカスだけを解除する。他ディスプレイのフォーカスは触らない。
    private void ClearFocusedSlotForDisplay(int monitorIndex)
    {
        var target = NormalizeMonitorIndex(monitorIndex, _windowArranger.GetMonitorCount());
        foreach (var slot in _statusStore.Slots)
        {
            if (slot.IsFocused && GetSlotMonitorIndex(slot) == target)
            {
                slot.IsFocused = false;
            }
        }
    }

    // 最大化・復元の DWM アニメーションを壊さず、通常 z-order 帯の相対順だけを整える。
    // パネル本体は topmost 帯、管理対象は通常帯なので、順序は常に
    // パネル本体 > 1面フォーカス > 同じ面の4面スロットになる。
    private bool EnsureFocusedSlotAboveTiles(WindowSlot focusedSlot)
    {
        var monitor = GetSlotMonitorIndex(focusedSlot);
        BringPanelToFrontImmediate();
        var focusedRaised = _windowArranger.BringToFrontWithoutTopmost(focusedSlot.WindowHandle);

        var orderedAny = false;
        foreach (var slot in _statusStore.Slots)
        {
            if (!ReferenceEquals(slot, focusedSlot)
                && slot.WindowHandle != IntPtr.Zero
                && GetSlotMonitorIndex(slot) == monitor)
            {
                // HWND_BOTTOM へ送らず、各タイルをフォーカス窓の直後へ置く。
                // タイルが画面を覆い続けるため、壁紙や白背景が露出しない。
                _windowArranger.ReleaseTopmostIfNeeded(slot.WindowHandle);
                orderedAny |= _windowArranger.PlaceDirectlyBehind(slot.WindowHandle, focusedSlot.WindowHandle);
            }
        }

        BringPanelToFrontImmediate();
        return focusedRaised || orderedAny;
    }

    // フォーカス遷移が落ち着いた後の検査用。正常な Z 順では何も書き込まない。
    // 4 面側が実際に 1 面より上へ出た場合だけ、その4面を非アクティブのまま直後へ戻す。
    // フォーカス窓自体を再前面化しないため、Electron の白い再描画や DWM ズームを誘発しない。
    private bool RepairFocusedZOrderIfNeeded(WindowSlot focusedSlot)
    {
        if (!focusedSlot.IsFocused || focusedSlot.WindowHandle == IntPtr.Zero)
        {
            return false;
        }

        var monitor = GetSlotMonitorIndex(focusedSlot);
        var repaired = false;
        foreach (var slot in _statusStore.Slots)
        {
            if (ReferenceEquals(slot, focusedSlot)
                || slot.WindowHandle == IntPtr.Zero
                || GetSlotMonitorIndex(slot) != monitor
                || !_windowArranger.IsAbove(slot.WindowHandle, focusedSlot.WindowHandle))
            {
                continue;
            }

            _windowArranger.ReleaseTopmostIfNeeded(slot.WindowHandle);
            repaired |= _windowArranger.PlaceDirectlyBehind(slot.WindowHandle, focusedSlot.WindowHandle);
        }

        if (repaired)
        {
            BringPanelToFrontImmediate();
            DiagnosticLog.Write($"Repaired focused z-order for slot {focusedSlot.Name} without activating managed windows.");
        }

        return repaired;
    }

    private void RepairAllFocusedZOrdersIfNeeded()
    {
        if (_areWindowsHidden
            || WindowState == WindowState.Minimized
            || _isWindowMoveOrResizeActive
            || IsAnyMouseButtonPressed())
        {
            return;
        }

        foreach (var focusedSlot in FocusedSlots().ToList())
        {
            RepairFocusedZOrderIfNeeded(focusedSlot);
        }
    }

    // 全ディスプレイのフォーカス中スロットを、各実効ディスプレイで最大化・前面に立て直す。
    // Arrange（非フォーカスのタイル配置）の後に呼び、各面の 1 面表示を保つ。
    // フォアグラウンド（SetForegroundWindow）は奪わない。複数ウィンドウへ繰り返し奪うと
    // アクティベーション争奪でちらつき・ハングを招くため、最大化と z-order だけ整える。
    private void ReassertAllFocusedSlots()
    {
        foreach (var focusedSlot in FocusedSlots().ToList())
        {
            _windowArranger.MaximizeOnMonitor(focusedSlot.WindowHandle, GetSlotMonitorIndex(focusedSlot));
            EnsureFocusedSlotAboveTiles(focusedSlot);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (AppExitConfirmOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideAppExitConfirmDialog();
            DiagnosticLog.Write(LogLevel.Info, "Panel close canceled by user.");
            ScheduleFocusedSlotReassert();
            e.Handled = true;
            return;
        }

        if (CloseAllConfirmOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideCloseAllConfirmDialog();
            e.Handled = true;
            return;
        }

        if (ClearSlotInfoOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideClearSlotInfoDialog();
            e.Handled = true;
            return;
        }

        if (DeleteStoredPanelOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideDeleteStoredPanelDialog();
            e.Handled = true;
            return;
        }

        if (HelpOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideHelpDialog();
            e.Handled = true;
            return;
        }

        // 結果ダイアログ・ログは設定の上に重ねて開くので、Escape は上から順に閉じる。
        if (MaintenanceResultOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            MaintenanceResultOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        if (LogOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            LogOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        if (SettingsOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideSettingsDialog();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        CancelScheduledFocusedSlotReassert();
        CancelScheduledPostLaunchArrange();

        if (!App.IsSessionEnding && !_closeConfirmed)
        {
            e.Cancel = true;
            ShowAppExitConfirmDialog();
            return;
        }

        // 非表示中にアプリが終了される場合、管理中ウィンドウを復元してから閉じる
        if (_areWindowsHidden)
        {
            foreach (var slot in _statusStore.Slots)
            {
                _windowArranger.Restore(slot.WindowHandle);
                slot.IsHidden = false;
            }

            _areWindowsHidden = false;
            _hiddenFocusedSlots.Clear();
            ArrangeSlotsOnActiveMonitor(false);
        }

        base.OnClosing(e);
    }

    // HwndSource.AddHook はフックを弱参照でしか保持しないため、デリゲートを自分で
    // 強参照しておかないと GC 後にフックが黙って外れ、DPI 補正や移動中判定が
    // ある時点から効かなくなる。フィールドに保持して寿命をウィンドウと揃える。
    private HwndSourceHook? _panelWindowProcHookKeepAlive;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _panelWindowProcHookKeepAlive = PanelWindowProcHook;
        HwndSource.FromHwnd(handle)?.AddHook(_panelWindowProcHookKeepAlive);
    }

    protected override void OnClosed(EventArgs e)
    {
        // パネルのクローズ要求が来たことを記録。App.OnExit の正常終了マーカーと突き合わせると、
        // 「閉じ操作は届いたのに終了処理が完走しなかった（＝終了直前のハング）」も切り分けられる。
        DiagnosticLog.Write(LogLevel.Info, "Main panel window closed; requesting application shutdown.");

        _refreshTimer.Stop();
        _refreshCancellation.Cancel();
        _panelFrontRestoreCancellation?.Cancel();
        _panelFrontRestoreCancellation?.Dispose();
        _focusSwitchArrangeCancellation?.Cancel();
        _focusedZOrderVerificationCancellation?.Cancel();
        _focusedSlotReassertCancellation?.Cancel();
        _focusedSlotReassertCancellation?.Dispose();
        _postLaunchArrangeCancellation?.Cancel();
        _postLaunchArrangeCancellation?.Dispose();
        StopPanelLocateEmphasis();
        _focusNameOverlay.Close();
        base.OnClosed(e);

        // App は ShutdownMode=OnExplicitShutdown のため、メインウィンドウを閉じても自動終了しない。
        // 従来の OnLastWindowClose と同じ「パネルを閉じたらアプリも終わる」挙動を保つため、
        // メインウィンドウのクローズ時にここで明示的に終了させる。
        Application.Current?.Shutdown();
    }

    private IntPtr PanelWindowProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // パネルウィンドウのタイトルバードラッグ中に focused slot の reassert が走ると
        // SetForegroundWindow で前面が奪われ、ネイティブ移動ループが破棄されてパネルが
        // 元位置に戻ってしまう。WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE で操作中フラグを立てる。
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        // 別 DPI のディスプレイへ移ると Windows が送ってくる。lParam に「移動先 DPI に合わせた
        // 推奨ウィンドウ矩形（物理px）」が入る。
        const int WM_DPICHANGED = 0x02E0;

        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                _isWindowMoveOrResizeActive = true;
                SuppressFocusedSlotReassertForPanelInput();
                break;
            case WM_EXITSIZEMOVE:
                _isWindowMoveOrResizeActive = false;
                SuppressFocusedSlotReassertForPanelInput();
                break;
            case WM_DPICHANGED:
                // 解像度（DPI）の異なるディスプレイ間を移動・復元したとき、WPF 既定の DPI 追従と
                // このアプリが物理pxで行うサイズ指定が二重適用され、ウィンドウが過大化して下部に
                // 余白ができることがある（特に高DPI側で最小化→低DPI側へドラッグ→復元）。
                // handled は立てず WPF 本来の DPI（描画スケール）更新はそのまま行わせたうえで、
                // 遷移が落ち着いた次のフレームで、Windows が lParam で渡した「移動先 DPI 用の推奨
                // 矩形（物理px）」へ最終サイズを合わせ直し、二重スケールの取りこぼしを正す。
                if (lParam != IntPtr.Zero)
                {
                    var suggested = Marshal.PtrToStructure<RECT>(lParam);
                    _ = Dispatcher.BeginInvoke(
                        DispatcherPriority.Render,
                        new Action(() => ApplyDpiSuggestedBounds(suggested)));
                }

                break;
        }

        return IntPtr.Zero;
    }

    // WM_DPICHANGED で受け取った推奨矩形（物理px）へウィンドウを合わせ直す。最小化中は
    // 復元後に改めて DPICHANGED が来るので何もしない。推奨矩形へ SetWindowPos すると WM_SIZE が
    // 走って WPF が中身を測り直すため、二重スケールで膨らんだ状態と下部の余白が解消される。
    private void ApplyDpiSuggestedBounds(RECT suggested)
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            panelHandle,
            IntPtr.Zero,
            suggested.Left,
            suggested.Top,
            suggested.Right - suggested.Left,
            suggested.Bottom - suggested.Top,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // 最小化からの復元後に、ウィンドウが現在載っているディスプレイの実DPIに合わせて
    // 物理サイズを引き直す。WPF の論理サイズ（DIP）× 現在DPI/96 を正しい物理サイズとし、
    // 旧ディスプレイのスケールを引きずって膨らんだ状態を是正する。位置は現状を維持する。
    // 標準表示以外（コンパクト/極小）は各モードが独自にサイズ管理するため対象外。
    private void CorrectWindowSizeForCurrentDpi()
    {
        if (!IsStandardMode || WindowState == WindowState.Minimized)
        {
            return;
        }

        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle == IntPtr.Zero)
        {
            return;
        }

        var dpi = GetDpiForWindow(panelHandle);
        if (dpi == 0)
        {
            return;
        }

        // WPF の論理サイズ（DIP）。ウィンドウ定義の Width/Height がそのまま使える。
        var logicalWidth = Width;
        var logicalHeight = Height;
        if (double.IsNaN(logicalWidth) || double.IsNaN(logicalHeight) || logicalWidth <= 0 || logicalHeight <= 0)
        {
            return;
        }

        var scale = dpi / 96.0;
        var expectedWidthPx = (int)Math.Round(logicalWidth * scale);
        var expectedHeightPx = (int)Math.Round(logicalHeight * scale);

        if (!_windowArranger.TryGetWindowBounds(panelHandle, out var current))
        {
            return;
        }

        // 既に期待サイズに収まっていれば何もしない（毎回 SetWindowPos して揺らさない）。
        if (Math.Abs(current.Width - expectedWidthPx) <= 2 && Math.Abs(current.Height - expectedHeightPx) <= 2)
        {
            return;
        }

        SetWindowPos(
            panelHandle,
            IntPtr.Zero,
            current.Left,
            current.Top,
            expectedWidthPx,
            expectedHeightPx,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    // ウィンドウが現在載っているディスプレイの DPI 倍率（100%=1.0, 150%=1.5）。
    // 取得できないときは WPF が認識している倍率→1.0 の順でフォールバックする。
    private double GetCurrentDpiScale(IntPtr panelHandle)
    {
        if (panelHandle != IntPtr.Zero)
        {
            var dpi = GetDpiForWindow(panelHandle);
            if (dpi > 0)
            {
                return dpi / 96.0;
            }
        }

        var wpfDpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return wpfDpi > 0 ? wpfDpi : 1.0;
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
        LaunchButton.IsEnabled = !busy && HasLaunchableWorkspaceSlot();
        ApplicationLauncherPanel.IsEnabled = !busy;
        TopmostAllButton.IsEnabled = !busy;
        BackmostAllButton.IsEnabled = !busy;
        ToggleMonitorButton.IsEnabled = !busy;
        ToggleVisibilityButton.IsEnabled = !busy;
        SaveSettingsButton.IsEnabled = !busy;
        LoadSettingsButton.IsEnabled = !busy;
        CloseAllButton.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;
        HelpButton.IsEnabled = !busy;
        DisplayModeButton.IsEnabled = !busy;
        StandardJumpButton.IsEnabled = !busy;
        CompactVisibilityButton.IsEnabled = !busy;
        MicroVisibilityButton.IsEnabled = !busy;
        CompactBarPanel.IsEnabled = !busy;
        StoredPanelsExpander.IsEnabled = !busy;
        SettingsOverlay.IsEnabled = !busy;
        AuxiliaryApplicationPanel.IsEnabled = !busy;
    }

    private void RefreshLaunchButtonAvailability()
    {
        LaunchButton.IsEnabled = !_isBusy && HasLaunchableWorkspaceSlot();
        LaunchButton.ToolTip = _statusStore.LaunchButtonToolTip;
    }

    private bool HasLaunchableWorkspaceSlot()
    {
        return _statusStore.Slots.Any(slot =>
        {
            var application = _statusStore.FindApplication(slot.ApplicationId);
            return application is not null
                && application.IsWorkspaceApplication
                && _applicationLauncher.CanLaunchWorkspaceApplication(application, _statusStore.Config);
        });
    }

    private enum PanelFrameVisual
    {
        Normal,
        Emphasis
    }

    private bool IsCompactMode => _displayMode == DisplayMode.Compact;
    private bool IsMicroMode => _displayMode == DisplayMode.Micro;
    private bool IsStandardMode => _displayMode == DisplayMode.Standard;

    private void SuppressFocusedSlotReassertForPanelInput()
    {
        if (!_statusStore.Slots.Any(slot => slot.IsFocused))
        {
            return;
        }

        _suppressFocusedSlotReassertUntil = DateTimeOffset.UtcNow + FocusedSlotReassertInputSuppressWindow;
        CancelScheduledFocusedSlotReassert();
    }

    private static bool IsAnyMouseButtonPressed()
    {
        return Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed
            || Mouse.MiddleButton == MouseButtonState.Pressed
            || Mouse.XButton1 == MouseButtonState.Pressed
            || Mouse.XButton2 == MouseButtonState.Pressed;
    }
}
