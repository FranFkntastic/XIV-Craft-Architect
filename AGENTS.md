# FFXIV Craft Architect - C# Edition

**Project:** C# fork of the Python-based FFXIV Craft Architect  
**Goal:** Replicate functionality with modern C#/.NET while maintaining code quality standards  
**Status:** Planning Phase

---

## Project Overview

This is a C# reimplementation of the FFXIV Craft Architect application, which calculates crafting costs in Final Fantasy XIV with live inventory tracking via DLL injection.

### Original Python Stack
- **GUI:** customtkinter (modern tkinter wrapper)
- **HTTP:** requests library
- **Windows API:** pywin32 + ctypes
- **Images:** Pillow (PIL)
- **Build:** PyInstaller

### Target C# Stack
- **Framework:** .NET 8 (LTS)
- **GUI:** WPF with Material Design or WinUI 3
- **HTTP:** HttpClient (built-in)
- **Windows API:** P/Invoke
- **Images:** System.Drawing or SkiaSharp
- **Build:** Single-file publish with AOT

---

## Architecture Mapping (Python → C#)

| Python Module | C# Namespace | Responsibility |
|--------------|--------------|----------------|
| `ffxiv_craft_architect.py` | `FFXIVCraftArchitect` | Main application, main window |
| `live_inventory.py` | `FFXIVCraftArchitect.LiveInventory` | Live inventory tracking manager |
| `packet_capture.py` | `FFXIVCraftArchitect.PacketCapture` | DLL injection, named pipe communication |
| `inventory_helpers.py` | `FFXIVCraftArchitect.Inventory` | Container mappings, utilities |
| `settings_manager.py` | `FFXIVCraftArchitect.Settings` | Settings persistence |

---

## File Structure Plan

```
FFXIV Craft Architect C# Edition/
├── src/
│   ├── FFXIVCraftArchitect/           # Main project
│   │   ├── FFXIVCraftArchitect.csproj
│   │   ├── App.xaml                   # Application entry
│   │   ├── App.xaml.cs
│   │   ├── MainWindow.xaml            # Main GUI
│   │   ├── MainWindow.xaml.cs
│   │   ├── Models/                    # Data models
│   │   │   ├── InventoryItem.cs
│   │   │   ├── Recipe.cs
│   │   │   ├── MarketListing.cs
│   │   │   └── WorldData.cs
│   │   ├── Services/                  # Business logic
│   │   │   ├── GarlandService.cs      # Garland Tools API
│   │   │   ├── UniversalisService.cs  # Universalis API
│   │   │   └── SettingsService.cs
│   │   ├── Inventory/                 # Inventory management
│   │   │   ├── InventoryManager.cs    # From live_inventory.py
│   │   │   ├── ContainerHelper.cs     # From inventory_helpers.py
│   │   │   └── InventoryCache.cs
│   │   ├── LiveMode/                  # Packet capture
│   │   │   ├── PacketCapture.cs       # From packet_capture.py
│   │   │   ├── DeucalionPipe.cs       # Named pipe handling
│   │   │   └── DllInjector.cs         # DLL injection
│   │   └── ViewModels/                # MVVM pattern
│   │       ├── MainViewModel.cs
│   │       └── InventoryViewModel.cs
│   └── FFXIVCraftArchitect.Tests/     # Unit tests
├── deucalion.dll                      # Injected DLL (same as Python)
├── settings.json                      # Settings file
└── AGENTS.md                          # This file
```

---

## Phase-by-Phase Implementation Plan

### Phase 1: Foundation ✅ (Complete)
- [x] Initialize Git repository
- [x] Create AGENTS.md with action plan
- [x] Create solution structure (.sln + projects)
- [x] Set up project dependencies
- [x] Create base Models (Item, Recipe, Container, etc.)
- [x] Initial commit with project skeleton

### Phase 2: Core Services
- [ ] Implement Garland Tools API service
- [ ] Implement Universalis API service
- [ ] Implement Settings service
- [ ] Add unit tests for services

### Phase 3: GUI (WPF)
- [ ] Create MainWindow layout
- [ ] Implement DC/World selector
- [ ] Implement item search with results
- [ ] Implement recipe tree view
- [ ] Implement market logistics cards
- [ ] Add dark theme styling

### Phase 4: Inventory Management
- [ ] Port container mappings (inventory_helpers.py)
- [ ] Implement InventoryManager class
- [ ] Implement inventory cache
- [ ] Add inventory viewer window

### Phase 5: Live Mode (DLL Injection)
- [ ] Port DLL injector (ctypes → P/Invoke)
- [ ] Implement named pipe communication
- [ ] Port packet parsing logic
- [ ] Implement LiveInventoryManager
- [ ] Add admin privilege handling

### Phase 6: Polish & Build
- [ ] Single-file publish configuration
- [ ] AOT compilation if feasible
- [ ] App manifest for UAC elevation
- [ ] Build scripts
- [ ] Documentation

---

## Code Quality Rules (Inherited from Python Edition)

### Syntax Validation
- **Always validate before building** - Use `dotnet build` to catch compilation errors
- **Check first lines** - Ensure using directives, namespace declarations are correct

### Naming Consistency
- Match the source system's terminology exactly
- If Python says "Live Mode", C# says "Live Mode" - not "Realtime Mode", not "Packet Mode"

### Error Prevention
- **64-bit compatibility** - Target x64 architecture (matches deucalion.dll)
- **Null safety** - Use nullable reference types (`<Nullable>enable</Nullable>`)
- **Async/await** - Use proper async patterns for API calls

### File Operations
- Prefer filesystem MCP tools over shell commands

---

## API Endpoints to Port

```csharp
// Garland Tools
const string GarlandSearch = "https://www.garlandtools.org/api/search.php?text={0}&lang=en";
const string GarlandItem = "https://www.garlandtools.org/db/doc/item/en/3/{0}.json";

// Universalis
const string UniversalisApi = "https://universalis.app/api/v2/{0}/{1}";
const string UniversalisWorlds = "https://universalis.app/api/v2/worlds";
const string UniversalisDataCenters = "https://universalis.app/api/v2/data-centers";
const string UniversalisMarketUrl = "https://universalis.app/market/{0}";

// XIVAPI (for icons)
const string XIVApiIcon = "https://xivapi.com/i/{0:D6}";

// Teamcraft
static readonly string TeamcraftInventory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "ffxiv-teamcraft",
    "inventory.json"
);
```

---

## Dependencies to Add

```xml
<!-- NuGet packages -->
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.x" />
<PackageReference Include="Microsoft.Extensions.Http" Version="8.0.x" />
<PackageReference Include="System.Net.Http.Json" Version="8.0.x" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.x" />

<!-- For UI -->
<!-- Option A: Material Design for WPF -->
<PackageReference Include="MaterialDesignThemes" Version="5.0.x" />

<!-- Option B: WPF UI (WinUI style for WPF) -->
<PackageReference Include="WPF-UI" Version="3.0.x" />
```

---

## Key Technical Challenges

### 1. DLL Injection in C#
- Python uses `ctypes` and `pywin32` for Windows API
- C# needs P/Invoke declarations for:
  - `OpenProcess`, `VirtualAllocEx`, `WriteProcessMemory`
  - `CreateRemoteThread`, `WaitForSingleObject`
  - `CreateToolhelp32Snapshot`, `Module32First/Next`

### 2. Named Pipes
- Python uses `win32pipe` and `win32file`
- C# has `System.IO.Pipes.NamedPipeClientStream` (built-in)

### 3. GUI Threading
- Python uses `threading` + `after()` for UI updates
- C# WPF uses `Dispatcher.Invoke()` or `IProgress<T>`

### 4. Admin Privilege Check
- Python: `ctypes.windll.shell32.IsUserAnAdmin()`
- C#: `WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)`

---

## Build Configuration

### Single-File Publish (PowerShell)
```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true
```

### Output
- **Target:** `FFXIV_Craft_Architect.exe`
- **Size:** ~30-50 MB (single-file, self-contained)
- **Requires:** .NET 8 runtime (if not self-contained)

---

## Next Steps

1. Create the solution structure
2. Set up the Models project
3. Implement API services (Garland + Universalis)
4. Create the WPF GUI shell
5. Port inventory management logic
6. Port packet capture (most complex)

---

*Last updated: 2026-01-31*
