# Store 掲載文案草案

この文書は Microsoft Store 申請前の掲載文案草案です。提出前に、ポリシー、商標表現、スクリーンショット、サポート URL、プライバシーポリシー URL を確認してください。

## アプリ名

Turtle AI Code Quartet Hub

## 短い説明

4 つの VS Code ウィンドウを配置し、AI コーディング状態を小さな Windows パネルから確認できます。

## 長い説明

Turtle AI Code Quartet Hub は、複数の AI 支援コーディングセッションを Visual Studio Code で扱う開発者向けのローカル Windows ユーティリティです。

最大 4 つの VS Code ウィンドウを起動して 2x2 に配置し、スロットごとのフォーカス切替、タイトル管理、ワークスペース状態の保存、視認性の高い状態フレーム表示を行えます。縮小モードでは、常に最前面の小さなパネルから各スロットへ素早くアクセスできます。

AI 状態は、表示中の VS Code UI と一部のローカル VS Code 拡張ログから推定します。このアプリは、テレメトリ、プロンプト、ソースコード、ワークスペースパス、ログを公開者へアップロードしません。

Visual Studio Code は別途インストールが必要です。AI 拡張機能や remote workspace 接続は、それぞれのツール・サービスにより提供されます。

## 主な機能

- 最大 4 つの VS Code ウィンドウを起動・配置。
- A-D スロット、パネルタイトル、ワークスペースパス、控えパネルを保持。
- 小さな常時最前面パネルとして使える縮小モード。
- AI 実行中、完了、確認待ちなどを遠目でも見分けやすい状態フレーム。
- Win32、UI Automation、一部の VS Code ログを使ったローカル状態推定。
- 複数セッションを分けやすいスロット別 VS Code user-data-dir。

## 依存関係

- Windows。
- Visual Studio Code。
- VS Code コマンドラインランチャー `code`、または `Code.exe` への設定済みパス。
- フレームワーク依存版では .NET Desktop Runtime。自己完結版では別途 .NET インストール不要。

## プライバシー要約

このアプリは、状態推定のためにローカルの VS Code ウィンドウ情報と一部の VS Code ログを処理します。設定と状態は `%LOCALAPPDATA%\TurtleAIQuartetHub\` に保存します。アプリ独自のテレメトリやユーザーのプロジェクトデータを公開者へ送信しません。

プライバシーポリシー草案: [PRIVACY.md](../PRIVACY.md)

## サポート注記

- このアプリは Microsoft、Visual Studio Code、GitHub、OpenAI、Anthropic の公式アプリ、提携アプリ、承認済みアプリではありません。
- VS Code が未インストール、または `code` コマンドが使えない場合は、`codeCommand` に `Code.exe` のパスを設定してください。
- Remote SSH や AI サービスの挙動は、VS Code とインストール済み拡張機能側で管理されます。

サポート案内草案: [SUPPORT.md](../SUPPORT.md)
リリースノート草案: [release-notes-draft.md](release-notes-draft.md)

## 画像素材メモ

公開用スクリーンショットは [assets/store/README.md](../assets/store/README.md) のチェックに沿って用意してください。
最低1枚、推奨4枚以上です。個人情報、ローカルパス、未公開コード、APIキー、社内情報が写らないダミーワークスペースで撮影します。

パッケージ用ロゴ画像は `src/TurtleAIQuartetHub.Package/Assets/` に配置済みです。Storeのスクリーンショットとは別物として扱ってください。
