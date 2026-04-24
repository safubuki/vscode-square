# Turtle AI Code Quartet Hub 実装計画書

作成日: 2026-04-14

## 1. 目的

Windowsデスクトップ上で複数のVS Codeウィンドウを扱いやすくする小型パネルアプリを作る。

主な目的は次の通り。

- ワンクリックでVS Codeを4つ起動する。
- 4つのVS Codeウィンドウを画面上に2x2のsquare配置で並べる。
- 各VS Codeウィンドウの状態を小さなパネルに表示する。
- パネル上の項目をクリックすると、対応するVS Codeウィンドウを前面化する。
- GitHub Copilot、Codex、その他AIエージェント系ツールが「実行中」「完了」「エラー」「確認待ちに見える」などの状態を把握しやすくする。

## 2. 結論

実現可能。ただし、機能は2種類に分けて考える必要がある。

| 領域 | 実現性 | 方針 |
| --- | --- | --- |
| VS Codeの起動、整列、前面化、サイズ変更 | 高い | WindowsアプリからWin32 APIで制御する |
| VS Codeウィンドウの存在、タイトル、配置、アクティブ状態 | 高い | `EnumWindows`、`GetWindowText`、`SetForegroundWindow`、`SetWindowPos`などを使う |
| VS Code拡張のインストール・有効化状態 | 中〜高 | VS Code補助拡張からVS Code APIで報告する |
| Copilot/CodexなどAIツールの実行状態 | 中 | 公式のOpenTelemetry、ターミナル実行イベント、ログ、補助拡張を組み合わせる |
| 任意のAI拡張の内部状態を完全に把握する | 低〜中 | 拡張側が公開APIやテレメトリを提供しない場合は推測に留まる |

最初から「どんなAI拡張でも完全監視できる」と置くのは危険。最初のMVPでは次を目標にする。

- VS Codeの4ウィンドウ起動・整列・フォーカスを確実に実装する。
- Copilot Chat / Agent系についてはOpenTelemetryを第一候補にする。
- Codex CLIやターミナル上で動くAIツールは、VS CodeのTerminal Shell Integration経由で開始・終了・exit codeを取る。
- AI拡張の内部状態が取れない場合は「状態不明」「最後の活動時刻」「長時間無変化」などの表示に落とす。

## 3. 推奨技術スタック

### 3.1 Windowsパネルアプリ

推奨:

- C# / .NET 10
- WPF
- Win32 API P/Invoke
- System.Text.Json
- FileSystemWatcherまたはNamed Pipe
- 必要に応じてローカルHTTP/OTLP receiver

理由:

- 小型Windowsアプリ、常駐パネル、ウィンドウ操作との相性がよい。
- Win32 APIへのアクセスが安定している。
- WPFは小さな業務用・個人用ユーティリティを素早く作りやすい。
- .NET 10は2028-11-14までサポート予定のため、2026年時点の新規開発ではLTS候補として妥当。

代替:

- PowerShell: 試作には最速。ただし常駐UI、状態管理、DPI、配布性で不利。
- AutoHotkey v2: ウィンドウ操作は簡単。ただし依存ツールが増える。
- WinUI 3: モダンUIには向くが、今回の小型ユーティリティではWPFより初期コストが高い。
- Electron/TypeScript: VS Code周辺の開発者には馴染みがあるが、Windowsネイティブのウィンドウ制御はC#より回り道になる。

### 3.2 VS Code補助拡張

推奨:

- TypeScript
- VS Code Extension API
- ローカル状態ファイル、Named Pipe、またはlocalhost通信でパネルアプリへ状態を送る

役割:

- VS Codeウィンドウごとのheartbeatを送る。
- workspace folder、window state、拡張の存在・activation状態を送る。
- ターミナルコマンドの開始・終了イベントを監視する。
- 必要ならユーザー向けに「このウィンドウは確認待ち」などの明示的な状態を送るコマンドを提供する。

注意:

- 補助拡張だけでGitHub CopilotやCodex拡張の内部実行状態を直接読めるとは限らない。
- 他拡張が公開APIをexportしていない場合、状態取得は限定的になる。

## 4. 全体アーキテクチャ

```text
+----------------------------------+
| Turtle AI Code Quartet Hub Panel |
| WPF / .NET                       |
|                                  |
| - Launch 4 VS Code               |
| - Arrange 2x2 windows            |
| - Focus selected window          |
| - Show status cards              |
| - Receive telemetry              |
+----------------+-----------------+
                 |
                 | Win32 API
                 v
+--------------------------+
| Windows Desktop          |
| - HWND enumeration       |
| - monitor work area      |
| - window activation      |
+------------+-------------+
             |
             | status / heartbeat / telemetry
             v
+--------------------------+
| VS Code windows x4       |
|                          |
| - Copilot / Codex        |
| - optional helper ext    |
| - terminal integration   |
+--------------------------+
```

## 5. 状態監視の設計

### 5.1 状態レベル

状態監視は段階を分けて実装する。

| レベル | 内容 | 信頼度 |
| --- | --- | --- |
| Level 0 | VS Codeウィンドウが存在する、応答している、前面化できる | 高 |
| Level 1 | 対象拡張がインストール済み、有効化済み | 中〜高 |
| Level 2 | ターミナルで実行したAIコマンドの開始・終了・exit code | 中〜高 |
| Level 3 | Copilot Chat / AgentのOpenTelemetryから実行中・完了・エラーを推定 | 中 |
| Level 4 | 任意AI拡張の内部的な確認待ち・完了状態を直接検出 | 低〜中 |

MVPではLevel 0〜3を対象にする。Level 4は公開APIや安定したイベントが確認できた拡張だけ対応する。

### 5.2 GitHub Copilot / Agent系

VS CodeのCopilot ChatはOpenTelemetry出力に対応している。これを使い、パネルアプリ側でローカルOTLP receiverを立てる案を第一候補にする。

取得したい情報:

- セッション開始
- 実行中のspan
- span終了
- error status
- tool callまたはagent処理らしきイベント
- 最後のイベント時刻
- 一定時間イベントが途絶えた状態

表示例:

- `Idle`
- `Running`
- `Completed`
- `Error`
- `No recent events`
- `Needs attention?`  
  これは明示的なAPIがなければ推定表示に留める。

課題:

- OpenTelemetryの`session.id`はVS Codeウィンドウ単位の識別に使える可能性があるが、WindowsのHWNDとそのまま結びつくとは限らない。
- 4つのVS Codeウィンドウに対して、どのテレメトリがどのウィンドウ由来かを確実に対応付ける必要がある。

対応案:

1. パネルからVS Codeを起動する時にslot番号を割り当てる。
2. 可能ならslotごとの環境変数やOTLP endpointを付与して起動する。
3. それが既存VS Codeプロセスに吸収されて不安定な場合は、補助拡張またはslot別profile/user-data-dirで対応する。
4. MVPでは「パネルが起動した4ウィンドウだけを監視対象」に限定して精度を上げる。

### 5.3 Codex系

Codexには複数の利用形態があり得るため、最初に対象を分ける。

| 形態 | 監視方針 |
| --- | --- |
| VS CodeのCopilot経由のOpenAI Codex agent | Copilot ChatのOpenTelemetryとセッション一覧の挙動を検証する |
| VS Code拡張としてのCodex | 公開API、Output Channel、ログ、通知、コマンドを調査する |
| Codex CLIをVS Code terminalで実行 | Terminal Shell Integrationで開始・終了・exit code・出力断片を監視する |
| 外部Codexアプリ | プロセス、ログ、ファイル、APIがある場合だけアダプタ化する |

MVPでは「Terminal Shell Integrationで監視できるCLI実行」と「Copilot Chat OpenTelemetryで見えるagent実行」を優先する。

### 5.4 確認待ち状態

「確認待ち」は最も難しい。理由は、AI拡張のUI内部状態が標準APIとして外部に出ているとは限らないため。

MVPでの扱い:

- 明示的に検出できる場合だけ`Waiting for confirmation`と表示する。
- 明示的に検出できない場合は`Needs attention?`として推定表示にする。
- 推定条件は次のようにする。
  - 直近までagent実行中だった。
  - その後、一定時間イベントがない。
  - VS Codeウィンドウは生きている。
  - エラー終了や正常終了イベントがまだ来ていない。

## 6. ウィンドウ制御の設計

### 6.1 起動

基本コマンド:

```powershell
code --new-window <path>
```

4スロット構成例:

```json
{
  "slots": [
    { "name": "A", "path": "C:\\work\\repo-a" },
    { "name": "B", "path": "C:\\work\\repo-b" },
    { "name": "C", "path": "C:\\work\\repo-c" },
    { "name": "D", "path": "C:\\work\\repo-d" }
  ],
  "monitor": "primary",
  "gap": 8
}
```

起動時の流れ:

1. 起動前のVS Code top-level window一覧を取得する。
2. slotごとに`code --new-window`を実行する。
3. 一定時間、VS Code window一覧をポーリングする。
4. 新規に増えたwindowをslotに紐づける。
5. 2x2に配置する。

### 6.2 配置

対象モニタのwork areaを取得し、タスクバーを避けた矩形を使う。

配置:

- slot A: 左上
- slot B: 右上
- slot C: 左下
- slot D: 右下

Win32 API候補:

- `EnumWindows`
- `GetWindowText`
- `GetWindowThreadProcessId`
- `IsWindowVisible`
- `GetWindowPlacement`
- `SetWindowPos`
- `ShowWindow`
- `SetForegroundWindow`
- `MonitorFromWindow`
- `GetMonitorInfo`

考慮点:

- DPI scaling
- 複数モニタ
- タスクバー位置
- 既存VS Codeウィンドウとの混在
- 最小化状態の復帰
- VS Code起動直後にウィンドウタイトルが変化するタイミング

## 7. パネルUI

初期UIは小さく保つ。

表示要素:

- `Launch 4`
- `Arrange`
- `Focus All`
- slot A〜Dのステータス
- 各slotの状態ラベル
- 最終イベント時刻
- AI状態バッジ

状態表示例:

| Slot | Window | AI | Last Event | Action |
| --- | --- | --- | --- | --- |
| A | Ready | Running | 12:30:15 | Focus |
| B | Ready | Needs attention? | 12:28:02 | Focus |
| C | Ready | Completed | 12:31:44 | Focus |
| D | Missing | Unknown | - | Launch |

## 8. 実装フェーズ

### Phase 0: 検証

- `code --new-window`で4ウィンドウを作れるか確認する。
- 起動前後のHWND差分でslot紐づけできるか確認する。
- `SetWindowPos`で2x2配置できるか確認する。
- Copilot Chat OpenTelemetryをローカルに出せるか確認する。
- OpenTelemetryのイベントから「実行中」「完了」「エラー」をどこまで読めるか確認する。
- Codexの実利用形態を確認する。

完了条件:

- 4ウィンドウ配置が安定して動く。
- AI状態について、取れる情報と取れない情報が明確になる。

### Phase 1: WindowsパネルMVP

- WPFアプリを作る。
- 設定JSONを読む。
- VS Codeを4つ起動する。
- HWNDをslotに紐づける。
- `Arrange`ボタンで2x2配置する。
- slotクリックで対象ウィンドウを前面化する。
- ウィンドウ消失時に`Missing`表示へ変える。

完了条件:

- AI状態なしでも、VS Code 4分割ランチャーとして実用できる。

### Phase 2: 補助拡張MVP

- TypeScriptのVS Code補助拡張を作る。
- workspace、拡張有効化状態、terminal eventsを状態ファイルへ書く。
- パネルアプリが状態ファイルを監視する。
- `GitHub.copilot`、`GitHub.copilot-chat`など対象拡張の存在・activation状態を表示する。

完了条件:

- 4つの各VS Codeウィンドウからheartbeatが見える。
- 対象拡張が存在するか、activeかが表示される。

### Phase 3: AI状態監視

- パネルアプリにOTLP receiverを追加する。
- Copilot ChatのOpenTelemetry出力を受ける。
- session/span/eventを内部状態へ変換する。
- `Running`、`Completed`、`Error`、`Needs attention?`を表示する。
- terminal shell executionの開始・終了・exit codeを補助拡張から受ける。

完了条件:

- 少なくともCopilot Chat agentまたはCodex CLIのどちらかで、実行開始・終了がパネルに表示される。

### Phase 4: 精度改善

- slotとtelemetry sessionの対応付けを安定化する。
- slot別OTLP endpoint、環境変数、profile、user-data-dirのどれが最も副作用が少ないか検証する。
- 確認待ち推定ロジックの閾値を設定化する。
- ログ出力、診断ビュー、手動状態変更を追加する。

完了条件:

- 通常利用で「完了しているのに気づかない」「確認待ちで止まっているのに放置する」を減らせる。

## 9. 推奨リポジトリ構成

```text
turtle-ai-quartet-hub/
  IMPLEMENTATION_PLAN.md
  src/
    TurtleAIQuartetHub.Panel/
      TurtleAIQuartetHub.Panel.csproj
      App.xaml
      MainWindow.xaml
      Services/
        VscodeLauncher.cs
        WindowEnumerator.cs
        WindowArranger.cs
        StatusStore.cs
        OtlpReceiver.cs
      Models/
        SlotConfig.cs
        WindowSlot.cs
        AiStatus.cs
    turtle-ai-quartet-hub-extension/
      package.json
      src/
        extension.ts
        statusReporter.ts
        terminalMonitor.ts
  config/
    turtle-ai-quartet-hub.example.json
  docs/
    telemetry-notes.md
```

## 10. リスクと対策

| リスク | 対策 |
| --- | --- |
| VS Code起動時に既存プロセスへ吸収され、slot別の環境変数が効かない | HWND差分、補助拡張、profile/user-data-dir案を検証する |
| Copilot/Codexの内部状態が公開APIで取れない | OpenTelemetry、terminal events、ログ、推定表示へ分解する |
| 確認待ちを誤判定する | `Needs attention?`として推定扱いにし、明示状態と区別する |
| 複数モニタ・DPIで配置がずれる | work areaとDPI aware設定を早期に検証する |
| 補助拡張導入が面倒 | Phase 1では補助拡張なしで価値を出す |
| user-data-dir分離でCopilot認証や拡張管理が面倒になる | 最後の手段にする。まず通常profileと補助拡張を試す |

## 11. 最初に作るべきもの

最初の実装対象はPhase 1にする。

理由:

- VS Codeの4ウィンドウ起動・整列・フォーカスは確実に価値がある。
- AI状態監視の前提になるslot管理を先に固められる。
- AI拡張の内部仕様に依存しないため、短期間で動くものを作れる。

Phase 1の最小タスク:

1. .NET WPFプロジェクトを作る。
2. `code`コマンドの存在確認を実装する。
3. 起動前後のVS Code HWND差分を取る。
4. 4つのslotにHWNDを割り当てる。
5. 2x2配置を実装する。
6. slotクリックで前面化する。
7. 設定JSONから起動パスを読めるようにする。

## 12. 参考資料

- VS Code Command Line Interface: https://code.visualstudio.com/docs/configure/command-line
- VS Code Extension Host: https://code.visualstudio.com/api/advanced-topics/extension-host
- VS Code API Reference: https://code.visualstudio.com/api/references/vscode-api
- VS Code Terminal Shell Integration: https://code.visualstudio.com/docs/terminal/shell-integration
- VS Code Copilot monitoring: https://code.visualstudio.com/docs/copilot/guides/monitoring-agents
- VS Code Agents overview: https://code.visualstudio.com/docs/copilot/agents/overview
- VS Code Third-party agents: https://code.visualstudio.com/docs/copilot/agents/third-party-agents
- Microsoft .NET lifecycle: https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core
