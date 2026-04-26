# Microsoft Store 公開準備

この文書は、Turtle AI Code Quartet Hub を Microsoft Store へ公開する前に、このリポジトリ内で確認・改善する内容を管理するためのチェックリストです。

## 配布方針

Microsoft は Windows アプリのインストール、更新、Store 配布の形式として MSIX を案内しています。WPF デスクトップアプリは、Visual Studio の Windows Application Packaging Project または MSIX パッケージング手順で Store 申請用パッケージを作成できます。

参照先:

- Publish your first Windows app: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/publish-first-app
- Packaging overview: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/packaging/
- Package desktop apps in Visual Studio: https://learn.microsoft.com/en-us/windows/msix/desktop/vs-package-overview
- Microsoft Store Policies: https://learn.microsoft.com/en-us/windows/apps/publish/store-policies

## このリポジトリで対応済み

- アプリ表示名とアセンブリメタデータを `Turtle AI Code Quartet Hub` に統一。
- Windows アプリケーションマニフェストで `asInvoker` を明示し、管理者権限を要求しない構成にした。
- 実行時状態の保存先を `%LOCALAPPDATA%\TurtleAIQuartetHub\` に統一。
- `%LOCALAPPDATA%\TurtleAIQuartetHub\config\turtle-ai-quartet-hub.json` からユーザー設定を優先読み込みできるようにした。Store / MSIX 配布でアプリ配置先が読み取り専用になっても、ユーザー設定を安全に扱える。
- `TurtleAIQuartetHub.sln` を追加し、WPF本体と AI 状態確認ツールをソリューションから開けるようにした。
- ビルド / publish 出力へ `LICENSE.txt` を同梱するようにした。
- [msix-packaging-guide.md](msix-packaging-guide.md) に MSIX パッケージング手順を追加。
- `src/TurtleAIQuartetHub.Package/Package.appxmanifest` と開発用 Packaging Project を追加。
- `src/TurtleAIQuartetHub.Package/Assets/` にパッケージ用ロゴ画像を追加。
- [../scripts/New-LocalMsixPackage.ps1](../scripts/New-LocalMsixPackage.ps1) にローカル確認用 MSIX 生成スクリプトを追加。
- [../.github/workflows/windows-build.yml](../.github/workflows/windows-build.yml) に Windows ビルドと公開準備チェックの CI を追加。
- [release-notes-draft.md](release-notes-draft.md) にリリースノート草案を追加。
- [../SUPPORT.md](../SUPPORT.md) にサポート案内草案を追加。
- [../assets/store/README.md](../assets/store/README.md) に Store 画像素材の要件と撮影前チェックを追加。
- [../scripts/Test-StoreReadiness.ps1](../scripts/Test-StoreReadiness.ps1) にローカル公開準備チェックを追加。
- [PRIVACY.md](../PRIVACY.md) にプライバシーポリシー草案を追加。
- [store-listing-draft.md](store-listing-draft.md) に Store 掲載文案の草案を追加。
- README から公開準備・プライバシー関連文書へ誘導する導線を追加。

## 現在の状態

| 項目 | 状態 | メモ |
|---|---|---|
| アプリ名・表示名 | 済み | `Turtle AI Code Quartet Hub` に統一済み。 |
| 管理者権限 | 済み | `asInvoker`。 |
| ユーザー設定保存先 | 済み | `%LOCALAPPDATA%` 優先。 |
| ライセンス同梱 | 済み | ビルド出力へ `LICENSE.txt` をコピー。 |
| CI | 済み | Windows上でビルドと公開準備チェックを実行。 |
| サポート文書 | 草案 | 連絡先とサポートURLは未確定。 |
| リリースノート | 草案 | 初回リリース候補として作成済み。 |
| プライバシーポリシー | 草案 | 連絡先、公開者名、公開URLは未確定。 |
| Store掲載文案 | 草案 | 提出前に商標表現と画像を最終確認。 |
| Store画像 | 未着手 | `assets/store/` に公開用PNGを配置する。 |
| MSIX Packaging Project | 開発用追加済み | Store提出前に Partner Center の Identity / Publisher へ差し替える。 |
| `.msixupload` / `.appxupload` | 未着手 | Partner Center 連携後に生成する。 |
| WACK実行 | 開発用MSIXでPASS | 最終Store提出パッケージでは再実行する。 |
| Partner Center登録 | 未着手 | ワークスペース外の作業。 |

## Store 申請時に明記すること

Partner Center の説明、プライバシーポリシー、サポート文書では次を明示してください。

- Visual Studio Code は別途インストールが必要。
- このアプリは独立したユーティリティであり、Microsoft、Visual Studio Code、GitHub、OpenAI、Anthropic の公式アプリ、提携アプリ、承認済みアプリではない。
- Win32 ウィンドウ API を使って VS Code ウィンドウを配置する。
- 状態表示のために、ローカルの VS Code ウィンドウタイトル、UI Automation 状態、一部の VS Code 拡張ログを読む。
- アプリ独自のテレメトリ、プロンプト、ソースコード、ワークスペース情報、ログを公開者へ送信しない。
- VS Code 内で使用する remote workspace、AI サービス、拡張機能は、それぞれのツール・サービスの仕様とプライバシーポリシーに従う。

## 一般公開前に残っている作業

- Partner Center の Identity / Publisher を `Package.appxmanifest` に反映する。
- Visual Studio または同等の MSIX 手順で `.msixupload` または `.appxupload` を生成する。
- Partner Center で最終アプリ名を予約する。
- プライバシーポリシーを公開 HTTPS URL でホストし、`PRIVACY.md` の連絡先を正式情報へ更新する。
- 個人のワークスペース名や機密情報が写らないスクリーンショットを用意する。
- サポート URL と公開者連絡先を用意する。
- 最終MSIX / `.msixupload` に対して Windows App Certification Kit を再実行する。
- クリーンな Windows ユーザープロファイルで、インストール、初回起動、アップグレード、アンインストールを確認する。
- VS Code 未インストール時、または `code` コマンドがない場合の表示と案内を確認する。
- GPL-3.0 での Store 配布時に、ソースコード提供、ライセンス文書同梱、著作権表示などの義務を満たす。

## 推奨するパッケージング方針

最初は手作業で package manifest を組むより、Visual Studio の Windows Application Packaging Project を使う方針を推奨します。WPF デスクトップアプリの MSIX 生成と Partner Center 申請用パッケージ作成の流れに合わせやすいためです。

パッケージには次を含めます。

- WPF アプリ本体。
- `config\turtle-ai-quartet-hub.example.json`。
- `LICENSE.txt`。
- Partner Center で構成したパッケージ ID と publisher 情報。

ユーザー設定はインストール先へ書き込まず、`%LOCALAPPDATA%\TurtleAIQuartetHub\config\turtle-ai-quartet-hub.json` を使います。

## ローカル確認コマンド

公開準備チェック:

```powershell
.\scripts\Test-StoreReadiness.ps1
```

自己完結版 publish まで含める場合:

```powershell
.\scripts\Test-StoreReadiness.ps1 -Publish
```

ローカル確認用 MSIX を生成する場合:

```powershell
.\scripts\New-LocalMsixPackage.ps1
```

署名とWACKまで含める場合:

```powershell
.\scripts\New-LocalMsixPackage.ps1 -Sign -RunWack
```

起動中アプリを上書きせずにビルド確認する場合:

```powershell
$out = Join-Path $env:TEMP 'vscode-square-build-check'
dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -o $out
```

フレームワーク依存版の publish:

```powershell
dotnet publish .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -c Release -o .\dist\turtle-ai-quartet-hub-framework
```

自己完結版の publish:

```powershell
dotnet publish .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\turtle-ai-quartet-hub
```
