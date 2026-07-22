# Quartermaster

Quartermaster is a standalone Dalamud plugin for browsing owner-scoped player and retainer stock, maintaining target quantities, and running reviewed retainer retrieval or elemental quick-deposit operations. Its internal abbreviation, `RQ`, expands to **Retainer Quartermaster**.

## Features

- Captures complete retainer inventories when a retainer closes and stores them atomically.
- Browses player stock, cached retainer stock, and retainer market listings with Franthropy filters.
- Maintains a target-quantity retrieval plan with notes and enabled state.
- Plans against cached evidence, then verifies exact live slots before moving items.
- Refreshes retainer caches through AutoRetainer when available.
- Exposes versioned, owner-scoped IPC snapshots and shortage submissions with explicit review or automatic-execution intent.
- Persists operation status, transition history, and transfer receipts.

Use `/rq` to open the workbench. Manual retrieval plans, deposits, and shortage requests without automatic-execution intent require an explicit action in Quartermaster.

## Build

Quartermaster expects the public `Franthropy` checkout beside this repository.

```powershell
dotnet build Quartermaster.slnx -c Release
dotnet test Quartermaster.slnx -c Release --no-build
```

## Development deployment

```powershell
.\tools\Deploy-DevPlugin.ps1 -Target "C:\path\to\devPlugins\RQ"
```

Pass `-SkipBuild` to deploy an existing Release build.
