# Turtle AI Code Quartet Hub

Turtle AI Code Quartet Hub は、4つの VS Code ウィンドウをスロット A-D として起動し、2x2 に配置する Windows 向けの小さな WPF パネルです。

## 機能

- `Launch Quartet` で未起動スロットの VS Code を起動し、4分割に配置
- パネル自体は常に最前面を維持し、管理対象の VS Code はその一段下で表示
- `縮小` でパネルを低いバー状の最小表示へ切り替え、`標準` で通常表示へ戻せる
- 縮小表示ではパネル外周に常時点灯する緑のフレームを出し、黒基調の VS Code 上でも見失いにくくする
- 縮小表示では A-D の小さなスロット枠の点灯・点滅で `AI 実行中` `AI 完了` `AI 確認中` `待機中` を見分けられる
- 縮小表示やタスクバーの右クリックメニューから A-D を選ぶと、対象スロットのフォーカス切替とパネルの標準/縮小表示を連動できる
- タスクバーアイコンの右クリックでは、各スロットの簡易状態、表示モード切替、最前面/最背面操作を JumpList として呼び出せる
- 各 VS Code ウィンドウの外周に AI 状態連動の太い高輝度フレームを重ね、緑/青/黄/灰などで状態を遠目でも把握できる
- 各カード全体をクリックすると対象ウィンドウを最大化し、再度クリックで4分割へ戻す
- 下部バーの `最前面` で管理中の VS Code を一度だけ前面へ出し、その後は通常の Windows 表示順序に戻す
- 下部バーの `最背面` で管理中の VS Code 全体を背面へ送る
- 各カードのタイトルをその場で編集して保持
- 各カードのワークスペース名は、VS Code 側で開いているフォルダや workspace を数秒以内に反映
- 各カードの `控えへ` で現在のスロット設定を `控え Quartet` に退避し、3タブで12件まで保持
- `控え Quartet` は普段は折りたたみ、必要なときだけ開いて使う
- `控え Quartet` から `A` `B` `C` `D` へ戻すと、対象スロットの設定と入れ替え
- 各 `控え Quartet` カードは `D` の右側のゴミ箱から個別に削除でき、確認ダイアログで OK したときだけ空きスロットへ戻る
- 空きスロットへ `Launch Quartet` で新規起動した場合は、自動で重複しないタイトルを付ける
- `控え Quartet` を開くとウィンドウの縦サイズも追従して広がり、閉じると元の高さへ戻る
- `控え Quartet` から表へ戻したワークスペースは、その場で対象スロットの VS Code を起動して表示する
- 各カードの `閉じる` で対象 VS Code を閉じる
- `全て閉じる` で管理中の VS Code をまとめて閉じる
- `設定保存` / `設定読み込み` でカードタイトル、最後に確認できたワークスペース、ウィンドウ割り当て、控え Quartet を保存・復元
- `ディスプレイ移動` で 4 つの VS Code を次のディスプレイへまとめて移動
- スロット別の VS Code user-data-dir を使い、スロットごとに最近開いたワークスペースを分けて管理
- 専用 user-data-dir を使う場合でも、通常使っている VS Code の設定、globalStorage、スニペットなどの軽量な共有状態を起動前に同期する
- スロットごとに VS Code のサイドバー / セカンダリパネル幅を保持し、再フォーカスや再起動時にも極端に細らないよう復元する
- SSH 接続などの remote workspace URI も保存し、次回の `Launch Quartet` で短時間だけ再接続を試み、失敗時は空の VS Code ウィンドウへフォールバックする

AI 状態は、VS Code ウィンドウの UI Automation とスロット別 user-data-dir の拡張ログから限定的に取得します。実行中は現在表示されている `思考中` / `考え中` / 停止ボタンなどの可視 UI と、`Codex.log` の明示的な生成・確認要求を読み取ります。Codex 系は起動時の会話復元ログや単発のストリーム状態更新だけでは実行中にせず、UI Automation の一時的な読み落としや SSH 接続時の遅延だけで即座に完了へ落ちないよう短時間保持します。状態更新は既定 750ms 間隔で行い、各スロットを並列に確認するため、SSH 接続スロットの検出が重くても他スロットへ波及しにくくしています。完了表示は対象スロットのカードクリックや操作で `AI 待機中` に戻り、アプリ再起動や VS Code 再起動後は待機中から始まります。古い `作業中` ライブリージョンや過去ログを実行中として保持しません。

フォーカス表示や4分割復帰、前後移動では、VS Code 側の再レイアウトが落ち着く短い時間だけパネルの最前面復帰を遅らせ、過度な待ち時間を増やさずにちらつきを抑えています。

パネルは単一起動で動作し、2個目以降の起動要求やタスクバー JumpList の操作は既存インスタンスへ引き渡します。

低スペックPCでも起動しやすくするため、パネルは WPF の高負荷な影描画を使わず、起動時はソフトウェア描画で安定性を優先します。VS Code 起動前の user-data-dir 同期やウィンドウ検出も UI スレッドを塞がないようにバックグラウンドで処理します。

## 必要環境

- Windows
- VS Code
- .NET 10 SDK
- VS Code コマンドラインランチャー `code`

## 環境構築

.NET SDK を winget で入れる場合:

```powershell
winget install Microsoft.DotNet.SDK.10
```

インストール後、別の PowerShell を開き直して確認します。

```powershell
dotnet --version
```

VS Code の `code` コマンドも確認します。

```powershell
code --version
```

`code` が見つからない場合は、VS Code のコマンドパレットから `Shell Command: Install 'code' command in PATH` 相当の設定を有効にするか、`config\turtle-ai-quartet-hub.json` の `codeCommand` に VS Code の `Code.exe` パスを設定してください。`code.cmd` を指定していても、アプリは可能なら実体の `Code.exe` を優先して起動し、余計なコマンドプロンプトが残らないようにします。

## 開発環境での起動

ビルド:

```powershell
dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj
```

開発実行:

```powershell
dotnet run --project .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj
```

ビルド済み exe を直接起動:

```powershell
.\src\TurtleAIQuartetHub.Panel\bin\Debug\net10.0-windows\TurtleAIQuartetHub.exe
```

AI 状態のスモーク確認:

```powershell
dotnet run --project .\tools\AiStatusSmoke\AiStatusSmoke.csproj -- --json
```

対象スロットへ実際に入力して状態遷移を追う:

```powershell
.\scripts\Invoke-AiStatusSmoke.ps1 -Slot A -Prompt "status smoke test"
```

## 設定

設定ファイルを作る場合:

```powershell
Copy-Item .\config\turtle-ai-quartet-hub.example.json .\config\turtle-ai-quartet-hub.json
```

アプリは次の順で設定を探します。

1. `config\turtle-ai-quartet-hub.json`
2. `config\turtle-ai-quartet-hub.example.json`
3. アプリ内の既定値

`config\turtle-ai-quartet-hub.json` は、スロット名、初期ワークスペースパス、起動タイムアウト、remote 再接続の待ち時間、AI 状態更新間隔、スロット別 user-data-dir の有無など、配布時にも固定したい設定を置く場所です。`launchTimeoutSeconds` の既定値は 40 秒、`remoteReconnectTimeoutSeconds` の既定値は 15 秒、`statusRefreshIntervalMilliseconds` の既定値は 750ms です。

`inheritMainUserState` を有効にすると、専用 user-data-dir を使う場合でも通常の VS Code の `User/globalStorage`、設定、スニペット、`prompts` を起動前に取り込みます。低スペック PC での起動遅延を避けるため、`Local Storage`、`Session Storage`、`WebStorage`、`Network`、`Service Worker`、`Partitions` などの Chromium 系ストレージはコピーしません。

スロットの `path` を空にすると、初回起動では VS Code のようこそ画面やフォルダ未選択状態になります。その後、VS Code でフォルダやワークスペースを開くと、パネル上のスロット名の下に現在のフォルダ名または workspace 名が数秒以内に反映されます。SSH Remote のワークスペースは `ssh@192.168.0.66-codex-test` のように接続先とワークスペース名が分かる短縮表記で表示します。さらに `設定保存` または `閉じる` / `全て閉じる` を押すと、現在のウィンドウタイトルからそのワークスペースが確認できたスロットだけ `%LOCALAPPDATA%\TurtleAIQuartetHub\slots.json` に保存されます。SSH 接続などの remote workspace は `vscode-remote://...` の URI として保存し、次回は VS Code CLI の `--folder-uri` または `--file-uri` で開き直します。remote 再接続は `remoteReconnectTimeoutSeconds` の範囲でだけ待ち、ワークスペースが見えないままならそのスロットは空ウィンドウへ切り替えるため、接続不能な相手で残りスロットの起動が止まり続けないようにしています。ようこそ画面や no-folder 状態のスロットは保存対象にせず、次回 `Launch Quartet` でもようこそ画面のまま起動します。

複数ディスプレイがある場合は `ディスプレイ移動` で 4 分割の配置先を次のディスプレイへ順送りできます。2 枚なら 1 → 2 → 1、3 枚なら 1 → 2 → 3 → 1 のようにトグルします。

実行時状態の既定保存先:

```text
%LOCALAPPDATA%\TurtleAIQuartetHub\
```

ここには、スロット別 user-data-dir、`slots.json`、控え Quartet の状態、VS Code 拡張ログなど、PCごとの実行時データを保存します。

## デプロイ用 exe の作成

自己完結型の win-x64 exe を作る場合:

```powershell
dotnet publish .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\turtle-ai-quartet-hub
```

出力先:

```text
dist/
  turtle-ai-quartet-hub/
    TurtleAIQuartetHub.exe
    config/
      turtle-ai-quartet-hub.example.json
```

配布時に固定設定を同梱する場合は、同じ階層に `config\turtle-ai-quartet-hub.json` を置きます。

GPL-3.0 で配布する場合は、出力物と一緒にルートの `LICENSE.txt` も同梱してください。

```powershell
Copy-Item .\config\turtle-ai-quartet-hub.example.json .\dist\turtle-ai-quartet-hub\config\turtle-ai-quartet-hub.json
```

軽量なフレームワーク依存版でよい場合は、実行先PCに .NET 10 Desktop Runtime が必要です。

```powershell
dotnet publish .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -c Release -o .\dist\turtle-ai-quartet-hub-framework
```

## AI・タブ・拡張機能情報

この WPF パネル単体で安定して取れるのは、Win32 で見える VS Code ウィンドウのハンドル、タイトル、プロセス情報までです。

AI 状態は例外的に、VS Code ウィンドウを Windows UI Automation で走査しつつ、スロット別 user-data-dir に出力される拡張ログを読み取って推定します。実行中判定は現在見えている `思考中` / `考え中` / 停止ボタンなどの可視 UI と、`openai.chatgpt` の `Codex.log`、`github.copilot-chat` の `GitHub Copilot Chat.log` の現在セッション内イベントを使います。Codex では起動時の `Conversation created` や単発の `thread-stream-state-changed` だけを開始根拠にせず、明示的な生成ログまたは直前の実行中状態の継続としてだけ扱います。`commandExecution/requestApproval` は開始イベントが取れなくても `AI 確認中` の根拠に使います。UI Automation の一時的な読み落としでは直前の実行中状態を短時間保持し、Codex のストリーム停止判定は SSH 接続時の遅延を見込んで十数秒の猶予を置いて完了へ切り替えます。完了表示はユーザーが対象スロットを操作するまで保持します。非表示の `作業中` ライブリージョンや継続的に流れる状態ログだけでは実行中にしません。VS Code API からの厳密な状態取得ではありません。

VS Code の開いているタブ、ワークスペース、インストール済み拡張機能、拡張機能の詳細状態を正確に取るには、VS Code 補助拡張を作り、VS Code API から取得した情報をローカルファイルなどへ書き出す構成が適しています。候補 API は `vscode.window.tabGroups.all`、`vscode.workspace.workspaceFolders`、`vscode.extensions.all` です。

## ライセンス

このプロジェクトは GNU General Public License v3.0 で公開します。詳細は `LICENSE.txt` を参照してください。
