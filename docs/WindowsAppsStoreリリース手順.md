# Windowsアプリ Microsoft Store リリース手順

この文書は、Windows デスクトップアプリを Microsoft Store へリリースするための実務手順です。
このリポジトリの `Turtle AI Code Quartet Hub` を前提にしつつ、別アプリを公開するときにも流用できる形で整理しています。

最終更新: 2026-04-26

## まず見るURL

| 用途 | URL | 補足 |
|---|---|---|
| Store 開発者登録の入口 | https://storedeveloper.microsoft.com/ | 新規登録はここから始める。直接 Partner Center に入るより安定しやすい。 |
| Partner Center ホーム | https://partner.microsoft.com/dashboard | 汎用ホーム。Store 用ワークスペースがない場合は `マイ アクセス` だけになることがある。 |
| Apps and games | https://partner.microsoft.com/en-us/dashboard/apps-and-games | アプリ名予約、製品作成、提出作業の本体。 |
| Windows overview | https://partner.microsoft.com/en-us/dashboard/windows/overview | 旧導線。現在は Apps and games へ寄せられている。 |
| Legal info / verification | https://partner.microsoft.com/dashboard/account/v3/organization/legalinfo | 本人確認、会社確認、審査状態の確認。 |
| Windows Developer Support | https://learn.microsoft.com/en-us/windows/apps/develop/support | Partner Center、Store、認定、開発の公式サポート導線。 |
| Store 開発者登録サポート | `storesupport@service.microsoft.com` | 新規オンボーディング問題向け。アカウント有効化、登録失敗、deactivated など。 |

## 全体像

| 状態 | フェーズ | 何をするか | 主な場所 |
|---|---|---|---|
| 一部済 | 1. アカウント準備 | Store 開発者アカウント登録、発行元名の決定、本人確認/会社確認 | `storedeveloper.microsoft.com` |
| 済 | 2. アプリ側準備 | アプリ名、保存先、ライセンス、README、プライバシー草案、サポート草案 | このワークスペース |
| ほぼ済 | 3. MSIX 準備 | Packaging Project、manifest、ロゴ、ローカル MSIX、WACK | Visual Studio / `scripts` |
| 未 | 4. 公開情報準備 | プライバシーURL、サポートURL、Store掲載文、スクリーンショット | Webサイト / `assets/store` |
| 未 | 5. Partner Center 設定 | アプリ名予約、Identity 取得、価格、市場、カテゴリ、年齢区分 | Partner Center |
| 未 | 6. 提出パッケージ作成 | `.msixupload` または `.appxupload` 作成 | Visual Studio |
| 未 | 7. 申請 | パッケージ、説明文、画像、URLを入力して認定へ提出 | Partner Center |
| 未 | 8. 公開後運用 | 審査結果確認、更新、レビュー、クラッシュ/利用状況確認 | Partner Center |

このワークスペース内の自動チェックでは、基本ファイル、MSIX関連ファイル、ローカルMSIX、開発用WACK、ビルドは PASS です。残りは主に次です。

- 公開用スクリーンショットを `assets/store/` に置く。
- `PRIVACY.md` と `SUPPORT.md` を正式情報へ差し替え、HTTPS URL で公開する。
- Partner Center で取得した正式な Identity / Publisher を `Package.appxmanifest` に反映する。
- Store提出用 `.msixupload` / `.appxupload` を作成する。

確認コマンド:

```powershell
.\scripts\Test-StoreReadiness.ps1
```

## 1. アカウント準備

### 1.1 個人か会社かを決める

| 種別 | 選ぶ基準 | 準備する情報 |
|---|---|---|
| Individual developer | 個人名または個人ブランドで公開する。小規模・趣味・個人開発向け。 | Microsoft アカウント、本人確認用の身分証、顔写真、連絡先、発行元表示名。 |
| Company account | 法人名、団体名、会社名で公開する。複数人管理や法人表示が必要。 | 会社名、会社住所、会社ドメインメール、D-U-N-S 番号または公的書類、担当者情報、サポート連絡先。 |

注意:

- Individual から Company への変更はできない扱いなので、法人名で公開したい場合は最初から Company を選ぶ。
- 発行元の表示名はアプリ名ではなく、開発者/ブランド名。今回の例では `Turtle Village` のような名前が該当する。
- Microsoft、Visual Studio Code、GitHub、OpenAI、Anthropic の公式・提携アプリに見える名前は避ける。

### 1.2 登録手順

1. https://storedeveloper.microsoft.com/ を開く。
2. `Get started` または `Get started for free` を選ぶ。
3. `Individual developer` または `Company account` を選ぶ。
4. Microsoft アカウントでサインインする。
5. 本人確認または会社確認を完了する。
6. 発行元の表示名を入力する。
7. 完了後、`Go to Partner Center dashboard` で Partner Center へ進む。
8. Apps and games が表示されるか確認する。

直接開く場合:

```text
https://partner.microsoft.com/en-us/dashboard/apps-and-games
```

## 2. アカウント登録で詰まったとき

### 2.1 `Access restricted` が出る

表示文によって意味が違います。

| 表示 | 意味 | 対応 |
|---|---|---|
| `you don't have permission to access this page` | そのページへの権限がない、またはワークスペース未有効。 | `storedeveloper.microsoft.com` から入り直す。Apps and games を直接開く。 |
| `Partner Center account has been deactivated` | Partner Center アカウントが非アクティブ化扱い。 | ユーザー側では直せない可能性が高い。サポートへ連絡する。 |
| ホームに `マイ アクセス` だけ出る | Partner Center には入れているが、Store公開用ワークスペースが見えていない。 | Apps and games を直接開く。反映待ちなら 30分から数時間待つ。 |

まず試すこと:

1. ブラウザの InPrivate / シークレットウィンドウで開く。
2. Microsoft 系サイトからいったんサインアウトする。
3. 登録に使った同じ Microsoft アカウントで https://storedeveloper.microsoft.com/ から入り直す。
4. 30分から数時間待って再確認する。
5. 直らなければサポートへ連絡する。

### 2.2 問い合わせ先

| 問題 | 問い合わせ先 |
|---|---|
| 新規開発者登録が完了しない | `storesupport@service.microsoft.com` |
| `deactivated` と表示される | `storesupport@service.microsoft.com` または Partner Center support |
| Apps and games が表示されない | Partner Center support。入れない場合は `storesupport@service.microsoft.com` |
| 会社確認、D-U-N-S、法人書類の問題 | Partner Center の Legal info / verification または Partner Center support |
| 認定不合格、提出後の審査問題 | Partner Center support / 認定レポートの指示 |
| アプリ開発そのものの技術問題 | Microsoft Q&A、Windows Developer Support、GitHub Discussions など |

Partner Center に入れる場合は、公式には Partner Center 内のサポート導線を使う。入れない場合や登録直後のオンボーディング問題では、`storesupport@service.microsoft.com` を使う。

問い合わせテンプレート:

```text
Subject: Partner Center account deactivated after Microsoft Store developer registration

Hello Microsoft Store Developer Support,

I registered a Microsoft Store developer account, but when I access Partner Center, I see this error:

"Access restricted. You don't have permission to access this page because your Partner Center account has been deactivated."

Publisher display name: Turtle Village
Account type: Individual developer
Microsoft account: [登録に使ったメールアドレス]
Registration date/time: [登録した日時]
Country/region: Japan

I cannot access the Apps & Games workspace or continue my app submission.
Could you please check my developer account status and reactivate or complete provisioning for Microsoft Store app publishing?

I have attached a screenshot of the error.

Thank you.
```

## 3. このワークスペースで済んでいること

| 状態 | 項目 | 場所 |
|---|---|---|
| 済 | アプリ表示名を `Turtle AI Code Quartet Hub` に統一 | プロジェクト設定 / README |
| 済 | 管理者権限を要求しない構成 | アプリ manifest |
| 済 | 実行時データを `%LOCALAPPDATA%\TurtleAIQuartetHub\` に保存 | アプリ実装 |
| 済 | ユーザー設定を `%LOCALAPPDATA%` 側から優先読み込み | アプリ実装 |
| 済 | `LICENSE.txt` をビルド出力に同梱 | `LICENSE.txt` |
| 済 | README に公開準備関連の導線を追加 | `README.md` |
| 済 | Store公開準備チェックリスト | `docs/store-readiness.md` |
| 済 | MSIXパッケージング手順 | `docs/msix-packaging-guide.md` |
| 済 | Store掲載文案草案 | `docs/store-listing-draft.md` |
| 済 | リリースノート草案 | `docs/release-notes-draft.md` |
| 済 | テレメトリ/ローカル情報の扱い | `docs/telemetry-notes.md` |
| 済 | プライバシーポリシー草案 | `PRIVACY.md` |
| 済 | サポート案内草案 | `SUPPORT.md` |
| 済 | Packaging Project | `src/TurtleAIQuartetHub.Package/TurtleAIQuartetHub.Package.wapproj` |
| 済 | 開発用 Package manifest | `src/TurtleAIQuartetHub.Package/Package.appxmanifest` |
| 済 | パッケージ用ロゴ | `src/TurtleAIQuartetHub.Package/Assets/` |
| 済 | ローカルMSIX生成スクリプト | `scripts/New-LocalMsixPackage.ps1` |
| 済 | Store準備チェックスクリプト | `scripts/Test-StoreReadiness.ps1` |
| 済 | 開発用MSIX生成 | `dist/msix-local/` |
| 済 | 開発用MSIXでWACK PASS | `dist/msix-local/wack-report.xml` |

## 4. このワークスペースで残っていること

| 優先 | 項目 | 作業内容 |
|---|---|---|
| 高 | プライバシーポリシー正式化 | `PRIVACY.md` の草案表現を消し、公開者名、連絡先、公開日、サポートURLを正式情報へ差し替える。 |
| 高 | プライバシーポリシーURL公開 | GitHub Pages、自分のWebサイト、公開ドキュメントサイトなど HTTPS URL で公開する。 |
| 高 | サポートページ正式化 | `SUPPORT.md` を正式な問い合わせ先、対応範囲、連絡先へ差し替える。 |
| 高 | サポートURL公開 | Partner Center に入力できる HTTPS URL を用意する。 |
| 高 | Storeスクリーンショット作成 | `assets/store/` に PNG を配置する。最低1枚、推奨4枚以上。 |
| 高 | Partner Center正式値反映 | 予約後に `Package.appxmanifest` の `Identity Name`、`Publisher`、`PublisherDisplayName`、`Version` を正式値へ変更する。 |
| 中 | Store掲載文の最終確認 | 商標表現、依存関係、プライバシー説明、サポート注記を最終化する。 |
| 中 | GPL-3.0対応確認 | ソースコード提供URL、ライセンス同梱、著作権表示を確認する。 |
| 中 | クリーン環境テスト | 新規Windowsユーザーでインストール、初回起動、更新、アンインストール、VS Code未導入時を確認する。 |

## 5. 公開前に用意する情報

### 5.1 Partner Center へ入力する基本情報

| 項目 | 今回の候補 | 備考 |
|---|---|---|
| 発行元表示名 | `Turtle Village` | アプリ名ではなく開発元/ブランド名。 |
| アプリ名 | `Turtle AI Code Quartet Hub` | Store内で一意である必要がある。 |
| カテゴリ | Developer tools または Utilities & tools | 最終的には Partner Center の選択肢に合わせる。 |
| 価格 | 無料または有料 | 初回公開は無料の方が審査後の切り分けが楽。 |
| 市場 | 日本、または全市場 | サポート可能範囲と説明言語で決める。 |
| 対象デバイス | Windows Desktop | このアプリは Windows デスクトップ向け。Xbox は対象外。 |
| 年齢区分 | 質問票に回答 | 暴力、課金、ユーザー投稿、位置情報などの有無を正確に答える。 |
| プライバシーURL | 未定 | HTTPSで公開。 |
| サポートURL | 未定 | HTTPSで公開。メールでも可能な場合があるが、URL推奨。 |
| Webサイト | 任意 | アプリ紹介ページがあるなら入力。 |

### 5.2 Store 掲載文で明記すること

`Turtle AI Code Quartet Hub` では次を明記する。

- Visual Studio Code は別途インストールが必要。
- このアプリは Microsoft、Visual Studio Code、GitHub、OpenAI、Anthropic の公式アプリ、提携アプリ、承認済みアプリではない。
- VS Code ウィンドウ配置のために Win32 API を使う。
- AI状態表示のために、ローカルの VS Code ウィンドウタイトル、UI Automation 状態、一部の VS Code 拡張ログを参照する。
- アプリ独自のテレメトリ、プロンプト、ソースコード、ワークスペース情報、ログを公開者へ送信しない。
- VS Code内で使用する remote workspace、AIサービス、拡張機能は、それぞれのツール/サービスの仕様とプライバシーポリシーに従う。

掲載文の草案は `docs/store-listing-draft.md` を使う。

## 6. Store画像を用意する

Store の提出では、最低1枚のスクリーンショットが必要です。推奨は4枚以上です。

| 項目 | 要件 |
|---|---|
| 形式 | PNG |
| Desktop サイズ | 1366 x 768 px 以上 |
| ファイルサイズ | 1ファイル 50 MB 以下 |
| 枚数 | 最低1枚、推奨4枚以上 |
| 保存先 | `assets/store/` |

推奨ファイル名:

```text
assets/store/desktop-01-main.png
assets/store/desktop-02-compact.png
assets/store/desktop-03-status-frame.png
assets/store/desktop-04-settings.png
```

撮影前チェック:

- ダミーのワークスペースを使う。
- 個人名、ユーザー名、ローカルパス、APIキー、社内情報、未公開コードを写さない。
- Store掲載文に書いていない機能を強調しない。
- MicrosoftやVS Codeの公式アプリに見える表現を避ける。
- 画面の文字が読める状態で撮る。

## 7. MSIX 提出パッケージを作る

### 7.1 Partner Center でアプリ名を予約する

1. https://partner.microsoft.com/en-us/dashboard/apps-and-games を開く。
2. `New product` を選ぶ。
3. `MSIX or PWA app` を選ぶ。
4. アプリ名 `Turtle AI Code Quartet Hub` を入力する。
5. `Check availability` を押す。
6. 利用可能なら `Reserve product name` を押す。

予約名は公開前でも取得できる。使われなければ一定期間後に解除されるため、公開準備を進めるタイミングで予約する。

### 7.2 manifest を正式値へ差し替える

対象ファイル:

```text
src/TurtleAIQuartetHub.Package/Package.appxmanifest
```

確認/差し替え項目:

| manifest 項目 | 内容 |
|---|---|
| `Identity Name` | Partner Center の Package/Identity Name |
| `Identity Publisher` | Partner Center の Publisher |
| `Identity Version` | `1.0.0.0` 形式。更新時は増やす。 |
| `PublisherDisplayName` | Storeに表示する公開者名 |
| `DisplayName` | `Turtle AI Code Quartet Hub` |
| `TargetDeviceFamily` | `Windows.Desktop` |
| `Capabilities` | 不要な権限を宣言しない。`runFullTrust` はWPF/Full Trust構成のため必要。 |

### 7.3 Visual Studio で `.msixupload` を生成する

1. Visual Studio で `TurtleAIQuartetHub.sln` を開く。
2. `src/TurtleAIQuartetHub.Package/TurtleAIQuartetHub.Package.wapproj` が含まれていることを確認する。
3. Packaging Project を右クリックする。
4. `Publish` から `Create App Packages` を選ぶ。
5. Microsoft Store 用パッケージを選ぶ。
6. Partner Center のアプリ名と関連付ける。
7. Release 構成でビルドする。
8. `.msixupload` または `.appxupload` を生成する。

Store提出では、Microsoft公式は Windows 10 以降向けに `.msixupload` または `.appxupload` のアップロードを推奨している。

### 7.4 ローカル確認

通常チェック:

```powershell
.\scripts\Test-StoreReadiness.ps1
```

自己完結 publish まで確認:

```powershell
.\scripts\Test-StoreReadiness.ps1 -Publish
```

ローカル MSIX 生成:

```powershell
.\scripts\New-LocalMsixPackage.ps1
```

署名と WACK まで確認:

```powershell
.\scripts\New-LocalMsixPackage.ps1 -Sign -RunWack
```

## 8. WACK と動作確認

Microsoft Store 提出時にはサーバー側の認定が実行されます。ただし、提出前にローカルでも Windows App Certification Kit で確認する。

確認項目:

- WACK が PASS する。
- クリーンな Windows ユーザーで初回起動できる。
- VS Code が未インストールのときに、適切な案内が出る。
- `code` コマンドが使えないときに、`codeCommand` の案内が出る。
- `%LOCALAPPDATA%\TurtleAIQuartetHub\` へ設定や状態が保存される。
- インストール先へユーザー設定を書き込まない。
- アンインストール後の残存データの扱いをサポート文書に書いている。
- アップデート時に既存設定が維持される。

代表的な WACK コマンド:

```powershell
appcert.exe reset
appcert.exe test -appxpackagepath ".\path\to\package.msix" -reportoutputpath ".\TestResults\wack-report.xml"
```

## 9. Partner Center で提出する

アプリ名予約後、アプリの概要ページで `Start submission` を選ぶ。提出には次のセクションがある。

| セクション | 入力内容 |
|---|---|
| Pricing and availability | 市場、公開範囲、公開日、価格、無料試用など。 |
| Properties | カテゴリ、プライバシーURL、Webサイト、サポート情報、システム要件など。 |
| Age ratings | 年齢区分質問票。 |
| Packages | `.msixupload` / `.appxupload` などをアップロード。 |
| Store listings | 説明文、短い説明、機能、スクリーンショット、ロゴ、キーワードなど。 |
| Submission options | 認定向けメモ、公開保留、制限付き機能の説明など。 |

### 9.1 `runFullTrust` の説明

このアプリは WPF の Full Trust デスクトップアプリで、manifest に `runFullTrust` を宣言しています。
提出画面で restricted capabilities の説明が求められた場合は、次のように説明する。

```text
This app is a packaged WPF desktop utility that runs as a full trust desktop application.
It uses Win32 window APIs to launch and arrange local Visual Studio Code windows, stores user configuration under the user's local app data folder, and reads local UI/window state to display coding session status.
The app does not upload source code, prompts, workspace paths, telemetry, or VS Code logs to the publisher.
```

日本語で補足を書く場合:

```text
このアプリは WPF のデスクトップユーティリティであり、ローカルの VS Code ウィンドウを起動・配置するために Full Trust が必要です。
Win32 ウィンドウ API、UI Automation、ローカル設定ファイルを使用します。
アプリ独自にソースコード、プロンプト、ワークスペース情報、ログを公開者へ送信しません。
```

### 9.2 認定向けメモ

認定担当者が確認しやすいように、Submission options の Notes へ次を書く。

- Visual Studio Code が必要であること。
- VS Code が未インストールの場合の挙動。
- `codeCommand` に `Code.exe` のパスを設定できること。
- AI状態はローカルの VS Code UI と一部ログから推定すること。
- ネットワーク送信をしないこと。
- 確認用の最小手順。

例:

```text
To test the main workflow, install Visual Studio Code, launch the app, and select Launch Quartet.
The app opens up to four VS Code windows and arranges them in a 2x2 layout.
If the code command is not available, set codeCommand to the Code.exe path in the local configuration file.
The app stores user data under %LOCALAPPDATA%\TurtleAIQuartetHub\ and does not upload telemetry, prompts, source code, workspace paths, or logs to the publisher.
```

## 10. 認定結果と公開

1. `Submit for certification` を押す。
2. Partner Center のステータスを確認する。
3. 認定レポートでエラーが出た場合は、該当箇所を修正して再提出する。
4. 公開保留を設定している場合は、認定通過後に `Publish now` または指定日時で公開する。
5. 公開後、Storeページ、インストール、初回起動を実機で確認する。

公開後に見るもの:

- Partner Center の Action Center。
- 認定結果メール。
- Storeページの表示。
- ダウンロード/インストール確認。
- クラッシュ、評価、レビュー。

## 11. 公開後の更新

更新時の流れ:

1. バージョンを上げる。
2. 変更点を `docs/release-notes-draft.md` にまとめる。
3. Store掲載の `What's new in this version` を書く。
4. 新しい `.msixupload` を作る。
5. Partner Center で対象アプリを開く。
6. `Start update` を選ぶ。
7. Packages と必要なメタデータを更新する。
8. 必要なら段階的ロールアウトを使う。
9. `Submit for certification` する。

注意:

- manifest の `Identity Version` は上げる。
- 追加の capability を増やした場合は、Submission options で説明が必要になる可能性がある。
- スクリーンショットと説明文が実際のUIと矛盾しないように更新する。

## 12. 別アプリをリリースするときの共通TODO

| 状態 | TODO | 内容 |
|---|---|---|
| 未 | アプリ名を決める | Storeで一意に予約できる名前にする。 |
| 未 | 発行元名を決める | 個人名、法人名、ブランド名のどれで出すか決める。 |
| 未 | ライセンスを決める | OSSならライセンス、商用なら利用規約を明確にする。 |
| 未 | データ保存先を整理する | インストール先にユーザーデータを書かず、`%LOCALAPPDATA%` 等を使う。 |
| 未 | プライバシー文書を書く | 収集、保存、送信、第三者提供、削除方法を書く。 |
| 未 | サポート文書を書く | 問い合わせ先、対応範囲、問題報告時の情報を書く。 |
| 未 | Store掲載文を書く | 説明、短い説明、機能、依存関係、注意事項を書く。 |
| 未 | 画像を用意する | スクリーンショット、ロゴ、必要なら追加アート。 |
| 未 | MSIX化する | WPF/WinFormsなら Packaging Project、WinUIなら Single-project MSIX など。 |
| 未 | WACK/ローカル確認 | インストール、起動、更新、アンインストールを確認する。 |
| 未 | Partner Center登録 | アプリ名予約、価格、カテゴリ、年齢区分、パッケージ、掲載情報を入力。 |
| 未 | 認定へ提出 | エラーが出たら認定レポートに沿って修正する。 |
| 未 | 公開後運用 | 更新、レビュー対応、問い合わせ対応、クラッシュ確認を行う。 |

## 13. 公式参考リンク

- Microsoft Store 公開概要: https://learn.microsoft.com/en-us/windows/apps/publish/
- 開発者アカウント作成: https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/open-a-developer-account
- 開発者アカウントFAQ: https://learn.microsoft.com/en-us/windows/apps/publish/faq/open-developer-account
- Partner Center workspaces: https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/partner-center-workspaces
- アプリ名予約: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/reserve-your-apps-name
- MSIX提出作成: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/create-app-submission
- パッケージアップロード: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages
- Store掲載情報: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info
- スクリーンショット/画像: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images
- プライバシー/サポート情報: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/support-info
- Submission options: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/manage-submission-options
- アカウント確認状態: https://learn.microsoft.com/en-us/partner-center/enroll/verification-responses
- Windows Developer Support: https://learn.microsoft.com/en-us/windows/apps/develop/support
- Microsoft Store Policies: https://learn.microsoft.com/en-us/windows/apps/publish/store-policies
- Visual StudioでMSIX作成: https://learn.microsoft.com/en-us/windows/msix/package/packaging-uwp-apps
- Windows App Certification Kit: https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit
- コード署名オプション: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options
