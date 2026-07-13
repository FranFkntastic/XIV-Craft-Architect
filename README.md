# FFXIV Craft Architect
Yet another crafting cost calculator for Final Fantasy XIV.

## Features

- **Market Data Integration** - Real-time prices from Universalis
- **Automatic Plan Pricing** - Recipe plan builds refresh vendor and market prices as part of construction
- **Actionable Plan Compilation** - Suggested world travel plans to get the best prices with minimum effort.

## Requirements

- Windows 10/11 (x64)
- .NET 8 Runtime (if not using self-contained build)
- .NET 10 SDK for development (pinned by `global.json`)
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
