# Turtle AI Code Quartet Hub 実装パターン・注意点

更新日: 2026-07-23

## 1. AI 状態監視を戻さない
- AI 状態検出、UI Automation のチャット走査、拡張ログ解析、VS Code 外枠オーバーレイは削除済み。
- `StatusStore.RefreshWindowStatusesAsync` は HWND、タイトル、ワークスペース表示更新の軽量処理として維持する。
- AI 状態に由来するカード色、縮小ボタン色、点滅、Jump List 文言を復活させない。

## 2. スロット状態と保存
- `slots.json` は visible slot と stored panel を同じドキュメントで保持する。
- `WindowSlot` は管理対象ウィンドウ、タイトル、ワークスペース、フォーカス、表示/非表示、レイヤー状態だけを持つ。
- パネルカードを A-D 間でドラッグ移動するときは、表示位置の `Name` と VS Code user-data / workspaceStorage / code.lock 用の `RuntimeSlotName` を分けて扱う。実行中ウィンドウを B から D に移した場合、D は B の runtime profile を持ち続け、空いた B は D の runtime profile で新規起動することで、同じ user-data を二重に再利用しない。
- VS Code / Antigravity の workspaceStorage 読み取りに失敗しても `SavedWorkspacePath` は消さない。強制終了後や中途半端な user-data 状態でも、次回起動に使える保存済みパスを残す。
- 起動確認または periodic refresh で正しいワークスペースが読めた場合は、`Path` / `SavedWorkspacePath` / `SavedWorkspaceConfirmed` と自動タイトルを保存する。
- 控え Quartet から表示スロットへ復帰するときは、旧ウィンドウへ close を送っただけで次の起動へ進めない。置換用の close 待ち処理を通し、VS Code の再接続では起動予定の `SavedWorkspacePath` / `Path` とタイトルが一致しない既存 slot-owned window を採用しない。旧 workspace のウィンドウを新しいパネル表示へ誤接続すると、パネル名と実ワークスペースが永続的にずれる。

## 3. 複数アプリ起動
- 既定のスロットアプリは VS Code (`vscode`)。一括起動はスロットごとの `ApplicationId` に従う。
- VS Code は `VscodeLauncher` に残し、専用 user-data-dir、remote URI、workspaceStorage 読み取りの既存挙動を壊さない。
- Antigravity のワークスペース推定は VS Code 互換の `workspaceStorage` 形式を使い、`%APPDATA%/Antigravity/User/workspaceStorage` などの実アプリデータ候補を新しい順に見て、ウィンドウタイトルにワークスペース名が含まれるパスだけを採用する。
- Antigravity など VS Code 以外の workspace IDE は `ApplicationLauncher` の汎用起動で扱う。起動プロセスと表示ウィンドウのプロセス ID がずれるため、新規ウィンドウハンドルで割り当てる。
- Antigravity IDE は `%LOCALAPPDATA%/Programs/Antigravity IDE/Antigravity IDE.exe` 相当を優先検出する。過去設定の bare `antigravity` は新しい Antigravity Windows アプリと衝突しやすいため、既定検出へ戻して IDE 側を開く。
- Antigravity や terminal が起動完了後に中央へ戻ることがあるため、起動確認直後の配置に加えて短い遅延再配置を複数回行う。標準またはやや低性能な PC に備えて 8 秒、12 秒後の再配置も行い、ユーザーが集中表示に入った後は再配置しない。
- Codex / Claude / GitHub Copilot / Grok Build / Gemini は `WorkspaceCli`。対象スロットの保存済みワークスペースを working directory にした `cmd.exe /k` で CLI を起動し、terminal 系の新規ウィンドウをスロットに割り当てる。
- 一括起動では CLI 種別同士を並列捕捉しない。VS Code / Antigravity などの非 CLI は並列のまま、CLI グループだけ順次起動して terminal 系プロセス名の競合を避ける。
- Workspace CLI は同一 CLI 種別の複数スロットでも、まず各 `cmd.exe` を先に起動し、`Turtle {slot} - {shortName}` のタイトルで後から捕捉する。タイトルが Windows Terminal 側で反映されない場合は、新規 terminal ウィンドウを起動順で割り当てる。
- Workspace CLI はコマンド検出で可用性を判断する。PATH に加えて npm / pnpm / Volta の一般的な shim 置き場、Claude Code の公式インストーラが使うユーザー単位の `~\.local\bin`、Grok Build の Git Bash インストーラが使う `~\.grok\bin` も探索する。terminal プロセスが起動済みであることや Windows アプリ検出だけで CLI インストール済み扱いにしない。
- GitHub Copilot Chat 拡張が作る `globalStorage\github.copilot-chat\copilotCli\copilot*` は、実体が未インストール時のインストール案内ブートストラップなので、GitHub Copilot CLI インストール済み判定には使わない。
- Workspace CLI の実行コマンドは `ResolvedCommand` を優先し、短い `claude` などの設定コマンドだけに依存しない。起動した `cmd.exe` には検出済みコマンドのフォルダと一般的な shim 置き場を PATH 先頭へ一時追加する。
- GitHub Copilot CLI の既定起動は、対象ワークスペースへ `cd /d` して `copilot` だけを実行する。`workspacePath` は暗黙の引数として渡さない。
- Codex / ChatGPT / Claude / Antigravity2 の Windows アプリ版は CLI とは別に `codex-app` / `chatgpt-app` / `claude-app` / `antigravity-app` の `SingleWindowAgent` として補助ボタン行に残す。Antigravity2 は `%LOCALAPPDATA%/Programs/Antigravity/Antigravity.exe` 相当を優先検出し、Claude の右側に表示する。
- スロット内で別の IDE/CLI ボタンを押した場合は、現在のスロットウィンドウへ close を送り、短く待ってからスロット状態をクリアし、押されたアプリを同じスロット位置へ起動する。

## 4. UI とフォーカス
- UI は各スロット内に `IDE` 枠と `CLI` 枠を持つ。IDE 枠は VS Code / Antigravity を縦並び、CLI 枠は上段 `Codex` / `Claude`、下段 `Copilot` / `Grok` / `Gemini` で表示する。
- 選択中アプリのボタンはベタ塗りではなく、暗めの半透明に近い緑で表示する。ただし未検出アプリは選択中でも `IsAvailable=false` のグレーアウト表示を優先し、インストール済みと誤認させない。
- 未起動スロットの IDE / CLI 選択ボタンは起動対象を変更するだけで、アプリを自動起動しない。起動は個別スロットの起動ボタンか `Launch Quartet` のみで行う。
- メインパネルのクリアアイコンは右上のゴミ箱アイコンで表示する。押下時は削除確認ダイアログを出し、確認後に対象スロットの保存済みタイトル、パス、選択アプリ、ウィンドウ割り当てを削除する。起動中の IDE / CLI ウィンドウがある場合は close を送ってからパネル情報を削除する。
- 通常表示のスロット左下にはフォルダアイコンボタンを置き、`WindowSlot.DisplayPath` から Explorer で開けるローカルフォルダを解決する。既存ディレクトリはそのまま、`.code-workspace` など既存ファイルは親フォルダを開く。`vscode-remote://...` や `ssh://...` など non-file URI は Explorer 対象にせず、ボタンをグレーアウトする。
- スロットの実行中アクションボタン文言は `閉じる` にする。未起動時は `起動` / `新規`。
- タイトルバー右上は縮小表示、`?` ヘルプ、歯車設定、最小化、閉じるの順に置く。ヘルプは Codex / GitHub Copilot / Gemini / Claude Code / Grok Build の CLI 別カードで構成し、各カード内を `インストール` と `自律実行の起動オプション` に分ける。方式名とコマンドを別表示にしてコマンドだけを正確にコピーできるようにし、自律実行オプションは警告色と共通の注意書きで通常の導入手順から区別する。IDE / Windows アプリは公式サイト参照とし、Claude Code は公式インストーラの PowerShell / CMD コマンドと npm コマンドを書く。本文とコマンドは選択コピーできるよう `TextBox IsReadOnly=True` で表示し、実行操作は持たせない。
- CLI の導入方法は公式の最新情報を確認して更新し、README と `?` ヘルプで同じ選択肢を示す。Grok Build CLI の Windows 向け手順は Git Bash を経由させず、`irm https://x.ai/cli/install.ps1 | iex` を表示する。Git Bash / WSL 向けには `curl -fsSL https://x.ai/cli/install.sh | bash` を残す。
- 歯車設定画面では IDE / CLI / Windows アプリの起動コマンドを編集し、`%LOCALAPPDATA%/TurtleAIQuartetHub/config/turtle-ai-quartet-hub.json` へ保存する。VS Code の設定は `CodeCommand` と `applications[].command` を同期させる。
- 設定画面には表の Quartet と控え Quartet の保存状態を一覧表示する。表は `PanelTitle` / `Path` / `SavedWorkspacePath` / `SavedWorkspaceConfirmed` / `ApplicationId` を編集可能にし、控えは `PanelTitle` / `WorkspacePath` / `ApplicationId` を編集可能にする。空化ボタンと不整合修復ボタンを用意し、過去の重複控えや不完全な控えで再登録できない状態を解消できるようにする。
- Codex / ChatGPT / Claude / Antigravity2 の Windows アプリ版は、補助ボタン行の左に `Windows` ラベルを置いて CLI と区別する。Antigravity2 の文言が収まる固定幅にそろえ、縮小表示でも行全体が隠れない幅を確保する。
- 標準表示ではスロット領域をカード実寸の高さに詰め、控え Quartet までの黒い余白を作らない。下部の `Launch Quartet` ボタンも見切れないようにする。
- 集中表示中に同じスロットボタンを押したとき、対象ウィンドウの上に他アプリの可視ウィンドウが重なっている場合は 4 面表示へ戻さず、集中表示を維持して前面復帰する。
- 2026-05-14 時点では、CLI / Antigravity でも操作を予測しやすくするため、同じスロットボタンは常に 1 面フォーカス表示と 4 面表示のトグルとして扱う。上に他アプリが重なっているかどうかでは分岐しない。
- パネルカードや外部ウィンドウのドラッグ操作中は、フォーカス中スロットの再前面化を抑制する。カードのドラッグアンドドロップ中と直後はスロットクリックによる 1 面フォーカストグルも一時抑止し、ドラッグ中のマウス移動やボタン解放が集中表示へ化けないようにする。1 面表示中にカード入れ替えや状態クリアが発生した場合は、全スロットを 4 面再配置せず、フォーカス対象以外だけを背面へ整える。

## 5. ビルド
- 実行中の本体が既定の `bin\Debug` 出力をロックする場合がある。
- 反復確認では `scripts/Build-Panel.ps1` または `--artifacts-path` を使う。

## 6. UI レイアウト細部（2026-05-14 修正済み）
- `ApplicationGroupLabelStyle` の Margin は `"8,-5,0,0"` で境界線上に浮かせる（負のtopマージンで境界線に重ねる）。正にするとボタンと被る。
- IDE / CLI ボタンはどちらも高さ 24、`ItemContainerStyle Margin="2,0,2,4"` で統一する。IDE StackPanel と CLI UniformGrid の上端・隙間をそろえる。
- CLI ボタン配列は上段 `Codex` / `Claude`、下段 `Copilot` / `Grok` / `Gemini`。下段は Copilot と Gemini を同じ可変幅、Grok を短い固定幅にし、5 ボタン化してもカード内に収める。
- `SlotCardTemplate` 内から `StaticResource` で参照する DataTemplate / Style は、必ず `SlotCardTemplate` より前に定義する。後方定義にするとビルドは通っても起動時に `XamlParseException` で終了する。
- 控え Quartet Expander のコンテンツ上マージンは `"0,12,0,0"`。右上の `Windows` 補助アプリボタン行がオーバーレイ配置のため、閉じた状態の隙間に寄せつつ、開いた控えカードと重ならないだけの隙間を確保する。
- 縮小表示は C/D 行と `Windows` 補助アプリ行が初期状態で隠れない高さを最低値にする。
- 縮小表示の 4 パネルボタン中央には、極小表示と同じ表示/非表示の円形ボタンを重ねる。4 パネルの空間メタファーを崩さないよう、右側の `前` / `背` 操作列には重ねない。
- 終了確認は本体内の緑基調オーバーレイを維持する。縮小・極小表示では本体の表示領域が確認UIより小さいため、確認中だけ本体を必要な最小サイズへ一時展開し、キャンセル時に元の位置・サイズ・最小寸法へ復元する。固定幅の確認UIを小さい本体内で単純にクリップさせない。
- 標準表示の既定高さは、`Launch Quartet` ボタン下に空白が残らない値にする。下端余白が見える場合はウィンドウ既定高さを先に調整する。
- Windows 補助アプリが 4 つに増えたため、縮小表示の最低幅は Antigravity2 ボタンまで収まる値にする。
- Windows 補助アプリボタンは 4 つすべて同じ幅を保ちつつ、左の `Windows` ラベルが `indows` のように見切れない幅に収める。

## 7. 一括起動の逐次化（2026-06-04 修正済み）
- `LaunchAllMissingAsync` では A-D の対象スロットを 1 つずつ順番に起動する。非 CLI も `Task.WhenAll` で並列起動しない。VS Code / Antigravity / terminal の新規ウィンドウ捕捉が競合し、4 つ起動しきれない、または別スロットの遅延ウィンドウを拾って 2x2 配置がずれる問題を避けるため。
- 各スロットの起動結果は捕捉できた時点で `StatusStore.AssignWindow` へ反映し、短い間隔を置いて次スロットへ進む。最後に `RefreshSlotsAsync` と `ArrangeSlotsOnActiveMonitorWithSettlingAsync` を実行し、遅れて再接続されたウィンドウも 2x2 配置対象に含める。
- 起動スロット間の待ちは短めに保ち、テンポよく進める。起動後の 260ms / 720ms 付近の即時補正と 30 秒までの遅延補正は、`WindowArranger.NeedsArrange` で位置ずれがある場合だけ実行する。追加補正ではレイヤー再適用やパネル前面化を繰り返さず、`ArrangeSlotsOnActiveMonitorQuietly` で静かに座標だけ戻す。
- VS Code の新規ウィンドウ捕捉では、専用 `user-data-dir` 有効時は `code.lock` PID に一致するウィンドウを採用する。専用 user-data を使わない設定では対象ワークスペース名がタイトルに出たウィンドウを補助的に採用する。単に「最初に見えた VS Code ウィンドウ」を採用すると、直前スロットの遅延表示を次スロットへ誤割り当てするため避ける。
- CLI (cmd.exe) は `UseShellExecute = true` で起動する。`WindowStyle = Normal` が適用されウィンドウが即時表示される。`UseShellExecute = false` + `CreateNoWindow = false` の組み合わせより表示が速い。
- VS Code など IDE の同一アプリタイプ内スロットは引き続きシーケンシャル起動。Workspace CLI も一括起動ではスロット単位で順次起動し、terminal 系プロセス名の競合を避ける。
- VS Code は起動プロセス ID と実際のウィンドウプロセス ID がずれる場合があるため、新規 VS Code ウィンドウの HWND を優先して捕捉する。
- VS Code が低速端末で新規 HWND 捕捉前に既存スロット用ウィンドウとして残った場合は、専用 `user-data-dir` の `code.lock` に記録された PID と VS Code のトップレベル HWND を照合して再接続する。これにより、ウィンドウ自体は開いているがスロットが赤表示のままになる状態を次回起動・再起動時にも回復する。
- 起動後の遅延再配置は 30 秒まで確認するが、`WindowArranger.NeedsArrange` で現在位置が期待 2x2 配置から大きく外れている場合だけ静かに再配置する。高速端末で既に正しい位置にあるウィンドウは触らない。
- ディスプレイ切替（`ToggleMonitorButton_Click`）と非表示からの復帰（`ToggleVisibilityButton_Click`）も単発の `ArrangeSlotsOnActiveMonitor` ではなく `ArrangeSlotsOnActiveMonitorWithSettlingAsync` を使う。DPI/解像度が異なるディスプレイへ移すと、移動直後に対象ウィンドウへ `WM_DPICHANGED` が届いてこちらの指定サイズを上書きするため、遅延付きの `NeedsArrange` 補正で移動先の作業領域に合わせて再補正する。
- 移動直後の「小さく出てから直る」フラッシュを抑えるため、即時補正 `ImmediateArrangeSettleDelays` は前倒しの密な間隔（40/110/220/420/720ms）にする。`WM_DPICHANGED` のサイズ上書きはクロスプロセスで非同期に届くため、単発の同期再適用では防げない。短い間隔で `NeedsArrange` を見て静かに補正することで、上書き後のずれを素早く戻す。
- 2x2 配置はウィンドウの不可視リサイズ枠（`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` と `GetWindowRect` の差）を打ち消して配置する。`WindowArranger.BuildPlacements` のセルは可視枠の目標矩形とし、`CompensateForFrame` で各辺の不可視枠ぶん外側へ広げて `SetWindowPos` する。これをしないと上端は不可視枠 0、左右下は約 7px(DPI で増減) のぶん見た目の隙間が偏る。`NeedsArrange` の現在値比較も可視枠（`TryGetVisibleBounds`）で行う。
- セル計算（`BuildPlacements`）は `gap` を内側の隙間と外周マージンの両方に使う（縦横とも 3 枠ぶん）。不可視枠を補正済みなので `gap` がそのまま見た目の均等な隙間になる。隙間を詰めるときは `gap` を下げる。既定は `6`（密着させず均等に詰める値）。

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
- アニメを出さない配置: フォーカスイン随伴の背面整列（`ArrangeSlotsExceptOnActiveMonitor`）と settling 補正（`ArrangeSlotsOnActiveMonitorQuietly`）は `animateRestore=false` で、復元が必要なウィンドウにだけ `DWMWA_TRANSITIONS_FORCEDISABLED` を対で適用し、`SW_SHOWNOACTIVATE` で非アクティブのまま通常サイズへ戻す。`SW_RESTORE` は非表示アニメを止めてもウィンドウをアクティブ化して z-order を変えるため、旧フォーカスが新フォーカスの前へ一瞬割り込む。A フォーカス中に B を押す即切替は、B を通常Z順の先頭へ非アクティブで準備し、`SW_MAXIMIZE` 開始後にフォアグラウンドを渡す。先にアクティブ化すると通常サイズのElectronサーフェスが白く再描画される。A の解除は遅延後に B の背面で非アクティブ・アニメ無しで済ませる。ディスプレイまたぎのフォーカス移動（`EnsureWindowOnMonitor`〜最大化）も遷移アニメを止める。
- フォーカスイン時の透け対策: 他スロットの背面送り（`SendOtherSlotsToBackOnSameDisplay`）は最大化アニメ完了後（`FocusSwitchArrangeDelay` 経過後）に行う。アニメ開始と同時に HWND_BOTTOM へ送ると、最大化が画面を覆い切るまでタイルの位置に管理外ウィンドウ（ブラウザ等）が透けて見える。
- フォーカス切替の背景フラッシュ対策（2026-07-05/07/11 追加調整）: `MainWindow.PrepareFocusTransitionBackdrop` は同じ面に旧フォーカスがある間、その旧1面と他タイルの z-order を動かさず、旧1面を全面の覆いとして維持する。新フォーカスだけを `BringToFrontWithoutTopmost` で非アクティブのまま旧1面の上へ準備し、`FocusMaximizedOnMonitorAsync` は `SW_MAXIMIZE` の反映後に `SetForegroundWindow` する。4 面へ戻すときは先に非フォーカスを静かにセルへ整えて背後を埋め、フォーカス解除後のレイヤー再適用を `FocusSwitchArrangeDelay` 後まで遅らせる。後片付け中に `HWND_BOTTOM`、`SW_RESTORE`、フォーカス対象の反復前面化を使うと、旧窓と新窓の双方で白背景・再描画・z-order の跳ねが発生するため禁止する。
- 2026-07-23 のズーム面白化対策: `WindowArranger` の `ShowWindow` はハング防止のため実体が `ShowWindowAsync` であり、呼び出しから戻っただけでは `SW_MAXIMIZE` が対象スレッドで処理済みとは限らない。直後に `SetForegroundWindow` すると通常サイズの Electron を先にアクティブ化し、白いサーフェスがDWMの拡大アニメーション素材になる。初回フォーカスは `FocusMaximizedOnMonitorAsync` で最大化要求後に `IsZoomed` を8ms間隔・最大160msで確認し、反映後に一度だけ前景化する。タイムアウトを持たせ、応答しない管理窓でパネルを停止させない。
- 2026-07-23 の即時切替補強: A の1面から B の1面へ切り替えるとき、`SW_SHOWNOACTIVATE` でAを復元しても、復元要求より前にZ順が確定していないとAの縮小がBより前へ一瞬出る環境がある。Bの最大化完了待ち後、`ArrangeExcept` へBのハンドルを `keepAboveHandle` として渡し、旧AをBの直後へ相対配置してからアニメ無しで4面セルへ戻す。`SetWindowPos(SWP_ASYNCWINDOWPOS)` と既存の `ShowWindowAsync` を同じ対象スレッドの非同期キューへ順番に積み、復元中もBを覆いとして維持する。DWMクロークは管理スロットの下地を消して白背景を露出させるため、フォーカス切替では使わない。
- 不可視枠（DWM 拡張フレームと GetWindowRect の差）は「通常状態」のときにだけ計測し、ハンドルごとにキャッシュする（`GetFrameInsetCached`）。最大化中は枠のはみ出し方が異なり、最小化中は座標が無効なため、そのまま測ると 4 面セルより大きい/ずれた配置になる。復元前の事前補正（rcNormalPosition）はキャッシュ値、最終配置（SetWindowPos）は復元後の実測で行う。
- パネル UI の描画は GPU 既定（`RenderMode.SoftwareOnly` 強制は撤去）。特定環境で描画乱れが出る場合のみ SoftwareOnly へ戻す。
- 1 面フォーカス中の z-order は `EnsureFocusedSlotAboveTiles` に集約し、常に `パネル本体 > 1 面フォーカス > 同面の 4 面スロット` とする。パネル本体だけを topmost 帯に置き、1 面と 4 面は通常帯のまま扱う。最大化開始直後に1面を topmost 帯へ移すと DWM のズームアニメーションが乱れ、白いちらつきや表示のごちゃつきが発生するため禁止する。アニメーション完了後に各4面を `SetWindowPos(tile, focusHandle, ...)` で1面の直後へ相対配置する。4 面は `HWND_BOTTOM` へ送らず、タイル同士の画面の覆いを保つ。
- 2026-07-22 の回帰修正: フォーカス中の `ArrangeCore` で座標・サイズ変更と Z 順変更を同じ `SetWindowPos` 要求へ統合してはならない。Electron / VS Code のサーフェス再描画がズーム中に表へ出て白ちらつきが増える。配置は従来どおり `SWP_NOZORDER` で行い、`PrepareFocusTransitionBackdrop` による旧1面の覆いを維持する。
- 非同期配置の後勝ち対策は、遷移完了後の読み取り検査で行う。`WindowArranger.IsAbove` で同面の4面が1面より上にあるか確認し、実際に違反した窓だけを `PlaceDirectlyBehind` で非アクティブのまま戻す。正常時は `SetWindowPos` を呼ばず、短い遅延検査と周期更新の両方で `パネル本体 > 1面 > 4面` を回復する。フォーカス窓の反復前面化や topmost 化は行わない。
- パネル本体の終了は確認ダイアログを通し、既定選択を「いいえ」にする。タイトルバーの X や Alt+F4 の誤操作を即終了にしない。OS のシャットダウン/サインアウト時だけ確認を省略し、終了要求・キャンセル・確定を `panel.log` に残す。
- パネル終了確認を含むユーザー向け確認 UI に WPF 標準 `MessageBox` を使わない。OS 標準の灰色ダイアログは本体の緑基調デザインと統一できないため、`MainWindow.xaml` 内の既存オーバーレイパターン（暗い半透明背景、`SurfaceBrush`、`AccentBrush`）で表示する。
- 新規VS Codeの起動前は、AIチャット等を表示する右側の `auxiliaryBarWidth` を基準画面幅の約28%（既定538px、420～620pxの範囲）まで必要時だけ広げる。既に広いユーザー設定は縮めない。`auxiliarySideBarWidth` が既に存在する形式では同じ下限を適用するが、未使用環境へ新しい値を勝手に追加しない。フォーカス直前やVS Code以外のウィンドウには適用しない。
- 新規VS Codeの起動時は、WinEventで新しいHWNDを捕捉した時点で `DWMWA_CLOAK` を設定し、2x2配置と160msの初回描画猶予が完了してから解除する。これによりElectronの白い初期サーフェスや中央の初期位置を見せない。再接続した既存ウィンドウはクロークしない。配置処理が失敗しても12秒後に必ず解除するフェイルセーフを持つ。右側 `auxiliaryBarWidth` の拡張も新規ウィンドウ起動前に済ませ、フォーカス直前に `storage.json` を変更して4面を再描画させない。
