# Performance Tracker

`performance-tracker` is a Windows-only desktop app scaffold for diagnosing day-to-day PC stutters with low-overhead local telemetry, event detection, and a simple dashboard.

## Important Repo Notice

This repository is public, but it is intentionally **not licensed yet**. You can view the code, but reuse and redistribution are not granted until a license is added.

## Goals

- Keep telemetry local to the machine.
- Detect likely CPU, RAM, and disk-related slowdowns automatically.
- Surface plain-language event summaries instead of raw profiler noise.
- Stay lightweight enough to run continuously.

## Prerequisites

- Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2022 or a recent C#-capable editor

## Solution Layout

- `src/PerformanceTracker.Desktop`: WPF shell, tray integration, background host, app configuration
- `src/PerformanceTracker.Core`: contracts, settings, models, and event classification logic
- `src/PerformanceTracker.Infrastructure`: Windows collectors, foreground app tracking, SQLite persistence
- `tests/PerformanceTracker.Tests`: unit and storage tests

## Getting Started

```powershell
dotnet restore
dotnet build performance-tracker.sln
dotnet test performance-tracker.sln
```

The scaffold targets `.NET 10`. `appsettings.json` is for safe defaults only. Local overrides belong in `appsettings.Development.json`, which is ignored by git.

## Privacy and Local Data

- No cloud services are required.
- No `.env` file is used in the initial scaffold.
- Local databases, logs, and developer overrides are ignored by git.
- Personal telemetry exports and screenshots should be sanitized before sharing publicly.

See [docs/configuration.md](docs/configuration.md) for configuration and secret-handling guidance.

## Initial Roadmap

1. Harden the collector and add deeper Windows metrics.
2. Expand diagnosis beyond threshold-based heuristics.
3. Improve the dashboard, event timeline, and settings UX.
4. Add optional gaming-specific telemetry in a later phase.

## Recommended GitHub Settings After Repo Creation

- Protect `main`
- Require pull requests for changes
- Enable secret scanning and push protection if your GitHub plan supports it
- Disable direct pushes to `main`
- Require the Windows CI workflow to pass before merge
