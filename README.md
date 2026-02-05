# FFXIV Craft Architect
Yet another crafting cost calculator for Final Fantasy XIV.

## Features

- **Market Data Integration** - Real-time prices from Universalis
- **Actionable Plan Compilation** - Suggested world travel plans to get the best prices with minimum effort.

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
