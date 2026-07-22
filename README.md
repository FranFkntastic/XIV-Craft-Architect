# FFXIV Craft Architect
Yet another crafting cost calculator for Final Fantasy XIV.

The canonical app is the hosted Blazor web app — no installation required:

- Production: <https://xivcraftarchitect.com>
- Development slot: <https://dev.xivcraftarchitect.com>

## Features

- **Market Data Integration** - Real-time prices from Universalis
- **Automatic Plan Pricing** - Recipe plan builds refresh vendor and market prices as part of construction
- **Actionable Plan Compilation** - Suggested world travel plans to get the best prices with minimum effort.

## Requirements

- A modern browser, to use the hosted app
- .NET 10 SDK for development (pinned by `global.json`; application projects target .NET 8)
- Node.js 22 for browser and release-verification tooling
- a usecase, ideally

## Development verification

Run the same foundation checks used by CI before handing off a change:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\Assert-TruthfulTestSuite.ps1"
node --test ".\tools\TruthfulSuite\truthful-suite.test.mjs"
dotnet restore ".\FFXIV Craft Architect.sln" --locked-mode
node ".\tools\TruthfulSuite\check-dependencies.mjs"
dotnet format ".\FFXIV Craft Architect.sln" --verify-no-changes --no-restore
dotnet build ".\FFXIV Craft Architect.sln" --configuration Release --no-restore
dotnet test ".\FFXIV Craft Architect.sln" --configuration Release --no-build --no-restore
```

Web deployment publishes once, runs Chromium and Firefox against the hash-verified archive, and deploys those same bytes only after every required terminal outcome passes.

## License

See LICENSE file for details.
