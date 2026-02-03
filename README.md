# FFXIV Craft Architect
A crafting cost calculator for Final Fantasy XIV with live inventory tracking.

## Features

- **Crafting Cost Calculator** - Calculate total costs for multi-step recipes
- **Market Data Integration** - Real-time prices from Universalis
- **Recipe Analysis** - Hierarchical view of all materials needed

## Requirements

- Windows 10/11 (x64)
- .NET 8 Runtime (if not using self-contained build)

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

## License

See LICENSE file for details.
