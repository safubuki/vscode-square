# Turtle AI Code Quartet Hub

Turtle AI Code Quartet Hub は、4つの開発ワークスペースを A-D のスロットとしてまとめて起動し、画面上へ 2x2 に整列表示する Windows 向けランチャーアプリです。

VS Code だけを4面で開くためのツールではなく、スロットごとに VS Code / Google Antigravity / Codex CLI / GitHub Copilot CLI / Gemini CLI / Grok CLI / Claude CLI を選び、同じワークスペースを IDE と CLI のどちらでもすばやく開き直せることを重視しています。複数案件、複数AIエージェント、複数ターミナルを行き来する作業を、ひとつの小さな操作パネルに寄せるためのアプリです。

## 特徴

- **4面ワークスペース起動**: `Launch Quartet（一括起動）` で A-D の各スロットに選択済みアプリを起動し、2x2 に配置します。
- **IDE / CLI の切り替え**: 各スロットで VS Code / Antigravity と、Codex / Copilot / Gemini / Grok / Claude CLI を選択できます。
- **同じ位置でアプリを差し替え**: 起動中スロットで別の IDE / CLI を押すと、現在のウィンドウを閉じて同じ象限へ開き直します。
- **ワークスペースを覚える**: スロットのタイトル、ワークスペースパス、選択アプリ、控え Quartet を保存します。
- **CLI をワークスペース直下で起動**: CLI は対象フォルダをカレントディレクトリにした terminal として開きます。
- **Codex / ChatGPT / Claude Windows アプリも起動**: CLI とは別に、Windows アプリ版を補助ボタンから開けます。
- **フォーカス表示と4面表示**: スロットボタンで1面フォーカス表示と4面表示を切り替えます。
- **縮小モード**: 小さな操作バーとして常駐し、A-D スロット操作と Windows 補助アプリ起動をすぐ使えます。
- **タスクバー連携**: Jump List からスロット切替、表示モード切替、前面/背面操作を実行できます。

## 必要環境

- Windows 10 / Windows 11
- .NET 10 SDK
- VS Code
- VS Code の `code` コマンド

任意で使用するツール:

- Google Antigravity
- Codex CLI
- GitHub Copilot CLI
- Gemini CLI
- Grok CLI
- Claude CLI / Claude Code
- Codex / ChatGPT / Claude の Windows アプリ版

`code` コマンドが使えない場合は、設定ファイルの `codeCommand` に `Code.exe` のパスを指定してください。

## .NET 環境構築

PowerShell で .NET 10 SDK を導入します。Windows 10 / Windows 11 のどちらでも同じ手順です。

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements
```

導入後に新しい PowerShell または VS Code ターミナルを開き、SDK が見えていることを確認してください。

```powershell
dotnet --list-sdks
dotnet --info
```

`dotnet` コマンドが見つからない場合は、端末を開き直して PATH を再読み込みしてください。

## 起動方法

開発中は、実行中の本体が既定の `bin\Debug` をロックしていても通る `Build-Panel.ps1` を使うのがおすすめです。

```powershell
.\scripts\Build-Panel.ps1
```

ビルドしてそのまま起動する場合:

```powershell
.\scripts\Build-Panel.ps1 -Run
```

通常ビルド:

```powershell
dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj
```

通常の開発実行:

```powershell
dotnet run --project .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj
```

`dotnet build` と `dotnet run` は既定の `src\TurtleAIQuartetHub.Panel\bin\Debug\net10.0-windows` を使うため、その場所の exe が起動中だとロックで失敗することがあります。反復確認では `Build-Panel.ps1` か `--artifacts-path` を使ってください。

## 基本操作

1. A-D の各スロットにワークスペースを登録します。
2. スロット内の `IDE` または `CLI` から起動したいアプリを選びます。
3. `Launch Quartet（一括起動）` を押すと、未起動スロットがまとめて開きます。
4. 起動中スロットの別アプリボタンを押すと、同じ位置でアプリを切り替えます。
5. スロットボタンを押すと、1面フォーカス表示と4面表示を切り替えます。
6. 右上のゴミ箱アイコンで、保存済みのスロット情報を確認付きで削除できます。

## CLI インストール例

アプリ内の `?` ヘルプにも同じ内容を表示します。IDE と Windows アプリは各製品の公式サイトを参照してください。

Codex CLI（Windows PowerShell・推奨）:

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://chatgpt.com/codex/install.ps1 | iex"
```

Codex CLI（npm）:

```powershell
npm install -g @openai/codex
```

GitHub Copilot CLI（Windows・推奨）:

```powershell
winget install GitHub.Copilot
```

GitHub Copilot CLI（npm）:

```powershell
npm install -g @github/copilot
```

Gemini CLI:

```powershell
npm install -g @google/gemini-cli
```

Claude Code（PowerShell・推奨）:

```powershell
irm https://claude.ai/install.ps1 | iex
```

Claude Code（CMD）:

```cmd
curl -fsSL https://claude.ai/install.cmd -o install.cmd && install.cmd && del install.cmd
```

Claude Code（npm）:

```powershell
npm install -g @anthropic-ai/claude-code
```

Grok Build CLI（PowerShell）:

```powershell
irm https://x.ai/cli/install.ps1 | iex
```

Grok Build CLI（Git Bash／WSL）:

```bash
curl -fsSL https://x.ai/cli/install.sh | bash
```

自律実行向けの起動オプション例:

```powershell
codex --ask-for-approval never --sandbox workspace-write
copilot --allow-all
gemini --approval-mode=yolo
claude --permission-mode bypassPermissions
```

これらは承認確認を減らす、または権限を広げるための例です。信頼できるワークスペースでのみ使ってください。

## 設定

ユーザー設定は `%LOCALAPPDATA%` 側に置くのがおすすめです。

```powershell
$configDir = Join-Path $env:LOCALAPPDATA 'TurtleAIQuartetHub\config'
New-Item -ItemType Directory -Force $configDir
Copy-Item .\config\turtle-ai-quartet-hub.example.json (Join-Path $configDir 'turtle-ai-quartet-hub.json')
```

設定ファイルは次の順で読み込まれます。

1. `%LOCALAPPDATA%\TurtleAIQuartetHub\config\turtle-ai-quartet-hub.json`
2. `config\turtle-ai-quartet-hub.json`
3. `config\turtle-ai-quartet-hub.example.json`
4. アプリ内の既定値

主な設定項目:

- `codeCommand`: VS Code の起動コマンドまたは `Code.exe` のパス
- `launchTimeoutSeconds`: VS Code / Antigravity / CLI 起動待ち時間
- `remoteReconnectTimeoutSeconds`: SSH / Remote 接続の再接続待ち時間
- `statusRefreshIntervalMilliseconds`: 管理中ウィンドウ状態とワークスペース表示の更新間隔
- `useDedicatedUserDataDirs`: スロット別に VS Code user-data-dir を切るか。既定は `false`（標準プロファイル共有）。`true` にするとウィンドウ識別は容易になるが、キャッシュがスロット数だけ増える
- `inheritMainUserState`: 専用 user-data-dir 利用時に、通常 VS Code の設定やスニペットをスロットへ引き継ぐか。`globalStorage` や Chromium キャッシュはコピーしない
- `defaultWorkspaceApplicationId`: スロットの既定アプリ。未設定時は `vscode`
- `applications`: VS Code、Antigravity、Codex CLI、GitHub Copilot CLI、Gemini CLI、Claude CLI、Codex / ChatGPT / Claude Windows アプリなどの起動定義と検出候補
- `slots[].applicationId`: スロットごとの起動対象アプリ

`applications[].command` には、実行ファイルのフルパスまたはコマンド名を指定できます。未指定または検出できない場合は、PATH、App Paths、スタートメニュー、WindowsApps、一般的なインストール先から検出します。CLI については、npm / pnpm / Volta の shim 置き場と `~\.local\bin` も探索します。

実行時データは `%LOCALAPPDATA%\TurtleAIQuartetHub\` に保存されます。

## データとプライバシー

- スロット、控え Quartet、ワークスペース履歴、VS Code スロット別 user-data-dir はローカルに保存されます。
- アプリ独自の利用状況送信は行いません。

## 確認用コマンド

Store 公開準備の確認:

```powershell
.\scripts\Test-StoreReadiness.ps1
```

ローカル確認用 MSIX の生成:

```powershell
.\scripts\New-LocalMsixPackage.ps1
```

## 配布ビルド

自己完結型の win-x64 exe を作る場合:

```powershell
dotnet publish .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\turtle-ai-quartet-hub
```

配布物には `LICENSE.txt` も同梱してください。

## 関連ドキュメント

- [PRIVACY.md](PRIVACY.md): プライバシーポリシー草案
- [SUPPORT.md](SUPPORT.md): サポート案内草案
- [docs/multi-application-launcher-spec.md](docs/multi-application-launcher-spec.md): 複数アプリケーション起動対応 仕様書
- [docs/store-release-todo.md](docs/store-release-todo.md): Store 公開 TODO / 現状把握（ソフト面・リリース面の進捗一覧）
- [docs/store-readiness.md](docs/store-readiness.md): Microsoft Store 公開前チェック
- [docs/store-listing-draft.md](docs/store-listing-draft.md): Store 掲載文案
- [docs/msix-packaging-guide.md](docs/msix-packaging-guide.md): MSIX パッケージング手順
- [docs/release-notes-draft.md](docs/release-notes-draft.md): リリースノート草案
- [assets/store/README.md](assets/store/README.md): Store 画像素材チェック

## ライセンス

GNU General Public License v3.0 です。詳細は [LICENSE.txt](LICENSE.txt) を参照してください。
