# FFXIV Craft Architect - C# Edition

A C# reimplementation of the FFXIV Craft Architect, a crafting cost calculator for Final Fantasy XIV with live inventory tracking.

## Features

- **Crafting Cost Calculator** - Calculate total costs for multi-step recipes
- **Market Data Integration** - Real-time prices from Universalis
- **Recipe Analysis** - Hierarchical view of all materials needed
- **Live Inventory Tracking** - Real-time inventory sync via packet capture (requires admin)
- **Teamcraft Integration** - Import inventory from Teamcraft

## Requirements

- Windows 10/11 (x64)
- .NET 8 Runtime (if not using self-contained build)
- Administrator privileges (for Live Mode only)

## Building

```powershell
dotnet build -c Release
```

## Publishing (Single File)

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true
```

## Project Structure

```
src/
├── FFXIVCraftArchitect/       # Main WPF application
│   ├── Models/                # Data models
│   ├── Services/              # API services
│   ├── Inventory/             # Inventory management
│   ├── LiveMode/              # Packet capture
│   └── ViewModels/            # MVVM view models
└── FFXIVCraftArchitect.Tests/ # Unit tests
```

## Credits

- [Garland Tools](https://garlandtools.org/) - Item and recipe data
- [Universalis](https://universalis.app/) - Market board data
- [Deucalion](https://github.com/ffd/deucalion) - Packet capture DLL

## License

See LICENSE file for details.
