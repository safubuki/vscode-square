# Telemetry Notes

Phase 1 does not ingest Copilot, Codex, terminal, or OpenTelemetry events.

The next practical step is a VS Code helper extension that writes heartbeat and terminal execution state to a local status file. That provides a stable slot-to-window bridge before adding an OTLP receiver for Copilot Chat telemetry.

Current panel behavior:

- `AI 待機中` は検知結果ではなく、明示的な根拠が取れないときの既定表示。
- Window `Ready` / `Missing` は Win32 HWND の有無から判定する。
- ターミナルや agent の厳密な実行状態はまだ直接取得していない。
- エディタタブや拡張機能状態は Win32 だけでは安定取得できないため、必要なら VS Code helper extension を使う。
- 可視チャット UI の `思考中` / `考え中` / `Thinking` や明示的な停止ボタンは running hint として扱う。
- Codex fallback ではスロット別 `openai.chatgpt/Codex.log` を読み、起動時の `Conversation created` や単発の `thread-stream-state-changed` だけでは `Running` にしない。明示的な生成ログまたは直前の実行中状態の継続としてのみストリーム更新を使い、`commandExecution/requestApproval` は開始イベントが末尾から消えた後でも confirmation-waiting の根拠に使う。
- UI Automation の一時的な読み落としでは直前の `Running` を短時間保持し、Codex のログ静止は SSH 接続時の遅延を見込んで十数秒の猶予後に `Completed` へ切り替える。

Panel affordances:

- パネルは `標準` と `縮小` の 2 モードを持ち、縮小時は A-D の小枠で AI 状態を点灯・点滅し、簡易ボタン本体の枠はフォーカス表示に使う。
- 縮小ボタンとタスクバー JumpList の A-D 操作は、対象スロットのフォーカス切替とパネルの標準/縮小表示を連動させる。
- タスクバー右クリックメニューは単一起動の既存インスタンスにコマンド転送し、2個目のパネルは開かない。
- 管理中の VS Code ウィンドウには AI 状態に応じた発光フレームを重ね、遠目でも状態を把握できるようにする。

Recommended implementation order:

1. Add a VS Code helper extension that writes one heartbeat file per slot.
2. Report workspace title, active editor, installed extension IDs, and terminal shell execution start/end.
3. Map slot identity through the slot-specific `--user-data-dir` already used by the panel.
4. Add Copilot/OpenTelemetry ingestion only after the slot heartbeat path is stable.
5. Keep uncertain states as `Needs attention`, not `Waiting for confirmation`, unless the source is explicit.
