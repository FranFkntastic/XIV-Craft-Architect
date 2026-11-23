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

```powershell
dotnet restore ".\FFXIV Craft Architect.sln"
dotnet build ".\FFXIV Craft Architect.sln" --configuration Release --no-restore
dotnet test ".\FFXIV Craft Architect.sln" --configuration Release --no-build --no-restore
```

If you are developing against a local checkout of [Franthropy](https://github.com/FranFkntastic/Franthropy), pass `-p:FranthropySource=Local -p:FranthropyRoot=<path-to-Franthropy>` to `dotnet restore` and `dotnet build`.

## License

See LICENSE file for details.
