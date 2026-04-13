# Telemetry Notes

Phase 1 does not ingest Copilot, Codex, terminal, or OpenTelemetry events.

The next practical step is a VS Code helper extension that writes heartbeat and terminal execution state to a local status file. That provides a stable slot-to-window bridge before adding an OTLP receiver for Copilot Chat telemetry.

