# Configuration and Secret Handling

This project is designed to avoid tracked secrets by default.

## Current Configuration Model

- `src/PerformanceTracker.Desktop/appsettings.json` contains safe, non-secret defaults.
- `appsettings.Development.json` is supported for local overrides and is ignored by git.
- No `.env` file is required for the current local-only MVP scaffold.

## Public Repo Rules

- Never commit API keys, tokens, machine names, or exported personal telemetry.
- Never commit local SQLite databases or logs.
- Never store secrets in `README.md`, issue reports, screenshots, or sample JSON.

## Local Overrides

Create `src/PerformanceTracker.Desktop/appsettings.Development.json` only on your machine when you need local tuning. Example categories for local-only overrides:

- sample interval experimentation
- retention tuning
- temporary diagnostic logging changes

## Collector Telemetry Toggles

- `Collector.EnableExtendedWindowsMetrics` defaults to `true` so deeper disk, GPU, and network fields can be populated when the collector starts emitting them.
- `Collector.EnableGamingTelemetry` defaults to `false` to preserve the current low-overhead behavior until game-focused capture is explicitly enabled.
- `Collector.CapturePerProcessGpuMetrics` and `Collector.CaptureNetworkLatencyMetrics` default to `false` because they may require more expensive collection paths on some systems.
- Additional collector thresholds for GPU load, disk queue/response time, network latency, FPS, and frame/input/render latency are safe tracked defaults only. Tune them locally if your machine needs different alert thresholds.

## Local Schema Evolution

- The SQLite repository now applies additive column migrations on startup for new telemetry fields instead of requiring you to delete an existing local database.
- New telemetry columns are nullable or have conservative defaults so older rows remain readable after the schema expands.

If the project later introduces real secrets, store them in Windows-managed secure storage or another local secret store rather than tracked config files.
