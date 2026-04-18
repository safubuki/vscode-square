# Turtle App Launch Quartet

Turtle App Launch Quartet は、4つの VS Code ウィンドウをスロット A-D として起動し、2x2 に配置する Windows 向けの小さな WPF パネルです。

## 機能

- `Launch Quartet` で未起動スロットの VS Code を起動し、4分割に配置
- パネル自体は常に最前面を維持し、管理対象の VS Code はその一段下で表示
- 各カード全体をクリックすると対象ウィンドウを最大化し、再度クリックで4分割へ戻す
- 下部バーの `最前面` で管理中の VS Code を一度だけ前面へ出し、その後は通常の Windows 表示順序に戻す
- 下部バーの `最背面` で管理中の VS Code 全体を背面へ送る
- 各カードのタイトルをその場で編集して保持
- 各カードのワークスペース名は、VS Code 側で開いているフォルダや workspace を数秒以内に反映
- 各カードの `裏へ` で現在のスロット設定を `裏保存 Quartet` に退避し、4件まで保持
- `裏保存 Quartet` は普段は折りたたみ、必要なときだけ開いて使う
- `裏保存 Quartet` から `A` `B` `C` `D` へ戻すと、対象スロットの設定と入れ替え
- 各 `裏保存 Quartet` カードは `D` の右側のゴミ箱から個別に削除でき、確認ダイアログで OK したときだけ空きスロットへ戻る
- 空きスロットへ `Launch Quartet` で新規起動した場合は、自動で重複しないタイトルを付ける
- `裏保存 Quartet` を開くとウィンドウの縦サイズも追従して広がり、閉じると元の高さへ戻る
- `裏保存 Quartet` から表へ戻したワークスペースは、その場で対象スロットの VS Code を起動して表示する
- 各カードの `閉じる` で対象 VS Code を閉じる
- `全て閉じる` で管理中の VS Code をまとめて閉じる
- `設定保存` / `設定読み込み` でカードタイトル、最後に確認できたワークスペース、ウィンドウ割り当て、裏保存 Quartet を保存・復元
- `ディスプレイ移動` で 4 つの VS Code を次のディスプレイへまとめて移動
- スロット別の VS Code user-data-dir を使い、スロットごとに最近開いたワークスペースを分けて管理
- 専用 user-data-dir を使う場合でも、通常使っている VS Code の設定、globalStorage、Electron 側の認証保存領域を起動前に同期し、可能な範囲でログイン状態を引き継ぐ
- SSH 接続などの remote workspace URI も保存し、次回の `Launch Quartet` で再オープンする

AI 状態は、VS Code ウィンドウの UI Automation とスロット別 user-data-dir の拡張ログから限定的に取得します。実行中は現在表示されている停止ボタンなどの可視 UI を優先して読み取り、完了は実行中表示が消えた直後または拡張ログの完了イベントから `AI 完了` を表示し続けます。完了表示は対象スロットのカードクリックや操作で `AI 待機中` に戻り、アプリ再起動や VS Code 再起動後は待機中から始まります。古い `作業中` ライブリージョンや過去ログを実行中として保持しません。

フォーカス表示と4分割への復帰では、VS Code のアニメーション中のちらつきを抑えるため、パネルの最前面復帰を約500msだけ遅らせています。

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

`code` が見つからない場合は、VS Code のコマンドパレットから `Shell Command: Install 'code' command in PATH` 相当の設定を有効にするか、`config\vscode-square.json` の `codeCommand` に VS Code の `Code.exe` パスを設定してください。`code.cmd` を指定していても、アプリは可能なら実体の `Code.exe` を優先して起動し、余計なコマンドプロンプトが残らないようにします。

## 開発環境での起動

ビルド:

```powershell
dotnet build .\src\VscodeSquare.Panel\VscodeSquare.Panel.csproj
```

開発実行:

```powershell
dotnet run --project .\src\VscodeSquare.Panel\VscodeSquare.Panel.csproj
```

ビルド済み exe を直接起動:

```powershell
.\src\VscodeSquare.Panel\bin\Debug\net10.0-windows\VscodeSquare.Panel.exe
```

## 設定

設定ファイルを作る場合:

```powershell
Copy-Item .\config\vscode-square.example.json .\config\vscode-square.json
```

アプリは次の順で設定を探します。

1. `config\vscode-square.json`
2. `config\vscode-square.example.json`
3. アプリ内の既定値

`config\vscode-square.json` は、スロット名、初期ワークスペースパス、起動タイムアウト、スロット別 user-data-dir の有無など、配布時にも固定したい設定を置く場所です。

`inheritMainUserState` を有効にすると、専用 user-data-dir を使う場合でも通常の VS Code の `User/globalStorage`、設定、スニペットに加えて `Local Storage`、`Network`、`Service Worker` などの認証関連ストレージも起動前に取り込みます。これにより GitHub / Microsoft などのログイン状態や拡張ごとの保存状態を、可能な範囲で毎回引き継ぎます。

スロットの `path` を空にすると、初回起動では VS Code のようこそ画面やフォルダ未選択状態になります。その後、VS Code でフォルダやワークスペースを開くと、パネル上のスロット名の下に現在のフォルダ名または workspace 名が数秒以内に反映されます。さらに `設定保存` または `閉じる` / `全て閉じる` を押すと、現在のウィンドウタイトルからそのワークスペースが確認できたスロットだけ `%LOCALAPPDATA%\VscodeSquare\slots.json` に保存されます。SSH 接続などの remote workspace は `vscode-remote://...` の URI として保存し、次回は VS Code CLI の `--folder-uri` または `--file-uri` で開き直します。ようこそ画面や no-folder 状態のスロットは保存対象にせず、次回 `Launch Quartet` でもようこそ画面のまま起動します。

複数ディスプレイがある場合は `ディスプレイ移動` で 4 分割の配置先を次のディスプレイへ順送りできます。2 枚なら 1 → 2 → 1、3 枚なら 1 → 2 → 3 → 1 のようにトグルします。

実行時状態の既定保存先:

```text
%LOCALAPPDATA%\VscodeSquare\
```

ここには、スロット別 user-data-dir、`slots.json`、裏保存 Quartet の状態、VS Code 拡張ログなど、PCごとの実行時データを保存します。

## デプロイ用 exe の作成

自己完結型の win-x64 exe を作る場合:

```powershell
dotnet publish .\src\VscodeSquare.Panel\VscodeSquare.Panel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\vscode-square
```

出力先:

```text
dist/
  vscode-square/
    VscodeSquare.Panel.exe
    config/
      vscode-square.example.json
```

配布時に固定設定を同梱する場合は、同じ階層に `config\vscode-square.json` を置きます。

GPL-3.0 で配布する場合は、出力物と一緒にルートの `LICENSE.txt` も同梱してください。

```powershell
Copy-Item .\config\vscode-square.example.json .\dist\vscode-square\config\vscode-square.json
```

軽量なフレームワーク依存版でよい場合は、実行先PCに .NET 10 Desktop Runtime が必要です。

```powershell
dotnet publish .\src\VscodeSquare.Panel\VscodeSquare.Panel.csproj -c Release -o .\dist\vscode-square-framework
```

## AI・タブ・拡張機能情報

この WPF パネル単体で安定して取れるのは、Win32 で見える VS Code ウィンドウのハンドル、タイトル、プロセス情報までです。

AI 状態は例外的に、VS Code ウィンドウを Windows UI Automation で走査しつつ、スロット別 user-data-dir に出力される拡張ログを読み取って推定します。実行中判定は現在見えている停止ボタンなどの可視 UI を優先し、完了判定は実行中表示が消えた直後の遷移または `openai.chatgpt` の `Codex.log` と `github.copilot-chat` の `GitHub Copilot Chat.log` の現在セッション内イベントを使います。完了表示はユーザーが対象スロットを操作するまで保持します。非表示の `作業中` ライブリージョンや継続的に流れる状態ログだけでは実行中にしません。VS Code API からの厳密な状態取得ではありません。

VS Code の開いているタブ、ワークスペース、インストール済み拡張機能、拡張機能の詳細状態を正確に取るには、VS Code 補助拡張を作り、VS Code API から取得した情報をローカルファイルなどへ書き出す構成が適しています。候補 API は `vscode.window.tabGroups.all`、`vscode.workspace.workspaceFolders`、`vscode.extensions.all` です。

## ライセンス

このプロジェクトは GNU General Public License v3.0 で公開します。詳細は `LICENSE.txt` を参照してください。
