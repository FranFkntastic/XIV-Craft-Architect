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
- a usecase, ideally

## Development verification

Run the same foundation checks used by CI before handing off a change:

```powershell
dotnet restore ".\FFXIV Craft Architect.sln"
dotnet list ".\FFXIV Craft Architect.sln" package --vulnerable --include-transitive --no-restore
dotnet format ".\FFXIV Craft Architect.sln" --verify-no-changes --no-restore
dotnet build ".\FFXIV Craft Architect.sln" --configuration Release --no-restore
dotnet test ".\src\FFXIV Craft Architect.Tests\FFXIV Craft Architect.Tests.csproj" --configuration Release --no-build --no-restore
```

## License

See LICENSE file for details.
