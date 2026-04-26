# MSIX パッケージング手順

Turtle AI Code Quartet Hub を Microsoft Store へ出すための、リポジトリ側の MSIX 準備メモです。
Partner Center の値はアカウントごとに異なるため、ここでは固定せず、実際の予約後に Visual Studio 側で設定します。

## このワークスペースで準備済みの値

- アプリ表示名: `Turtle AI Code Quartet Hub`
- WPFプロジェクト: `src/TurtleAIQuartetHub.Panel/TurtleAIQuartetHub.Panel.csproj`
- ソリューション: `TurtleAIQuartetHub.sln`
- アプリケーションアイコン: `src/TurtleAIQuartetHub.Panel/app.ico`
- 同梱する設定例: `config/turtle-ai-quartet-hub.example.json`
- 同梱するライセンス: `LICENSE.txt`
- ユーザー設定の保存先: `%LOCALAPPDATA%\TurtleAIQuartetHub\config\turtle-ai-quartet-hub.json`
- 開発用 package identity: `TurtleAIQuartetHub.Dev`
- 開発用 publisher: `CN=TurtleAIQuartetHubDev`

## Visual Studio での推奨手順

1. Partner Center でアプリ名を予約する。
2. Visual Studio で `TurtleAIQuartetHub.sln` を開く。
3. 必要に応じて `src/TurtleAIQuartetHub.Package\TurtleAIQuartetHub.Package.wapproj` を既存プロジェクトとして追加する。
4. `Package.appxmanifest` に Partner Center の Identity / Publisher / Version を設定する。
5. `Package.appxmanifest` の表示名、説明、ロゴ、対象アーキテクチャを確認する。
6. `Publish > Create App Packages` で Microsoft Store 用パッケージを作成する。
7. Store申請用には `.msixupload` または `.appxupload` を生成する。
8. 生成したパッケージに Windows App Certification Kit を実行する。

## Package.appxmanifest で確認する項目

- `Identity Name`: Partner Center の予約名に合わせる。
- `Identity Publisher`: Partner Center の publisher 値に合わせる。
- `Identity Version`: `1.0.0.0` 形式で更新する。
- `DisplayName`: `Turtle AI Code Quartet Hub`
- `PublisherDisplayName`: 公開者名に置き換える。
- `Executable`: WPFアプリ本体を指していること。
- `EntryPoint`: デスクトップブリッジ / Full Trust アプリとして正しいこと。
- `Capabilities`: 不要な権限を宣言しないこと。

## パッケージに含めるもの

- WPFアプリ本体
- `config\turtle-ai-quartet-hub.example.json`
- `LICENSE.txt`
- アプリのアイコン・ロゴ類

ユーザーが編集する設定ファイルは、インストール先ではなく `%LOCALAPPDATA%` 側を使います。

## ローカル確認

通常ビルド:

```powershell
dotnet build .\TurtleAIQuartetHub.sln
```

公開準備チェック:

```powershell
.\scripts\Test-StoreReadiness.ps1
```

ローカル確認用 MSIX 生成:

```powershell
.\scripts\New-LocalMsixPackage.ps1
```

署名付きのローカル確認用 MSIX 生成:

```powershell
.\scripts\New-LocalMsixPackage.ps1 -Sign
```

既定ではMSIX内にPDBを含めません。シンボルを確認用に含める場合だけ `-IncludeSymbols` を指定してください。

自己完結版 publish まで確認する場合:

```powershell
.\scripts\Test-StoreReadiness.ps1 -Publish
```

Windows App Certification Kit の代表的な実行例:

```powershell
appcert.exe reset
appcert.exe test -appxpackagepath ".\path\to\package.msix" -reportoutputpath ".\TestResults\wack-report.xml"
```

`New-LocalMsixPackage.ps1 -Sign -RunWack` を使う場合、開発用MSIXとWACKレポートは次へ出力されます。

```text
dist\msix-local\TurtleAIQuartetHub.msix
dist\msix-local\wack-report.xml
```
