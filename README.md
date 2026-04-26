# Turtle AI Code Quartet Hub

4つの VS Code ウィンドウを A-D のスロットとして起動し、2x2 に並べる Windows 向け WPF パネルです。
AI コーディング作業中でも、どの VS Code が実行中・完了・確認待ちなのかを枠の色で見分けやすくします。

## できること

- `Launch Quartet` で4つの VS Code を起動し、画面に2x2で配置
- 縮小モードで小さな操作バーとして常時表示
- VS Code 外周のネオン風フレームで AI 状態を表示
- スロット A-D のタイトル、ワークスペース、控え Quartet を保存
- `ディスプレイ移動` で4面表示を別ディスプレイへまとめて移動
- タスクバー右クリックからスロット切替、表示モード切替、前面/背面操作を実行
- スロット別の VS Code user-data-dir で最近使ったワークスペースを分離

## 必要環境

- Windows
- VS Code
- .NET 10 SDK
- VS Code の `code` コマンド

`code` コマンドが使えない場合は、設定ファイルの `codeCommand` に `Code.exe` のパスを指定してください。

## 起動方法

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
- `launchTimeoutSeconds`: VS Code 起動待ち時間
- `remoteReconnectTimeoutSeconds`: SSH / Remote 接続の再接続待ち時間
- `statusRefreshIntervalMilliseconds`: AI 状態の更新間隔
- `inheritMainUserState`: 通常 VS Code の設定やスニペットをスロットへ引き継ぐか

実行時データは `%LOCALAPPDATA%\TurtleAIQuartetHub\` に保存されます。

## AI 状態表示

AI 状態は、VS Code の見えている UI とスロット別の拡張ログからローカルで推定します。
外部送信は行いません。

詳しくは [docs/telemetry-notes.md](docs/telemetry-notes.md) を参照してください。

## 確認用コマンド

Store公開準備の確認:

```powershell
.\scripts\Test-StoreReadiness.ps1
```

ローカル確認用 MSIX の生成:

```powershell
.\scripts\New-LocalMsixPackage.ps1
```

AI 状態の簡易確認:

```powershell
dotnet run --project .\tools\AiStatusSmoke\AiStatusSmoke.csproj -- --json
```

対象スロットへ入力して状態遷移を見る:

```powershell
.\scripts\Invoke-AiStatusSmoke.ps1 -Slot A -Prompt "status smoke test"
```

## 配布ビルド

自己完結型の win-x64 exe を作る場合:

```powershell
dotnet publish .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\turtle-ai-quartet-hub
```

配布物には `LICENSE.txt` も同梱してください。

## 関連ドキュメント

- [PRIVACY.md](PRIVACY.md): プライバシーポリシー草案
- [docs/store-readiness.md](docs/store-readiness.md): Microsoft Store 公開前チェック
- [docs/store-listing-draft.md](docs/store-listing-draft.md): Store 掲載文案
- [docs/msix-packaging-guide.md](docs/msix-packaging-guide.md): MSIX パッケージング手順
- [docs/release-notes-draft.md](docs/release-notes-draft.md): リリースノート草案
- [SUPPORT.md](SUPPORT.md): サポート案内草案
- [assets/store/README.md](assets/store/README.md): Store 画像素材チェック
- [docs/telemetry-notes.md](docs/telemetry-notes.md): AI 状態検出とローカル情報の扱い

## ライセンス

GNU General Public License v3.0 です。詳細は [LICENSE.txt](LICENSE.txt) を参照してください。
