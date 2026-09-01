# Turtle AI Code Quartet Hub 実装パターン・注意点

更新日: 2026-05-13

## 1. AI状態監視を再導入しない

- AI状態検出、UI Automation のチャット走査、拡張ログ解析、VS Code外周オーバーレイは削除済み。
- `StatusStore.RefreshWindowStatusesAsync` は HWND/タイトル/ワークスペース更新の軽量処理として維持する。
- AI状態のためにパネルカード色、縮小ボタン色、点滅アニメーション、Jump List 文言を変えない。
- focused/selected の枠表示は AI 状態と無関係な操作フィードバックなので維持する。

## 2. スロット状態と保存

- `slots.json` は visible slot と stored panel を同じドキュメントで保持する。
- legacy 読み込み互換は壊さない。
- `WindowSlot` は管理対象ウィンドウ、タイトル、ワークスペース、フォーカス、表示/非表示、レイヤー状態だけを持つ。
- AI状態、最終AIイベント時刻、AI状態詳細は持たない。

## 3. 定期更新

- 更新対象は管理対象ウィンドウの存在、タイトル、ワークスペース表示のみ。ワークスペース読み取りは VS Code スロットに限定する。
- ワークスペース取得は `WorkspaceRefreshInterval` を使い、毎 tick で重い読み取りをしない。
- 操作中のレイアウト変更では `_suppressPeriodicRefreshUntil` で短時間抑止し、UI操作を優先する。

## 4. フォーカス/集中表示

- focused slot は `StatusStore.SetFocusedSlot` で UI 状態を保持する。
- 対象 VS Code は `FocusMaximized`、他スロットは `ArrangeExcept` と `SendOtherSlotsToBack` で制御する。
- パネルクリック直後に `SetForegroundWindow` が走るとクリックがキャンセルされるため、遅延 reassert と入力抑止を維持する。

## 5. UI 表現

- 標準カードと縮小ボタンは常に安定したサイズを持つ。
- 選択中スロットの外枠は残す。
- AI状態に由来する色変え、発光、点滅、状態ピルは追加しない。
- テキストがはみ出る場合は `TextTrimming` と固定寸法で吸収する。

## 6. ビルド

- 実行中の本体が既定の `bin\Debug` 出力をロックする場合がある。
- 反復確認では `scripts/Build-Panel.ps1` または `--artifacts-path` を使う。

## 7. 複数アプリ起動

- 各スロットの既定アプリは VS Code (`vscode`) とし、一括起動はスロットごとの `ApplicationId` に従う。
- VS Code は `VscodeLauncher` に残し、専用 user-data-dir、リモート URI、workspaceStorage 読み取りの既存挙動を壊さない。
- Antigravity など VS Code 以外の workspace IDE は `ApplicationLauncher` の汎用起動で扱う。
- Codex / Claude は `SingleWindowAgent` として、既存ウィンドウ探索やウィンドウ検出待ちをせず起動コマンドを送信する。閉じる/切り替えるトグル動作にしない。
- Claude / Codex の Windows Store 版は通常の PATH やスタートメニュー `.lnk` で検出できない場合があるため、AppModel Repository から AppUserModelID を解決し、`shell:AppsFolder` 経由で起動する。起動済みプロセスと `PackageRootFolder` も補助的に見る。
- `WindowEnumerator` はスロットの `ApplicationId` に対応する process names で確認する。VS Code 以外に `VscodeWorkspaceState` を適用しない。
- 未検出アプリはボタンを無効化し、`ToolTip` とメッセージで設定パス/コマンド確認へ誘導する。
- UI は VS Code / Antigravity を各スロット内の等幅ボタン、Codex / Claude を控え Quartet 行の右端補助ボタンとして配置する。Launch 直上にグローバル IDE 選択ボタンは置かない。AI状態の色変えや点滅は追加しない。
- 各スロットにも VS Code / Antigravity 切替を置く。未起動スロットでは選択状態だけ保存し、起動中スロットで別アプリを選んだ場合は現在のウィンドウを閉じて同じスロット内容を選択アプリで開き直す。

## 8. ディスプレイ移動（全移動＋単独移動, 2026-06-08 追加）
- 配置先は「ベースディスプレイ（全体）」＋「各スロットの単独 override（`WindowSlot.MonitorOverride`, 非永続）」で表す。実効ディスプレイ＝`override ?? ベース`。フォーカスやレイヤーと同じランタイム状態として扱い、`slots.json` には保存しない。`WindowSlot.ClearWindow` で override も解除する。
- フッタの「全ディスプレイ移動」（旧「ディスプレイ移動」）はベース（`_activeMonitorIndex`）だけを次の面へ進める。進めた直後に `CollapseMonitorOverridesMatchingBase` で「override == 新ベース」のスロットの override を解除し、群れへ合流させる。これで「2 に単独移動 → 全移動でベースが 1→2 → 単独スロットは 2 に留まりつつ合流」が成立する。単独移動を 2→1 へ押し戻すスワップは行わない（リベースの不変条件）。
- 各スロットカードの「単独ディスプレイ移動」ボタン（モニタアイコン）は、そのスロットの実効ディスプレイを次へ巡回する。次がベースに一致したら override=null（ベースに戻る）。2 枚運用では「ベース ⇄ もう一方」のトグル。管理中ウィンドウが無いスロットでは無効。
- `WindowArranger.BuildPlacements` は象限セル（index→列/行）を固定したまま、作業領域だけスロットごとの実効ディスプレイから取る。同じ面へ複数スロットを単独移動しても各々の象限へタイルする。`Arrange` / `ArrangeExcept` / `NeedsArrange` の `monitorIndex` 引数は「ベース」を意味し、内部で per-slot に解決する。
- フォーカス（1 面）は実効ディスプレイで最大化する（`FocusMaximizedOnMonitor` / `MaximizeOnMonitor`）。`EnsureWindowOnMonitor` は対象が既にその面なら何もしないため、未移動スロットは従来どおりベース面で最大化する。単独移動したパネルをフォーカス・フォーカス解除しても、他ディスプレイのパネルは触らない。
- モニタ構成が変わったとき（単独移動先のディスプレイを抜く等）は `NormalizeMonitorOverrides` が override を健全化する。正規化後にベースと一致するものは解除、範囲外は丸める。1 枚運用ではすべてベースへ収束し単独移動は自然解消する。
- カードの `DisplayBadgeText`（例 "D2"）は、複数モニタかついずれかのスロットが単独移動中のときだけ表示する。全パネルが同一面に揃っているときは雑音を出さない。`RefreshAuxiliaryUi` から `UpdateDisplayBadges` を呼んで常時最新化する。

## 9. 複数ディスプレイの同時フォーカス（2026-06-09 追加）
- フォーカス（1 面・最大化）は「全体で 1 つ」ではなく「各ディスプレイで 1 つ」。3 画面なら最大 3 フォーカスを許容する。`WindowSlot.IsFocused` は複数同時 true を取り得る（ただし同一実効ディスプレイには 1 つ）。
- 土台は `WindowArranger.BuildPlacements` が `IsFocused` のスロットを必ずスキップすること。これで全 Arrange は各ディスプレイの最大化ウィンドウを保ったまま非フォーカスだけ象限へ並べる。フォーカス操作は基本「フラグを更新 → Arrange → `ReassertAllFocusedSlots`（各面で最大化・前面化）」で表現する。
- フォーカスの集合操作はディスプレイ単位のヘルパで行う: `GetSlotMonitorIndex` / `GetFocusedSlotOnMonitor` / `SetFocusedSlotForDisplay`（同面の旧フォーカスだけ解除）/ `ClearFocusedSlotForDisplay` / `SendOtherSlotsToBackOnSameDisplay`。z-order は「同じ実効ディスプレイの他スロットだけ背面へ」。別ディスプレイのフォーカスや前後は触らない。
- リアサート（`ReassertFocusedSlotIfNeeded`）と手動最小化整合（`ReconcileExternallyMinimizedFocusedSlot`）、非表示復帰（`_hiddenFocusedSlots` リスト）、`CaptureFocusedLayout`、`ApplyLayerPreservingFocusMode`、`ArrangeSlotsAfterPanelStateChange` はすべて全フォーカスをループ処理する。1 つの面を手動最小化したらその面のフォーカスだけ解除し、他面は維持する。
- スロットクリックのトグルは「そのディスプレイだけ」を 1 面/4 面で切り替える。他ディスプレイのフォーカスは保持。
- 全ディスプレイ移動を押したら、ベースに追従するスロット（override なし）のフォーカスだけ解除して 4 面に戻す。単独移動中（override あり）のスロットはディスプレイが変わらないため、その面のフォーカス（1 面表示）を維持し、配置後に `ReassertAllFocusedSlots` で立て直す。リベース（override==新ベースの解除）は従来どおり行い、解除されたスロットはベース追従としてフォーカスも解除対象になる。
- 単独ディスプレイ移動とフォーカスの干渉は「移動先優先」: フォーカス中パネルを、既にフォーカスを持つディスプレイへ移すと、移動先のフォーカスを優先し、移動したパネルはフォーカス解除（移動先で非フォーカス＝最大化の背面）になる。移動元のフォーカスはそのパネルが去るので自然に解ける。移動先にフォーカスが無ければフォーカスを引き継いで移動先で最大化する。
- ディスプレイ色: ベース（作業ベース）は常に緑（`AccentBrush`）、非ベースは番号の小さい順に 青 `DisplayAccent2Brush` → 紫 `DisplayAccent3Brush` → 金 `DisplayAccent4Brush`（`GetDisplayBrushForMonitor`）。全ディスプレイ移動でベースが動いても「緑＝ベース」を維持する（番号固定ではない）。
- 色は `WindowSlot.DisplayBrush`（実効ディスプレイの色、常時更新）に集約し、バッジ文字色・枠線色、フォーカス枠 `FocusFrameBrush`（フォーカス中だけ DisplayBrush、それ以外は透明）に使う。`FocusFrameBrush` は標準カード `SlotCardFocusFrame`、縮小 `CompactFocusFrame`、極小 `MicroFocusFrame` の `BorderBrush` に直接バインドする（`Setter.Value` はバインド不可のためトリガではなく直接バインド。トリガ側の固定色 `BorderBrush` 設定は撤去）。グロー(DropShadow)と LED ドットは Setter 内 Freezable で DataContext が効かないため緑のまま。
- 色解決 `GetDisplayBrushForMonitor` は `TryFindResource`（見つからなければ緑フォールバック）で必ず例外を出さない。`UpdateDisplayBadges` は毎ティックの `RefreshAuxiliaryUi` から呼ばれるため、ここで例外を投げるとアプリ全体が落ちる。
- リアサート（`ReassertFocusedSlotIfNeeded` / `ReassertAllFocusedSlots`）は `SetForegroundWindow` を呼ばない（`MaximizeOnMonitor` + `BringToFrontOnce` + 同面背面のみ）。複数ディスプレイのフォーカスへ毎回フォアグラウンドを奪うと、アクティベーション争奪でちらつき・ハングになる。前面化はパネル（`SchedulePanelToFront`）に任せる。初回フォーカス（`ToggleSlotFocus`）だけは従来どおり `FocusMaximizedOnMonitor` で一度だけ前面化する。
- 単独移動は settle 付き再配置の await を挟む前に `ReassertAllFocusedSlots` で移動先の最大化を先行確定し、「4 面のまま少し残ってから 1 面へ遷移」する見た目のラグを抑える。
- 最前面/最背面（`ApplyLayerPreservingFocusMode`）は、非フォーカスの全スロットへレイヤーを適用してから、フォーカスを持つ各ディスプレイで同面の前後関係を保ち直す。フォーカスの無いディスプレイのウィンドウも確実に前面/背面化される。
- 致命傷対策: `App.OnStartup` で `DispatcherUnhandledException`（`e.Handled=true`）/ `TaskScheduler.UnobservedTaskException`（`SetObserved`）/ `AppDomain.UnhandledException` をログ付きで握りつぶす。外部ウィンドウや P/Invoke の一過性失敗でランチャー本体が勝手に終了する事故を防ぐ。
- 周期更新 `RefreshTimer_Tick` は async void のため内部でも try/catch して `DiagnosticLog` へ記録する（`DispatcherUnhandledException` の手前で握る多重防御）。
- D&D 入替（`SwapSlotContents`）は `MonitorOverride` もワークスペース側と一緒に交換する。入替はカード上の象限位置だけを交換し、各ワークスペースは元のディスプレイ・フォーカス状態・4 面/1 面の見え方を維持する（入替でウィンドウはディスプレイをまたがない）。
- 入替・単独移動・全体移動後の DPI/解像度差の補正は、フォーカスがあっても走る `SettleArrangementPreservingFocusAsync`（`NeedsArrange` はフォーカス中スロットをスキップ）で行う。`ArrangeSlotsOnActiveMonitorWithSettlingAsync` の settling は `CanReapplyPostLaunchArrangement` によりフォーカス中は走らないため、フォーカスを保つ操作では使わない。
- ズームアウト演出とちらつき対策: 配置（`ArrangeCore`）は最大化/最小化中のウィンドウの復元先 `rcNormalPosition` を目的セルへ事前設定（`SetWindowPlacement`。`WPF_RESTORETOMAXIMIZED` も解除）してから `SW_RESTORE` する。DWM の復元アニメは復元先矩形へ向かって再生されるため、ズーム解除はアニメーション付きで目的セルへ直接着地し、「旧位置へ戻ってからセルへジャンプ」する二段移動（ちらつき）は出ない。rcNormalPosition はワークスペース座標（プライマリ作業領域原点基準）なので原点ぶん補正し、残差は直後の `SetWindowPos`（画面座標）が吸収する。
- アニメを出さない配置: フォーカスイン随伴の背面整列（`ArrangeSlotsExceptOnActiveMonitor`）と settling 補正（`ArrangeSlotsOnActiveMonitorQuietly`）は `animateRestore=false` で、復元が必要なウィンドウにだけ `DWMWA_TRANSITIONS_FORCEDISABLED` を対で適用して無音・即時に行う。A フォーカス中に B を押す即切替は、B のズームイン（`SW_MAXIMIZE`）を前面で演出し、A の解除は遅延後に B の背面でアニメ無しで済ませる。ディスプレイまたぎのフォーカス移動（`EnsureWindowOnMonitor`〜最大化）も遷移アニメを止める。
- フォーカスイン時の透け対策: 他スロットの背面送り（`SendOtherSlotsToBackOnSameDisplay`）は最大化アニメ完了後（`FocusSwitchArrangeDelay` 経過後）に行う。アニメ開始と同時に HWND_BOTTOM へ送ると、最大化が画面を覆い切るまでタイルの位置に管理外ウィンドウ（ブラウザ等）が透けて見える。
- 不可視枠（DWM 拡張フレームと GetWindowRect の差）は「通常状態」のときにだけ計測し、ハンドルごとにキャッシュする（`GetFrameInsetCached`）。最大化中は枠のはみ出し方が異なり、最小化中は座標が無効なため、そのまま測ると 4 面セルより大きい/ずれた配置になる。復元前の事前補正（rcNormalPosition）はキャッシュ値、最終配置（SetWindowPos）は復元後の実測で行う。
- パネル UI の描画は GPU 既定（`RenderMode.SoftwareOnly` 強制は撤去）。特定環境で描画乱れが出る場合のみ SoftwareOnly へ戻す。

## 10. SSH 接続名を含むワークスペース照合（2026-09-01 追加）
- **ファイル**: `VscodeWorkspaceState.cs`, `TurtleAIQuartetHub.Panel.Tests/VscodeWorkspaceStateTests.cs`
- **問題**: 異なる SSH 接続先で同名フォルダを開くと、フォルダ名の一致スコアが同点になり、`workspaceStorage` で先に並んだ古い `vscode-remote://ssh-remote+...` URI を現在値として保存することがあった。次回起動でも古い SSH 接続名を再利用するため、廃止済み接続先では接続に失敗する。
- **対策**: VS Code タイトルに `[SSH: 接続名]` が見える場合は、URI authority の `ssh-remote+接続名` と一致しない候補を除外し、一致する候補へ最優先スコアを与える。選択された現在 URI は既存の `StatusStore` 経路で `Path` / `SavedWorkspacePath` / `slots.json` へ保存する。
- **注意**: フォルダ名・ファイル名の誤判定防止を維持し、単純な `Contains` へ戻さない。カスタム `window.title` で `[SSH: ...]` が出ない場合はフォルダ名照合へフォールバックし、候補を根拠なく破棄しない。Remote-SSH の履歴や認証情報、`workspaceStorage` 自体は削除しない。
