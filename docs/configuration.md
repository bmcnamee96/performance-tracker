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

If the project later introduces real secrets, store them in Windows-managed secure storage or another local secret store rather than tracked config files.

