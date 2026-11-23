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

## Building from Source

Craft Architect depends on [Franthropy](https://github.com/FranFkntastic/Franthropy) at the revision pinned in `eng/Franthropy.Version.props`. From a fresh checkout, resolve the pinned Franthropy source and build in local source mode:

```powershell
$franthropyRoot = powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\Resolve-Franthropy.ps1"
dotnet restore ".\FFXIV Craft Architect.sln" -p:FranthropySource=Local -p:FranthropyRoot="$franthropyRoot"
dotnet build ".\FFXIV Craft Architect.sln" --configuration Release --no-restore -p:FranthropySource=Local -p:FranthropyRoot="$franthropyRoot"
dotnet test ".\FFXIV Craft Architect.sln" --configuration Release --no-build --no-restore -p:FranthropySource=Local -p:FranthropyRoot="$franthropyRoot"
```

Alternatively, if you have a local Franthropy NuGet package feed (default `$(LOCALAPPDATA)/Franthropy/packages`, or pass `-p:FranthropyFeed=<dir>`), you can run `dotnet restore`, `dotnet build`, and `dotnet test` directly without `FranthropySource=Local`.

## License

See LICENSE file for details.
