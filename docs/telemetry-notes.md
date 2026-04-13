# Telemetry Notes

Phase 1 does not ingest Copilot, Codex, terminal, or OpenTelemetry events.

The next practical step is a VS Code helper extension that writes heartbeat and terminal execution state to a local status file. That provides a stable slot-to-window bridge before adding an OTLP receiver for Copilot Chat telemetry.

Current panel behavior:

- `AI 未取得` is a placeholder, not a detected state.
- Window `Ready` / `Missing` comes from Win32 HWND checks.
- Terminal or agent execution state is not read yet.

Recommended implementation order:

1. Add a VS Code helper extension that writes one heartbeat file per slot.
2. Report workspace title, active editor, installed extension IDs, and terminal shell execution start/end.
3. Map slot identity through the slot-specific `--user-data-dir` already used by the panel.
4. Add Copilot/OpenTelemetry ingestion only after the slot heartbeat path is stable.
5. Keep uncertain states as `Needs attention?`, not `Waiting for confirmation`, unless the source is explicit.
