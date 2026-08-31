# プライバシーポリシー草案

Turtle AI Code Quartet Hub は、4つの開発ワークスペースを A-D スロットとして起動し、各スロットに IDE（VS Code / Antigravity）または AI CLI（Codex / GitHub Copilot / Gemini / Grok / Claude）を割り当てて配置・管理するためのローカル Windows デスクトップユーティリティです。

この文書は公開前レビューと Microsoft Store 公開準備のための草案です。公開時には、連絡先、公開者名、サポートURL、正式な公開日を実際の情報に置き換えてください。

## 参照する情報

このアプリは、ユーザーの PC 上にあるローカル情報のみを参照します。

- Win32 API で取得できる管理対象ウィンドウ（IDE / CLI / 補助 Windows アプリ）のハンドル、タイトル、プロセス ID、位置とサイズ。
- ワークスペースパス、remote workspace URI、パネルタイトル、スロット割り当て、控えパネル、レイアウト設定。
- `inheritMainUserState` が有効な場合に、スロット別 user-data-dir へコピーされる軽量な VS Code ユーザー状態。例: 設定、キーバインド、スニペット。`globalStorage` や Chromium キャッシュはコピーしない。

AI 実行状態の推定、VS Code UI Automation 走査、VS Code 拡張ログの読み取りは行いません。

## 保存する情報

アプリの実行時データは、既定で次のローカルフォルダに保存します。

```text
%LOCALAPPDATA%\TurtleAIQuartetHub\
```

保存される可能性がある情報は次のとおりです。

- `slots.json` とパネル保存状態。
- スロット別 VS Code user-data-dir。
- 最後に確認できたワークスペースパスまたは remote workspace URI。
- 例外や遅い処理を記録するアプリ診断ログ。
- 任意のユーザー設定ファイル `%LOCALAPPDATA%\TurtleAIQuartetHub\config\turtle-ai-quartet-hub.json`（起動コマンドに加え、VS Code 共通のプロキシ URL を含む場合がある）。

## ネットワーク利用

Turtle AI Code Quartet Hub は、アプリ独自のテレメトリ、ワークスペース情報、プロンプト、ソースコード、VS Code ログ、利用状況分析を公開者へ送信しません。

このアプリは、ローカルパスまたは remote workspace URI を指定して VS Code を起動する場合があります。VS Code、VS Code 拡張機能、SSH、GitHub Copilot、Codex、その他のツールが行うネットワーク通信は、それぞれの製品・サービスの仕様とプライバシーポリシーに従います。

## 第三者提供

このアプリは、ユーザーデータを販売、共有、アップロード、第三者提供しません。

## ユーザーによる管理

アプリの実行時データを削除するには、次のフォルダを削除してください。

```text
%LOCALAPPDATA%\TurtleAIQuartetHub\
```

軽量な VS Code ユーザー状態のコピーを無効にするには、設定ファイルで次のように指定します。

```json
{
  "inheritMainUserState": false
}
```

## 連絡先

正式公開前に、この項目を公開者のサポート連絡先またはサポートURLに置き換えてください。
