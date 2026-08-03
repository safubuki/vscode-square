using System.Runtime.InteropServices;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class WindowArranger
{
    private const uint WM_CLOSE = 0x0010;
    private const int SW_MAXIMIZE = 3;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_SHOWWINDOW = 0x0040;
    // 対象ウィンドウ（VS Code 等の他プロセス）のスレッドが応答不能でも、この呼び出しが
    // ブロックせず要求をキューに積んで即戻るようにする。これが無いと SetWindowPos は相手
    // スレッドへ同期メッセージを送るため、ハングした管理対象ウィンドウ 1 つでパネルの
    // UI スレッドごと無期限に凍結する（＝ログに何も残らない「痕跡なきハング」の正体）。
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    // SetWindowPlacement 用の同趣旨のフラグ。他プロセス窓への配置要求を非同期化する。
    private const int WPF_ASYNCWINDOWPLACEMENT = 0x0004;
    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint MONITORINFOF_PRIMARY = 0x00000001;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaTransitionsForceDisabled = 3;
    private const int DwmwaCloak = 13;
    private const int DwmwaCloaked = 14;
    private static readonly TimeSpan MaximizeRequestTimeout = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan MaximizeStatePollInterval = TimeSpan.FromMilliseconds(8);
    private const uint GW_HWNDPREV = 3;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private static readonly uint ArrangeFlags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS;
    private static readonly uint LayerFlags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS;

    public int Arrange(IReadOnlyList<WindowSlot> slots, int gap, int monitorIndex, bool animateRestore = true)
    {
        return ArrangeCore(slots, gap, monitorIndex, excludedSlot: null, animateRestore, keepAboveHandle: IntPtr.Zero);
    }

    public int ArrangeExcept(
        IReadOnlyList<WindowSlot> slots,
        WindowSlot excludedSlot,
        int gap,
        int monitorIndex,
        bool animateRestore = true,
        IntPtr keepAboveHandle = default)
    {
        return ArrangeCore(slots, gap, monitorIndex, excludedSlot, animateRestore, keepAboveHandle);
    }

    public bool NeedsArrange(IReadOnlyList<WindowSlot> slots, int gap, int monitorIndex, int tolerance = 48)
    {
        var placements = BuildPlacements(slots, gap, monitorIndex, excludedSlot: null);
        foreach (var placement in placements)
        {
            // 期待セルは可視枠（DWM 拡張フレーム）基準なので、現在値も可視枠で比較する。
            if (IsIconic(placement.Handle) || !TryGetVisibleBounds(placement.Handle, out var current))
            {
                return true;
            }

            if (!IsCloseToExpectedBounds(current, placement, Math.Max(0, tolerance)))
            {
                return true;
            }
        }

        return false;
    }

    private int ArrangeCore(
        IReadOnlyList<WindowSlot> slots,
        int gap,
        int monitorIndex,
        WindowSlot? excludedSlot,
        bool animateRestore,
        IntPtr keepAboveHandle)
    {
        var placements = BuildPlacements(slots, gap, monitorIndex, excludedSlot);

        if (placements.Count == 0)
        {
            return 0;
        }

        // 最大化/最小化中のウィンドウは、復元先（rcNormalPosition）を目的セルへ差し替えてから
        // SW_RESTORE する。DWM の復元アニメは復元先矩形へ向かって再生されるため、ズームアウトの
        // 演出を残したまま目的セルへ直接着地し、「旧位置へ戻ってから SetWindowPos でセルへ
        // ジャンプ」する二段移動（ちらつき）にならない。animateRestore=false のときは
        // フォーカス切替の背面整列や settling 補正なので、遷移アニメ自体を止めて無音で行う。
        var restoring = placements
            .Where(placement => IsIconic(placement.Handle) || IsZoomed(placement.Handle))
            .ToList();
        if (!animateRestore)
        {
            foreach (var placement in restoring)
            {
                SetDwmTransitionsDisabled(placement.Handle, true);
            }
        }

        try
        {
            foreach (var placement in restoring)
            {
                // 最大化/最小化中は不可視枠を正しく測れないため、通常状態のときに記録した
                // キャッシュ値で復元先を補正する。
                // フォーカス切替の静かな背面整列では、復元要求より先に旧フォーカスを新フォーカスの
                // 直後へ置く。SW_SHOWNOACTIVATE が処理される瞬間から新フォーカスが覆いになるため、
                // 旧フォーカスの縮小や Electron の白い再描画を表へ出さない。クロークは下地そのものを
                // 消して白背景を露出させ得るため使わない。
                if (!animateRestore && keepAboveHandle != IntPtr.Zero)
                {
                    ReleaseTopmostIfNeeded(placement.Handle);
                    PlaceDirectlyBehind(placement.Handle, keepAboveHandle);
                }

                PresetRestoreBoundsToCell(CompensateForFrameCached(placement));
                ShowWindow(placement.Handle, animateRestore ? SW_RESTORE : SW_SHOWNOACTIVATE);
                if (animateRestore)
                {
                    BringToFrontWithoutTopmostIfNeeded(keepAboveHandle);
                }
            }

            // 各ウィンドウの不可視枠（DWM 拡張フレームと GetWindowRect の差）を打ち消し、
            // 可視枠がセルにそろうように配置する。これで上端/下端/中央や縦横の隙間が均等になる。
            // 必ず復元「後」（全員が通常状態）に測ること。最大化中に測ると枠のはみ出し方が
            // 通常状態と異なり、セルより大きい/ずれたサイズで配置されてしまう。
            var targets = placements
                .Select(CompensateForFrameCached)
                .ToList();

            // かつては BeginDeferWindowPos/EndDeferWindowPos で一括配置していたが、
            // EndDeferWindowPos は SWP_ASYNCWINDOWPOS を尊重せず各ウィンドウへ同期
            // メッセージを送るため、ハング中の管理対象が 1 つあるだけで UI スレッドが
            // 無期限に凍結する。非同期フラグ付きの SetWindowPos を個別に発行する。
            var arranged = 0;
            foreach (var target in targets)
            {
                if (SetWindowPos(
                    target.Handle,
                    IntPtr.Zero,
                    target.X,
                    target.Y,
                    target.Width,
                    target.Height,
                    ArrangeFlags))
                {
                    arranged++;
                }
            }

            if (animateRestore)
            {
                BringToFrontWithoutTopmostIfNeeded(keepAboveHandle);
            }
            return arranged;
        }
        finally
        {
            if (!animateRestore)
            {
                foreach (var target in restoring)
                {
                    SetDwmTransitionsDisabled(target.Handle, false);
                }
            }
        }
    }

    private static bool BringToFrontWithoutTopmostCore(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        var demoted = IsTopmost(windowHandle)
            && SetWindowPos(windowHandle, HWND_NOTOPMOST, 0, 0, 0, 0, LayerFlags);
        var raised = SetWindowPos(windowHandle, HWND_TOP, 0, 0, 0, 0, LayerFlags);
        return demoted || raised;
    }

    private static bool IsTopmost(IntPtr windowHandle)
    {
        return (GetWindowLong(windowHandle, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
    }

    private static void BringToFrontWithoutTopmostIfNeeded(IntPtr windowHandle)
    {
        _ = BringToFrontWithoutTopmostCore(windowHandle);
    }

    // 復元先（通常時の位置）を目的セルへ事前設定する。rcNormalPosition はワークスペース座標
    // （プライマリディスプレイの作業領域原点が基準。タスクバーが下/右なら画面座標と一致）の
    // ため、プライマリ作業領域の原点ぶんを差し引く。誤差が残っても直後の SetWindowPos が
    // 画面座標で上書きするので、最終的な着地位置は常に正確になる。
    // WPF_RESTORETOMAXIMIZED も解除し、「最大化中に最小化」されたウィンドウが復元で最大化へ
    // 戻らず、セルへ向かうようにする。
    private static void PresetRestoreBoundsToCell(WindowPlacement target)
    {
        var placement = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>()
        };
        if (!GetWindowPlacement(target.Handle, ref placement))
        {
            return;
        }

        var monitors = GetOrderedMonitors();
        var primaryWorkArea = monitors[0].WorkArea;
        // 相手プロセスが応答不能でもブロックしないよう、配置要求を非同期で積む。
        placement.flags = WPF_ASYNCWINDOWPLACEMENT;
        placement.rcNormalPosition = new RECT
        {
            Left = target.X - primaryWorkArea.Left,
            Top = target.Y - primaryWorkArea.Top,
            Right = target.X + target.Width - primaryWorkArea.Left,
            Bottom = target.Y + target.Height - primaryWorkArea.Top
        };
        _ = SetWindowPlacement(target.Handle, ref placement);
    }

    // baseMonitorIndex は全ディスプレイ移動で決まる「ベース」。各スロットは MonitorOverride を
    // 持つときだけ別ディスプレイへ単独配置し、持たないときはベースに追従する。象限セル（index→
    // 列/行）は固定のまま、作業領域だけスロットごとの実効ディスプレイから取る。これにより同じ
    // ディスプレイへ複数スロットを単独移動しても、各々の象限へ自然にタイルする。
    private static List<WindowPlacement> BuildPlacements(
        IReadOnlyList<WindowSlot> slots,
        int gap,
        int baseMonitorIndex,
        WindowSlot? excludedSlot)
    {
        var monitors = GetOrderedMonitors();
        if (monitors.Count == 0)
        {
            return [];
        }

        var normalizedGap = Math.Clamp(gap, 0, 64);
        var placements = new List<WindowPlacement>(Math.Min(4, slots.Count));

        for (var index = 0; index < Math.Min(4, slots.Count); index++)
        {
            var slot = slots[index];
            if (ReferenceEquals(slot, excludedSlot))
            {
                continue;
            }

            // フォーカス中（1 面・最大化）のスロットはタイル配置の対象外。これにより全 Arrange は
            // 各ディスプレイの最大化ウィンドウを保ったまま、非フォーカスのみを象限へ並べる。
            // 複数ディスプレイで同時にフォーカス（各ディスプレイ 1 つ）を維持する土台になる。
            if (slot.IsFocused)
            {
                continue;
            }

            if (slot.WindowHandle == IntPtr.Zero || !IsWindow(slot.WindowHandle))
            {
                continue;
            }

            var effectiveMonitor = NormalizeMonitorIndex(slot.MonitorOverride ?? baseMonitorIndex, monitors.Count);
            var workArea = monitors[effectiveMonitor].WorkArea;
            var cellWidth = Math.Max(320, (workArea.Width - normalizedGap * 3) / 2);
            var cellHeight = Math.Max(240, (workArea.Height - normalizedGap * 3) / 2);

            var column = index % 2;
            var row = index / 2;
            var x = workArea.Left + normalizedGap + column * (cellWidth + normalizedGap);
            var y = workArea.Top + normalizedGap + row * (cellHeight + normalizedGap);
            placements.Add(new WindowPlacement(slot.WindowHandle, x, y, cellWidth, cellHeight));
        }

        return placements;
    }

    private static bool IsCloseToExpectedBounds(WindowBounds current, WindowPlacement expected, int tolerance)
    {
        return Math.Abs(current.Left - expected.X) <= tolerance
            && Math.Abs(current.Top - expected.Y) <= tolerance
            && Math.Abs(current.Width - expected.Width) <= tolerance
            && Math.Abs(current.Height - expected.Height) <= tolerance;
    }

    // セル（可視枠で表現した目標矩形）を、ウィンドウの不可視枠ぶん外側へ広げた
    // 実際の SetWindowPos 用矩形へ変換する。
    private WindowPlacement CompensateForFrameCached(WindowPlacement cell)
    {
        var inset = GetFrameInsetCached(cell.Handle);
        return new WindowPlacement(
            cell.Handle,
            cell.X - inset.Left,
            cell.Y - inset.Top,
            cell.Width + inset.Left + inset.Right,
            cell.Height + inset.Top + inset.Bottom);
    }

    // 不可視枠は「通常状態」のときに測った値だけを信用し、ハンドルごとにキャッシュする。
    // 最大化中は枠が画面外へはみ出し、最小化中は GetWindowRect が無効な座標を返すため、
    // そのまま測ると補正が狂って 4 面セルより大きい/ずれた配置になる。通常状態でない間は
    // 直近のキャッシュ値（無ければ補正なし）で代用し、最終配置は復元後の実測で行う。
    private FrameInset GetFrameInsetCached(IntPtr windowHandle)
    {
        if (!IsIconic(windowHandle) && !IsZoomed(windowHandle))
        {
            var inset = GetFrameInset(windowHandle);
            if (_frameInsetCache.Count > 64)
            {
                _frameInsetCache.Clear();
            }

            _frameInsetCache[windowHandle] = inset;
            return inset;
        }

        return _frameInsetCache.TryGetValue(windowHandle, out var cached) ? cached : FrameInset.Zero;
    }

    private readonly Dictionary<IntPtr, FrameInset> _frameInsetCache = new();

    // GetWindowRect と DWM 拡張フレーム（可視枠）の差＝各辺の不可視枠幅を返す。
    // DWM 非対応や取得失敗時は補正なし（ゼロ）。
    private static FrameInset GetFrameInset(IntPtr windowHandle)
    {
        if (GetWindowRect(windowHandle, out var windowRect)
            && DwmGetWindowAttribute(windowHandle, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<RECT>()) == 0)
        {
            var left = frame.Left - windowRect.Left;
            var top = frame.Top - windowRect.Top;
            var right = windowRect.Right - frame.Right;
            var bottom = windowRect.Bottom - frame.Bottom;
            if (left >= 0 && top >= 0 && right >= 0 && bottom >= 0
                && (left > 0 || top > 0 || right > 0 || bottom > 0))
            {
                return new FrameInset(left, top, right, bottom);
            }
        }

        return FrameInset.Zero;
    }

    // ウィンドウの可視枠（DWM 拡張フレーム）を返す。取得できないときは GetWindowRect で代用。
    private static bool TryGetVisibleBounds(IntPtr windowHandle, out WindowBounds bounds)
    {
        if (DwmGetWindowAttribute(windowHandle, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<RECT>()) == 0)
        {
            bounds = new WindowBounds(
                frame.Left,
                frame.Top,
                Math.Max(0, frame.Right - frame.Left),
                Math.Max(0, frame.Bottom - frame.Top));
            return true;
        }

        if (GetWindowRect(windowHandle, out var rect))
        {
            bounds = new WindowBounds(
                rect.Left,
                rect.Top,
                Math.Max(0, rect.Right - rect.Left),
                Math.Max(0, rect.Bottom - rect.Top));
            return true;
        }

        bounds = default;
        return false;
    }

    public bool BringToFront(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return SetWindowPos(windowHandle, HWND_TOPMOST, 0, 0, 0, 0, LayerFlags);
    }

    public bool BringToFrontOnce(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        if (IsIconic(windowHandle))
        {
            ShowWindow(windowHandle, SW_RESTORE);
        }

        var raised = SetWindowPos(windowHandle, HWND_TOPMOST, 0, 0, 0, 0, LayerFlags);
        var demoted = SetWindowPos(windowHandle, HWND_NOTOPMOST, 0, 0, 0, 0, LayerFlags);
        return raised || demoted;
    }

    public bool BringToFrontWithoutTopmost(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        if (IsIconic(windowHandle))
        {
            ShowWindow(windowHandle, SW_RESTORE);
        }

        return BringToFrontWithoutTopmostCore(windowHandle);
    }

    public bool SetBackmost(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        var demoted = SetWindowPos(windowHandle, HWND_NOTOPMOST, 0, 0, 0, 0, LayerFlags);
        var sentToBack = SetWindowPos(windowHandle, HWND_BOTTOM, 0, 0, 0, 0, LayerFlags);
        return demoted || sentToBack;
    }

    public bool ReleaseTopmost(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return SetWindowPos(windowHandle, HWND_NOTOPMOST, 0, 0, 0, 0, LayerFlags);
    }

    public bool ReleaseTopmostIfNeeded(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle) || !IsTopmost(windowHandle))
        {
            return false;
        }

        return SetWindowPos(windowHandle, HWND_NOTOPMOST, 0, 0, 0, 0, LayerFlags);
    }

    public static bool SetCloaked(IntPtr windowHandle, bool cloaked)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        var value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(windowHandle, DwmwaCloak, ref value, sizeof(int)) == 0;
    }

    // frontWindowHandle と同じ z-order 帯で、その直後へ windowHandle を置く。
    // パネル本体（front）> 1面フォーカス（window）の厳密な順序を1回の相対指定で保つ。
    public bool PlaceDirectlyBehind(IntPtr windowHandle, IntPtr frontWindowHandle)
    {
        if (windowHandle == IntPtr.Zero
            || frontWindowHandle == IntPtr.Zero
            || !IsWindow(windowHandle)
            || !IsWindow(frontWindowHandle))
        {
            return false;
        }

        return SetWindowPos(windowHandle, frontWindowHandle, 0, 0, 0, 0, LayerFlags);
    }

    // candidateWindowHandle が referenceWindowHandle より現在の Z 順で上にあるかを読み取る。
    // 状態を変更しないため、フォーカスのズームアニメーションや Electron の再描画へ干渉しない。
    public bool IsAbove(IntPtr candidateWindowHandle, IntPtr referenceWindowHandle)
    {
        if (candidateWindowHandle == IntPtr.Zero
            || referenceWindowHandle == IntPtr.Zero
            || candidateWindowHandle == referenceWindowHandle
            || !IsWindow(candidateWindowHandle)
            || !IsWindow(referenceWindowHandle))
        {
            return false;
        }

        for (var above = GetWindow(referenceWindowHandle, GW_HWNDPREV);
             above != IntPtr.Zero;
             above = GetWindow(above, GW_HWNDPREV))
        {
            if (above == candidateWindowHandle)
            {
                return true;
            }
        }

        return false;
    }

    // 指定ウィンドウより z-order が上に、管理外アプリの可視ウィンドウが重なって表示されて
    // いるかを返す。managedHandles（管理中スロットのウィンドウ）と自プロセスのウィンドウは
    // 「身内」として遮蔽とみなさない。TOPMOST ウィンドウも除外する。前面化（NOTOPMOST へ
    // 降ろす実装）では覆えない相手なので、遮蔽扱いにするとクリックでフォーカス解除へ
    // 永遠に到達できなくなる。
    public bool IsObscuredByExternalWindow(IntPtr windowHandle, IReadOnlyCollection<IntPtr> managedHandles)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle) || IsIconic(windowHandle))
        {
            return false;
        }

        if (!TryGetVisibleBounds(windowHandle, out var bounds) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var ownProcessId = (uint)Environment.ProcessId;
        for (var above = GetWindow(windowHandle, GW_HWNDPREV);
             above != IntPtr.Zero;
             above = GetWindow(above, GW_HWNDPREV))
        {
            if (managedHandles.Contains(above) || !IsWindowVisible(above) || IsIconic(above))
            {
                continue;
            }

            _ = GetWindowThreadProcessId(above, out var processId);
            if (processId == ownProcessId)
            {
                continue;
            }

            // タイトルなし（タスクバー・IME 等のシェル類）やツールウィンドウは操作対象の
            // アプリではないので遮蔽とみなさない。
            if (GetWindowTextLength(above) == 0)
            {
                continue;
            }

            var exStyle = GetWindowLong(above, GWL_EXSTYLE);
            if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_TOPMOST)) != 0)
            {
                continue;
            }

            // クローク中（UWP の停止中ウィンドウ等）は画面に描画されていない。
            if (DwmGetWindowAttributeInt(above, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                continue;
            }

            if (!TryGetVisibleBounds(above, out var aboveBounds)
                || aboveBounds.Width <= 0
                || aboveBounds.Height <= 0)
            {
                continue;
            }

            if (Intersects(bounds, aboveBounds))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Intersects(WindowBounds first, WindowBounds second)
    {
        return first.Left < second.Left + second.Width
            && second.Left < first.Left + first.Width
            && first.Top < second.Top + second.Height
            && second.Top < first.Top + first.Height;
    }

    public int GetMonitorCount()
    {
        return GetOrderedMonitors().Count;
    }

    public int GetDefaultMonitorIndex(string monitorSetting)
    {
        var monitors = GetOrderedMonitors();
        if (monitors.Count == 0)
        {
            return 0;
        }

        if (int.TryParse(monitorSetting, out var configuredIndex))
        {
            return NormalizeMonitorIndex(configuredIndex - 1, monitors.Count);
        }

        return 0;
    }

    public int GetMonitorIndexForWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return -1;
        }

        var monitorHandle = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
        var monitors = GetOrderedMonitors();
        for (var index = 0; index < monitors.Count; index++)
        {
            if (monitors[index].Handle == monitorHandle)
            {
                return index;
            }
        }

        return -1;
    }

    public string GetMonitorLabel(int monitorIndex)
    {
        var monitorCount = GetMonitorCount();
        if (monitorCount == 0)
        {
            return "ディスプレイ 1/1";
        }

        return $"ディスプレイ {NormalizeMonitorIndex(monitorIndex, monitorCount) + 1}/{monitorCount}";
    }

    // バッジ表示用に、指定インデックスを現在のモニタ枚数で正規化した 1 始まりの番号を返す。
    public int ResolveMonitorNumber(int monitorIndex)
    {
        var monitorCount = GetMonitorCount();
        return NormalizeMonitorIndex(monitorIndex, monitorCount) + 1;
    }

    public bool Focus(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        ShowWindow(windowHandle, SW_RESTORE);
        return SetForegroundWindow(windowHandle);
    }

    public bool FocusMaximized(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        ShowWindow(windowHandle, SW_MAXIMIZE);
        return SetForegroundWindow(windowHandle);
    }

    // 対象は直前に通常 z-order の先頭へ配置済み。ShowWindow は ShowWindowAsync への別名なので、
    // 要求直後に SetForegroundWindow すると最大化処理よりアクティブ化が先行し、通常サイズの
    // Electron サーフェスが白いまま DWM のズーム素材になる。IsZoomed で最大化要求の反映を
    // 確認してから一度だけフォアグラウンドを渡す。
    // 単独移動したスロットのフォーカス（1 面）を
    // その実効ディスプレイで開くために使う。ウィンドウが既にそのディスプレイに居れば移動しない。
    public async Task<bool> FocusMaximizedOnMonitorAsync(IntPtr windowHandle, int monitorIndex)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        var movedAcrossMonitors = EnsureWindowOnMonitor(windowHandle, monitorIndex);
        try
        {
            ShowWindow(windowHandle, SW_MAXIMIZE);
            var startedAt = DateTimeOffset.UtcNow;
            while (IsWindow(windowHandle)
                && !IsZoomed(windowHandle)
                && DateTimeOffset.UtcNow - startedAt < MaximizeRequestTimeout)
            {
                await Task.Delay(MaximizeStatePollInterval);
            }

            return SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (movedAcrossMonitors)
            {
                SetDwmTransitionsDisabled(windowHandle, false);
            }
        }
    }

    public bool Maximize(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return ShowWindow(windowHandle, SW_MAXIMIZE);
    }

    // 前面化を伴わずに指定ディスプレイで最大化する。非表示からのフォーカス復帰で使う。
    public bool MaximizeOnMonitor(IntPtr windowHandle, int monitorIndex)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        var movedAcrossMonitors = EnsureWindowOnMonitor(windowHandle, monitorIndex);
        try
        {
            return ShowWindow(windowHandle, SW_MAXIMIZE);
        }
        finally
        {
            if (movedAcrossMonitors)
            {
                SetDwmTransitionsDisabled(windowHandle, false);
            }
        }
    }

    // ウィンドウが指定ディスプレイに無ければ、そのディスプレイの作業領域内へ移してから
    // 最大化できるようにする。SW_MAXIMIZE は「現在ウィンドウが載っているディスプレイ」へ
    // 最大化するため、先に移動しておかないと別ディスプレイで最大化されてしまう。
    // ディスプレイをまたいで移動した場合は true を返す。その間は「復元アニメ → 暫定位置 →
    // 最大化」の三段の見た目になるのを避けるため遷移アニメを止めておくので、呼び出し側は
    // 最大化まで終えたあとに SetDwmTransitionsDisabled(handle, false) で必ず戻すこと。
    private static bool EnsureWindowOnMonitor(IntPtr windowHandle, int monitorIndex)
    {
        var monitors = GetOrderedMonitors();
        if (monitors.Count == 0)
        {
            return false;
        }

        var target = NormalizeMonitorIndex(monitorIndex, monitors.Count);
        var currentHandle = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
        if (monitors[target].Handle == currentHandle)
        {
            return false;
        }

        SetDwmTransitionsDisabled(windowHandle, true);
        RestoreForResize(windowHandle);
        var workArea = monitors[target].WorkArea;
        // 直後に SW_MAXIMIZE するので暫定サイズ。作業領域内へ確実に載せることだけが目的。
        SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            workArea.Left + 40,
            workArea.Top + 40,
            Math.Max(320, workArea.Width - 80),
            Math.Max(240, workArea.Height - 80),
            ArrangeFlags);
        return true;
    }

    public bool Close(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return PostMessage(windowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public bool Minimize(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return ShowWindow(windowHandle, SW_MINIMIZE);
    }

    public bool Restore(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return ShowWindow(windowHandle, SW_RESTORE);
    }

    // ウィンドウが最小化（アイコン化）されているか。ユーザーがアプリ外で直接最小化したかの判定に使う。
    public bool IsMinimized(IntPtr windowHandle)
    {
        return windowHandle != IntPtr.Zero && IsWindow(windowHandle) && IsIconic(windowHandle);
    }

    public bool TryGetWindowBounds(IntPtr windowHandle, out WindowBounds bounds)
    {
        bounds = default;
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle) || !GetWindowRect(windowHandle, out var rect))
        {
            return false;
        }

        bounds = new WindowBounds(
            rect.Left,
            rect.Top,
            Math.Max(0, rect.Right - rect.Left),
            Math.Max(0, rect.Bottom - rect.Top));
        return true;
    }

    public bool TryGetMonitorWorkAreaForWindow(IntPtr windowHandle, out WindowBounds workAreaBounds)
    {
        workAreaBounds = default;
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        var monitorHandle = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
        if (monitorHandle == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };

        if (!GetMonitorInfo(monitorHandle, ref info))
        {
            return false;
        }

        workAreaBounds = new WindowBounds(
            info.rcWork.Left,
            info.rcWork.Top,
            Math.Max(0, info.rcWork.Right - info.rcWork.Left),
            Math.Max(0, info.rcWork.Bottom - info.rcWork.Top));
        return true;
    }

    // 指定ディスプレイ（GetSlotMonitorIndex などで得た正規化済みインデックス）の作業領域を
    // 物理ピクセルで返す。最大化されたウィンドウが覆う範囲＝作業領域なので、その中央に
    // フォーカス名のオーバーレイを重ねるための座標計算に使う。
    public bool TryGetMonitorWorkArea(int monitorIndex, out WindowBounds workAreaBounds)
    {
        workAreaBounds = default;
        var monitors = GetOrderedMonitors();
        if (monitors.Count == 0)
        {
            return false;
        }

        var target = NormalizeMonitorIndex(monitorIndex, monitors.Count);
        var workArea = monitors[target].WorkArea;
        workAreaBounds = new WindowBounds(workArea.Left, workArea.Top, workArea.Width, workArea.Height);
        return true;
    }

    public bool SetWindowBounds(IntPtr windowHandle, WindowBounds bounds)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            Math.Max(1, bounds.Width),
            Math.Max(1, bounds.Height),
            ArrangeFlags);
    }

    private static void RestoreForResize(IntPtr windowHandle)
    {
        if (IsIconic(windowHandle) || IsZoomed(windowHandle))
        {
            ShowWindow(windowHandle, SW_RESTORE);
        }
    }

    // 対象ウィンドウの DWM 遷移アニメ（最小化/復元/最大化時のズーム演出）を一時的に止める。
    // 失敗（DWM 無効や対象消滅）は無視してよい。必ず disabled=false で対で戻すこと。
    private static void SetDwmTransitionsDisabled(IntPtr windowHandle, bool disabled)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return;
        }

        var value = disabled ? 1 : 0;
        _ = DwmSetWindowAttribute(windowHandle, DwmwaTransitionsForceDisabled, ref value, sizeof(int));
    }

    private static List<MonitorWorkArea> GetOrderedMonitors()
    {
        var monitors = new List<MonitorWorkArea>();

        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFO
            {
                cbSize = Marshal.SizeOf<MONITORINFO>()
            };

            if (GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new MonitorWorkArea(
                    hMonitor,
                    new WorkArea(
                        info.rcWork.Left,
                        info.rcWork.Top,
                        info.rcWork.Right - info.rcWork.Left,
                        info.rcWork.Bottom - info.rcWork.Top),
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        }, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            var monitor = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO
            {
                cbSize = Marshal.SizeOf<MONITORINFO>()
            };

            if (GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(new MonitorWorkArea(
                    monitor,
                    new WorkArea(
                        info.rcWork.Left,
                        info.rcWork.Top,
                        info.rcWork.Right - info.rcWork.Left,
                        info.rcWork.Bottom - info.rcWork.Top),
                    true));
            }
            else
            {
                monitors.Add(new MonitorWorkArea(IntPtr.Zero, new WorkArea(0, 0, 1280, 720), true));
            }
        }

        return monitors
            .OrderByDescending(item => item.IsPrimary)
            .ThenBy(item => item.WorkArea.Left)
            .ThenBy(item => item.WorkArea.Top)
            .ToList();
    }

    private static int NormalizeMonitorIndex(int monitorIndex, int monitorCount)
    {
        if (monitorCount <= 0)
        {
            return 0;
        }

        var normalizedIndex = monitorIndex % monitorCount;
        if (normalizedIndex < 0)
        {
            normalizedIndex += monitorCount;
        }

        return normalizedIndex;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ShowWindow ではなく ShowWindowAsync を使う。ShowWindow は他プロセスのウィンドウへ
    // 同期メッセージを送るため、相手スレッドがハングしていると呼び出し元（パネルの UI
    // スレッド）が無期限にブロックする。Async 版は表示要求をキューに積んで即座に戻る。
    [DllImport("user32.dll", EntryPoint = "ShowWindowAsync")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    // 拡張スタイル（GWL_EXSTYLE）は 64bit プロセスでも 32bit 値なので GetWindowLongW で足りる。
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    public readonly record struct WindowBounds(int Left, int Top, int Width, int Height);

    private readonly record struct WindowPlacement(IntPtr Handle, int X, int Y, int Width, int Height);

    private readonly record struct FrameInset(int Left, int Top, int Right, int Bottom)
    {
        public static FrameInset Zero { get; } = new(0, 0, 0, 0);
    }

    private readonly record struct WorkArea(int Left, int Top, int Width, int Height);

    private readonly record struct MonitorWorkArea(IntPtr Handle, WorkArea WorkArea, bool IsPrimary);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }
}
